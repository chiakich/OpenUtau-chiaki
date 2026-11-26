using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Agent {
    // One open project that an agent edits step by step: a request in, a reply out, both JSON objects.
    // The same requests work on the app's open project and on a project held by a process without a window.
    public sealed class AgentSession : ICmdSubscriber, IDisposable {
        public const int ProtocolVersion = 1;
        const int WholeNote = 1920;

        static readonly JsonSerializerOptions Compact = new JsonSerializerOptions {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        static readonly JsonDocumentOptions Lenient = new JsonDocumentOptions {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        readonly IAgentHost host;
        readonly object gate = new object();
        readonly Dictionary<string, Func<Request, JsonObject>> ops;
        // Ids follow the note object, so they survive edits and undo, and end when the project is reloaded.
        readonly ConditionalWeakTable<UNote, object> ids = new ConditionalWeakTable<UNote, object>();
        readonly Dictionary<int, WeakReference<UNote>> notesById = new Dictionary<int, WeakReference<UNote>>();
        int nextId = 1;
        int executed;
        // Counts edits, the user's included. Not the document's own revision, which also moves when the phonemizer answers.
        long revision;
        // The revision the current reply describes, taken in the same turn of the main thread as its content.
        long? seen;

        public bool ShutdownRequested { get; private set; }

        public AgentSession(IAgentHost host) {
            this.host = host;
            ops = new Dictionary<string, Func<Request, JsonObject>> {
                ["help"] = Help,
                ["status"] = Status,
                ["new"] = New,
                ["open"] = Open,
                ["save"] = Save,
                ["get"] = Get,
                ["add_notes"] = AddNotes,
                ["edit_notes"] = EditNotes,
                ["delete_notes"] = DeleteNotes,
                ["set_curve"] = SetCurve,
                ["set_tempo"] = SetTempo,
                ["set_track"] = SetTrack,
                ["load_pitch"] = LoadPitch,
                ["render"] = Render,
                ["undo"] = r => UndoRedo(r, undo: true),
                ["redo"] = r => UndoRedo(r, undo: false),
                ["singers"] = Singers,
                ["phonemizers"] = Phonemizers,
                ["expressions"] = Expressions,
                ["shutdown"] = Shutdown,
            };
            host.Invoke(() => DocManager.Inst.AddSubscriber(this));
        }

        public void OnNext(UCommand cmd, bool isUndo) {
            if (!(cmd is UNotification) || cmd is LoadProjectNotification) {
                revision++;
            }
        }

        public void Dispose() {
            host.Invoke(() => DocManager.Inst.RemoveSubscriber(this));
        }

        // Never throws: a failure is a reply with ok false.
        public string HandleLine(string line) {
            string op = string.Empty;
            JsonNode? id = null;
            JsonObject? result = null;
            AgentException? error = null;
            lock (gate) {
                try {
                    using var document = JsonDocument.Parse(line, Lenient);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) {
                        throw Invalid("A request is a JSON object with an \"op\".");
                    }
                    if (root.TryGetProperty("id", out var idValue)) {
                        id = JsonNode.Parse(idValue.GetRawText());
                    }
                    if (!root.TryGetProperty("op", out var opValue) || opValue.ValueKind != JsonValueKind.String) {
                        throw Invalid("A request needs an \"op\". Send {\"op\":\"help\"} for the list.");
                    }
                    op = opValue.GetString()!;
                    if (!ops.TryGetValue(op, out var handler)) {
                        throw Invalid($"Unknown op \"{op}\". Send {{\"op\":\"help\"}} for the list.");
                    }
                    seen = null;
                    currentOp = op;
                    result = handler(new Request(root));
                    if (Rephonemized.Contains(op)) {
                        result["phoneme_errors"] = CountPhonemeErrors();
                    }
                } catch (JsonException e) {
                    error = Invalid($"The request is not valid JSON: {e.Message}");
                } catch (AgentException e) {
                    error = e;
                } catch (Exception e) {
                    error = new AgentException("internal", e.ToString());
                }

                var reply = new JsonObject { ["ok"] = error == null, ["op"] = op };
                if (id != null) {
                    reply["id"] = id;
                }
                if (error != null) {
                    reply["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message };
                }
                try {
                    reply["revision"] = error == null && seen.HasValue ? seen.Value : host.Invoke(() => revision);
                } catch (Exception) {
                    // The host is going away; the reply still says what happened.
                }
                if (result != null) {
                    foreach (var pair in result.ToList()) {
                        result.Remove(pair.Key);
                        reply[pair.Key] = pair.Value;
                    }
                }
                AddMessages(reply);
                return reply.ToJsonString(Compact);
            }
        }

        // Ops after which the lyrics are sung differently: their reply says whether they still can be.
        static readonly HashSet<string> Rephonemized = new HashSet<string> {
            "new", "open", "add_notes", "edit_notes", "delete_notes", "set_track", "set_tempo", "undo", "redo",
        };

        static List<string> PhonemeErrors(UProject project) {
            return project.parts.OfType<UVoicePart>()
                .SelectMany(p => p.phonemes.Where(ph => ph.Error)
                    .Select(ph => $"\"{ph.Parent?.lyric}\" at tick {p.position + ph.position}: {ph.ErrorException?.Message ?? "phoneme error"}"))
                .ToList();
        }

        const int MaxTrialLyrics = 40;

        int CountPhonemeErrors() {
            var project = host.Invoke(() => DocManager.Inst.Project);
            if (project == null) {
                return 0;
            }
            AgentOps.WaitPhonemized(host, project);
            var errors = host.Invoke(() => PhonemeErrors(project));
            if (errors.Count > 0) {
                lock (host.Warnings) {
                    string used = host.Invoke(() => string.Join(", ", project.tracks.Select(t => t.Phonemizer?.GetType().FullName).Where(n => n != null).Distinct()));
                    host.Warnings.Add($"{errors.Count} phoneme(s) have errors, e.g. {string.Join("; ", errors.Take(3))}. "
                        + $"Reading the notes shows them all. The phonemizer in use is {used}: if it is not the one for the language of the lyrics, "
                        + "search the phonemizers list with \"singer\" and \"query\" (the language), and add \"lyrics\" to see how the matches sing those words.");
                }
            }
            return errors.Count;
        }

        // Parts whose notes changed after their pitch was written: the curve still follows the old notes.
        readonly HashSet<UVoicePart> stalePitch = new HashSet<UVoicePart>();
        // This session's own edits still on top of the undo stack, by the revision each one left.
        readonly Stack<(long Revision, string Op)> mine = new Stack<(long, string)>();
        string currentOp = string.Empty;

        void NotesChanged(UVoicePart part) {
            if (!(part.curves.FirstOrDefault(c => c.abbr == Format.Ustx.PITD)?.IsEmpty ?? true)) {
                stalePitch.Add(part);
            }
        }

        // One request is one revision, however many commands it took.
        void Settle(long before) {
            if (revision > before) {
                revision = before + 1;
            }
            seen = revision;
        }

        void AddMessages(JsonObject reply) {
            var warnings = new JsonArray();
            if (reply["warnings"] is JsonArray own) {
                reply.Remove("warnings");
                foreach (var item in own.ToList()) {
                    own.Remove(item);
                    warnings.Add(item);
                }
            }
            lock (host.Warnings) {
                foreach (string warning in host.Warnings) {
                    warnings.Add(warning);
                }
                host.Warnings.Clear();
            }
            reply["warnings"] = warnings;
            var diagnostics = host.TakeDiagnostics().AsEnumerable();
            if (reply["phoneme_errors"] is JsonValue count && count.TryGetValue<int>(out int left) && left == 0) {
                // Logged by a pass inside the request that a later one replaced.
                diagnostics = diagnostics.Where(d => !d.StartsWith("phonemizer error"));
            }
            reply["diagnostics"] = new JsonArray(diagnostics.Select(d => (JsonNode)d).ToArray());
        }

        // ---- Requests ------------------------------------------------------------------------------

        sealed class Request {
            readonly JsonElement root;
            readonly HashSet<string> read = new HashSet<string> { "op", "id" };

            public Request(JsonElement root) {
                this.root = root;
            }

            public long? ExpectRevision {
                get {
                    if (!TryGet("expect_revision", out var value)) {
                        return null;
                    }
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long expected)) {
                        throw Invalid("\"expect_revision\" must be a whole number: the revision of an earlier reply.");
                    }
                    return expected;
                }
            }

            public bool TryGet(string name, out JsonElement value) {
                read.Add(name);
                return root.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
            }

            public JsonElement Required(string name, string what) {
                if (!TryGet(name, out var value)) {
                    throw Invalid($"\"{name}\" is required: {what}.");
                }
                return value;
            }

            public string? Str(string name) {
                if (!TryGet(name, out var value)) {
                    return null;
                }
                if (value.ValueKind != JsonValueKind.String) {
                    throw Invalid($"\"{name}\" must be a string.");
                }
                return value.GetString();
            }

            public int? Int(string name) {
                if (!TryGet(name, out var value)) {
                    return null;
                }
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result)) {
                    throw Invalid($"\"{name}\" must be a whole number.");
                }
                return result;
            }

            public double? Number(string name) {
                if (!TryGet(name, out var value)) {
                    return null;
                }
                if (value.ValueKind != JsonValueKind.Number) {
                    throw Invalid($"\"{name}\" must be a number.");
                }
                return value.GetDouble();
            }

            public bool Flag(string name) {
                if (!TryGet(name, out var value)) {
                    return false;
                }
                if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False) {
                    throw Invalid($"\"{name}\" must be true or false.");
                }
                return value.GetBoolean();
            }

            // After every field was read: a misspelt field must not pass for an absent one.
            // An op that edits also takes "expect_revision", read once it holds the main thread.
            public void Done(bool edits = false) {
                if (edits) {
                    read.Add("expect_revision");
                }
                foreach (var property in root.EnumerateObject()) {
                    if (!read.Contains(property.Name)) {
                        throw Invalid($"Unknown field \"{property.Name}\" for this op.");
                    }
                }
            }
        }

        static AgentException Invalid(string message) => new AgentException("invalid_request", message, AgentException.ExitUsage);

        // ---- Document access -----------------------------------------------------------------------

        static UProject RequireProject() {
            var project = DocManager.Inst.Project;
            if (project == null) {
                throw new AgentException("no_project", "No project is open. Use the new or open op first.");
            }
            return project;
        }

        int IdOf(UNote note) {
            if (ids.TryGetValue(note, out var boxed)) {
                return (int)boxed;
            }
            int id = nextId++;
            ids.Add(note, id);
            notesById[id] = new WeakReference<UNote>(note);
            return id;
        }

        (UVoicePart Part, UNote Note) Find(UProject project, int id) {
            if (notesById.TryGetValue(id, out var reference) && reference.TryGetTarget(out var note)) {
                foreach (var part in project.parts.OfType<UVoicePart>()) {
                    if (part.notes.Contains(note)) {
                        return (part, note);
                    }
                }
            }
            throw new AgentException("note_not_found",
                $"No note has id {id}. Ids come from get and add_notes, and end when the note is deleted or the project is reloaded.");
        }

        // Main thread.
        void CheckRevision(long? expected) {
            if (expected.HasValue && expected.Value != revision) {
                throw new AgentException("conflict",
                    $"The project is at revision {revision}, not {expected.Value}: it changed since you read it. Read it again.");
            }
        }

        // open, save and render name files. The app's working folder is not the agent's, so there a path must be whole.
        string FullPath(string path, string field) {
            if (host.Kind == "gui" && !Path.IsPathFullyQualified(path)) {
                throw Invalid($"\"{field}\" must be an absolute path: the app does not run in your folder.");
            }
            return Path.GetFullPath(path);
        }

        // The part an edit goes to when the request names none: the one the user has open, else the first.
        UVoicePart TargetPart(UProject project, int? partIndex, int? trackIndex, bool create) {
            if (partIndex.HasValue) {
                if (partIndex.Value < 0 || partIndex.Value >= project.parts.Count || !(project.parts[partIndex.Value] is UVoicePart named)) {
                    throw Invalid($"The project has no voice part {partIndex.Value}. The status op lists the parts.");
                }
                return named;
            }
            if (!trackIndex.HasValue && host.UiState(IdOf)?["part"] is JsonValue open
                    && open.TryGetValue(out int openIndex) && openIndex >= 0 && openIndex < project.parts.Count
                    && project.parts[openIndex] is UVoicePart openPart) {
                return openPart;
            }
            int track = trackIndex ?? 0;
            if (track < 0 || track >= project.tracks.Count) {
                throw Invalid($"The project has no track {track}.");
            }
            var first = project.parts.OfType<UVoicePart>()
                .Where(p => !trackIndex.HasValue || p.trackNo == track)
                .OrderBy(p => p.trackNo).ThenBy(p => p.position)
                .FirstOrDefault();
            if (first != null) {
                return first;
            }
            if (!create) {
                throw new AgentException("no_part", "The project has no voice part yet. add_notes makes one.");
            }
            var part = new UVoicePart { name = "Part", trackNo = track, position = 0, Duration = WholeNote * 4 };
            Exec(new AddPartCommand(project, part));
            return part;
        }

        // One request is one step of undo.
        T Edit<T>(Request request, Func<UProject, T> action) {
            long? expected = request.ExpectRevision;
            return host.Invoke(() => {
                // A project made here stands for the empty one the agent saw, so the revision stays.
                long seenBefore = revision;
                host.EnsureProject();
                revision = seenBefore;
                var project = RequireProject();
                var doc = DocManager.Inst;
                CheckRevision(expected);
                if (doc.HasOpenUndoGroup) {
                    throw new AgentException("busy", "An edit is in progress in the app. Try again when it is done.");
                }
                executed = 0;
                long before = revision;
                doc.StartUndoGroup();
                try {
                    T result = action(project);
                    doc.EndUndoGroup();
                    Settle(before);
                    if (revision > before) {
                        mine.Push((revision, currentOp));
                    }
                    return result;
                } catch {
                    if (executed > 0) {
                        doc.RollBackUndoGroup();
                    }
                    // Rolling back empties the group but leaves it open.
                    doc.EndUndoGroup();
                    // A refused request leaves the project as it was, so the revision too.
                    revision = before;
                    throw;
                }
            });
        }

        void Exec(UCommand command) {
            executed++;
            DocManager.Inst.ExecuteCmd(command);
        }

        void FitPart(UProject project, UVoicePart part) {
            int needed = part.GetMinDurTick(project);
            if (needed > part.Duration) {
                Exec(new ResizeVoicePartCommand(project, part, needed - part.Duration, false));
            }
        }

        JsonObject NoteJson(UProject project, UVoicePart part, UNote note) {
            return new JsonObject {
                ["id"] = IdOf(note),
                ["part"] = project.parts.IndexOf(part),
                ["lyric"] = note.lyric,
                ["tone"] = note.tone,
                ["tone_name"] = MusicMath.GetToneName(note.tone),
                ["tick"] = part.position + note.position,
                ["end_tick"] = part.position + note.End,
            };
        }

        static JsonArray Overlaps(UProject project, IEnumerable<UVoicePart> parts) {
            var warnings = new JsonArray();
            foreach (var part in parts.Distinct()) {
                UNote? previous = null;
                foreach (var note in part.notes) {
                    if (previous != null && previous.End > note.position) {
                        warnings.Add($"Part {project.parts.IndexOf(part)}: \"{previous.lyric}\" at tick {part.position + previous.position} "
                            + $"overlaps \"{note.lyric}\" at tick {part.position + note.position}.");
                    }
                    previous = note;
                }
            }
            return warnings;
        }

        // ---- Ops -----------------------------------------------------------------------------------

        JsonObject Status(Request request) {
            request.Done();
            return host.Invoke(StatusOnMain);
        }

        JsonObject StatusOnMain() {
            seen = revision;
            var doc = DocManager.Inst;
            var project = doc.Project;
            var result = new JsonObject {
                ["protocol"] = ProtocolVersion,
                ["host"] = host.Kind,
                ["seed"] = host.Seed,
            };
            if (project == null) {
                result["project"] = null;
                return result;
            }
            var info = ProjectReport.Project(project);
            info["saved"] = doc.ChangesSaved && !string.IsNullOrEmpty(project.FilePath);
            result["project"] = info;
            result["tracks"] = ProjectReport.Tracks(project);
            var parts = new JsonArray();
            for (int i = 0; i < project.parts.Count; i++) {
                if (project.parts[i] is UVoicePart part) {
                    parts.Add(new JsonObject {
                        ["index"] = i,
                        ["track"] = part.trackNo,
                        ["name"] = part.name,
                        ["tick"] = part.position,
                        ["end_tick"] = part.End,
                        ["notes"] = part.notes.Count,
                    });
                }
            }
            result["parts"] = parts;
            result["can_undo"] = doc.GetUndoState(out _);
            result["can_redo"] = doc.GetRedoState(out _);
            result["ui"] = host.UiState(IdOf);
            return result;
        }

        void GuardUnsaved(bool discard, long? expected) {
            bool unsaved = host.Invoke(() => {
                CheckRevision(expected);
                return DocManager.Inst.Project != null && !DocManager.Inst.ChangesSaved;
            });
            if (host.Kind == "gui" && unsaved && !discard) {
                throw new AgentException("unsaved_changes",
                    "The project open in the app has unsaved changes. Save it, or pass \"discard_changes\": true if the user agreed to lose them.");
            }
        }

        // Another project: nothing remembered about the last one applies.
        JsonObject Fresh() {
            stalePitch.Clear();
            mine.Clear();
            return StatusOnMain();
        }

        JsonObject New(Request request) {
            bool discard = request.Flag("discard_changes");
            var score = request.Required("score", "{bpm, singer, phonemizer, notes?, curves?, name?, time_signature?, start?}");
            long? expected = request.ExpectRevision;
            request.Done();
            GuardUnsaved(discard, expected);
            var project = ScoreBuilder.Build(score, host, null, allowEmpty: true);
            host.LoadProject(project);
            return host.Invoke(Fresh);
        }

        JsonObject Open(Request request) {
            bool discard = request.Flag("discard_changes");
            string path = request.Str("path") ?? throw Invalid("\"path\" is required: the project file.");
            long? expected = request.ExpectRevision;
            request.Done();
            path = FullPath(path, "path");
            if (!File.Exists(path)) {
                throw new AgentException("file_not_found", $"Project file not found: {path}");
            }
            GuardUnsaved(discard, expected);
            try {
                host.OpenProject(path);
            } catch (Exception e) when (!(e is AgentException)) {
                throw new AgentException("invalid_project", $"Failed to open {path}: {e.Message}", inner: e);
            }
            return host.Invoke(Fresh);
        }

        JsonObject Save(Request request) {
            string? path = request.Str("path");
            request.Done();
            if (path != null) {
                path = FullPath(path, "path");
            }
            return host.Invoke(() => {
                var project = RequireProject();
                path ??= project.FilePath;
                if (string.IsNullOrEmpty(path)) {
                    throw Invalid("\"path\" is required: this project has not been saved before.");
                }
                host.SaveProject(project, path);
                return new JsonObject { ["path"] = project.FilePath };
            });
        }

        JsonObject Get(Request request) {
            int? partIndex = request.Int("part");
            int from = request.Int("from_tick") ?? int.MinValue;
            int to = request.Int("to_tick") ?? int.MaxValue;
            string detail = request.Str("detail") ?? "notes";
            if (detail != "notes" && detail != "phonemes" && detail != "phrases") {
                throw Invalid("\"detail\" must be notes, phonemes or phrases.");
            }
            var curveNames = new List<string>();
            if (request.TryGet("curves", out var curvesValue)) {
                if (curvesValue.ValueKind != JsonValueKind.Array || curvesValue.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String)) {
                    throw Invalid("\"curves\" must be an array of expression abbreviations.");
                }
                curveNames.AddRange(curvesValue.EnumerateArray().Select(c => c.GetString()!.ToLowerInvariant()));
            }
            request.Done();

            var project = host.Invoke(RequireProject);
            AgentOps.WaitPhonemized(host, project);
            return host.Invoke(() => {
                seen = revision;
                var errors = new JsonArray();
                var parts = ProjectReport.Parts(project, errors, IdOf);
                var kept = new JsonArray();
                foreach (var node in parts.ToList()) {
                    var part = (JsonObject)node!;
                    parts.Remove(node);
                    int index = part["index"]!.GetValue<int>();
                    if (partIndex.HasValue && index != partIndex.Value) {
                        continue;
                    }
                    Trim(part, detail, from, to);
                    if (curveNames.Count > 0) {
                        part["curves"] = Curves((UVoicePart)project.parts[index], curveNames, from, to);
                    }
                    kept.Add(part);
                }
                if (partIndex.HasValue && kept.Count == 0) {
                    throw Invalid($"The project has no voice part {partIndex.Value}.");
                }
                return new JsonObject {
                    ["project"] = ProjectReport.Project(project),
                    ["parts"] = kept,
                    ["errors"] = errors,
                };
            });
        }

        // Cuts a part's report down to a tick range and a level of detail: a whole song is too much to read at once.
        static void Trim(JsonObject part, string detail, int from, int to) {
            bool InRange(JsonNode? node) {
                int tick = node!["tick"]!.GetValue<int>();
                int end = node["end_tick"]?.GetValue<int>() ?? tick + 1;
                return end > from && tick < to;
            }
            void Filter(string name) {
                var array = (JsonArray)part[name]!;
                for (int i = array.Count - 1; i >= 0; i--) {
                    if (!InRange(array[i])) {
                        array.RemoveAt(i);
                    }
                }
            }
            var phonemes = (JsonArray)part["phonemes"]!;
            if (detail == "notes") {
                var names = phonemes.Select(p => p!["phoneme"]!.GetValue<string>()).ToArray();
                foreach (var note in (JsonArray)part["notes"]!) {
                    var indexes = ((JsonArray)note!["phonemes"]!).Select(i => i!.GetValue<int>()).ToArray();
                    note["phonemes"] = new JsonArray(indexes.Select(i => (JsonNode)names[i]).ToArray());
                }
                part.Remove("phonemes");
                part.Remove("phrases");
            } else {
                Filter("phonemes");
                if (detail == "phonemes") {
                    part.Remove("phrases");
                } else {
                    Filter("phrases");
                }
            }
            Filter("notes");
        }

        static JsonObject Curves(UVoicePart part, List<string> names, int from, int to) {
            var result = new JsonObject();
            foreach (string abbr in names) {
                var points = new JsonArray();
                var curve = part.curves.FirstOrDefault(c => c.abbr == abbr);
                for (int i = 0; curve != null && i < curve.xs.Count; i++) {
                    int tick = part.position + curve.xs[i];
                    if (tick >= from && tick <= to) {
                        points.Add(new JsonArray(tick, curve.ys[i]));
                    }
                }
                result[abbr] = points;
            }
            return result;
        }

        JsonObject AddNotes(Request request) {
            int? partIndex = request.Int("part");
            int? trackIndex = request.Int("track");
            int? at = request.Int("at");
            int? after = request.Int("after");
            var notesValue = request.Required("notes", "an array of [lyric, tone, duration], {\"rest\": duration} or {lyric, tone, duration, tick}");
            request.Done(edits: true);
            if (at.HasValue && after.HasValue) {
                throw Invalid("Give \"at\" or \"after\", not both.");
            }
            return Edit(request, project => {
                UVoicePart part;
                int cursor;
                if (after.HasValue) {
                    var found = Find(project, after.Value);
                    part = partIndex.HasValue ? TargetPart(project, partIndex, trackIndex, true) : found.Part;
                    cursor = found.Part.position + found.Note.End;
                } else {
                    part = TargetPart(project, partIndex, trackIndex, true);
                    cursor = at ?? (part.notes.Count > 0
                        ? part.position + part.notes.Max(n => n.End)
                        : Math.Max(part.position, WholeNote));
                }
                var notes = ScoreBuilder.ParseNotes(project, notesValue, ref cursor);
                if (notes.Count == 0) {
                    throw Invalid("\"notes\" has no notes.");
                }
                foreach (var note in notes) {
                    note.position -= part.position;
                    if (note.position < 0) {
                        throw Invalid($"\"{note.lyric}\" would start before its part, which begins at tick {part.position}.");
                    }
                }
                Exec(new AddNoteCommand(part, notes));
                NotesChanged(part);
                FitPart(project, part);
                host.ShowPart(part, part.position + notes[0].position);
                return new JsonObject {
                    ["notes"] = new JsonArray(notes.Select(n => (JsonNode)NoteJson(project, part, n)).ToArray()),
                    ["next_tick"] = cursor,
                    ["warnings"] = Overlaps(project, new[] { part }),
                };
            });
        }

        JsonObject EditNotes(Request request) {
            var notesValue = request.Required("notes", "an array of {id, lyric?, tone?, tick?, duration?}");
            request.Done(edits: true);
            if (notesValue.ValueKind != JsonValueKind.Array || notesValue.GetArrayLength() == 0) {
                throw Invalid("\"notes\" must be a non-empty array of {id, lyric?, tone?, tick?, duration?}.");
            }
            return Edit(request, project => {
                var edited = new List<(UVoicePart, UNote)>();
                int index = 0;
                foreach (var item in notesValue.EnumerateArray()) {
                    string where = $"notes[{index++}]";
                    if (item.ValueKind != JsonValueKind.Object) {
                        throw Invalid($"{where} must be an object with an id.");
                    }
                    foreach (var property in item.EnumerateObject()) {
                        if (property.Name != "id" && property.Name != "lyric" && property.Name != "tone"
                                && property.Name != "tick" && property.Name != "duration") {
                            throw Invalid($"{where}: unknown field \"{property.Name}\".");
                        }
                    }
                    if (!item.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out int id)) {
                        throw Invalid($"{where} needs the note's id.");
                    }
                    var (part, note) = Find(project, id);
                    if (item.TryGetProperty("lyric", out var lyric)) {
                        if (lyric.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(lyric.GetString())) {
                            throw Invalid($"{where}: lyric must be a non-empty string.");
                        }
                        if (lyric.GetString() != note.lyric) {
                            Exec(new ChangeNoteLyricCommand(part, note, lyric.GetString()!));
                        }
                    }
                    int deltaTone = item.TryGetProperty("tone", out var tone) ? ScoreBuilder.Tone(tone, where) - note.tone : 0;
                    int deltaTick = 0;
                    if (item.TryGetProperty("tick", out var tick)) {
                        if (!tick.TryGetInt32(out int target)) {
                            throw Invalid($"{where}: tick must be a whole number.");
                        }
                        deltaTick = target - (part.position + note.position);
                        if (note.position + deltaTick < 0) {
                            throw Invalid($"{where}: the note would start before its part, which begins at tick {part.position}.");
                        }
                    }
                    if (deltaTone != 0 || deltaTick != 0) {
                        Exec(new MoveNoteCommand(part, note, deltaTick, deltaTone));
                    }
                    if (item.TryGetProperty("duration", out var duration)) {
                        int delta = ScoreBuilder.Duration(duration, where) - note.duration;
                        if (delta != 0) {
                            Exec(new ResizeNoteCommand(part, note, delta));
                        }
                    }
                    edited.Add((part, note));
                }
                foreach (var part in edited.Select(e => e.Item1).Distinct()) {
                    NotesChanged(part);
                    FitPart(project, part);
                }
                return new JsonObject {
                    ["notes"] = new JsonArray(edited.Select(e => (JsonNode)NoteJson(project, e.Item1, e.Item2)).ToArray()),
                    ["warnings"] = Overlaps(project, edited.Select(e => e.Item1)),
                };
            });
        }

        JsonObject DeleteNotes(Request request) {
            var idsValue = request.Required("ids", "an array of note ids");
            request.Done(edits: true);
            if (idsValue.ValueKind != JsonValueKind.Array || idsValue.GetArrayLength() == 0
                    || idsValue.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.Number || !i.TryGetInt32(out _))) {
                throw Invalid("\"ids\" must be a non-empty array of note ids.");
            }
            var wanted = idsValue.EnumerateArray().Select(i => i.GetInt32()).Distinct().ToArray();
            return Edit(request, project => {
                var found = wanted.Select(id => Find(project, id)).ToList();
                foreach (var group in found.GroupBy(f => f.Part)) {
                    Exec(new RemoveNoteCommand(group.Key, group.Select(f => f.Note).ToList()));
                    NotesChanged(group.Key);
                }
                return new JsonObject { ["deleted"] = found.Count };
            });
        }

        JsonObject SetCurve(Request request) {
            int? partIndex = request.Int("part");
            int? trackIndex = request.Int("track");
            string abbr = (request.Str("abbr") ?? throw Invalid("\"abbr\" is required: the expression, such as pitd or genc.")).ToLowerInvariant();
            bool hasDefault = request.TryGet("default", out var defaultValue);
            bool resetDefault = request.Flag("reset_default");
            bool hasPoints = request.TryGet("points", out var pointsValue);
            bool clear = request.Flag("clear");
            int? from = request.Int("from_tick");
            int? to = request.Int("to_tick");
            request.Done(edits: true);
            int modes = (hasDefault ? 1 : 0) + (resetDefault ? 1 : 0) + (hasPoints ? 1 : 0) + (clear ? 1 : 0);
            if (modes != 1) {
                throw Invalid("Give exactly one of \"points\" ([[tick, value], ...]), \"clear\": true, \"default\": value or \"reset_default\": true.");
            }
            return Edit(request, project => {
                if (hasDefault || resetDefault) {
                    int track = trackIndex ?? (partIndex.HasValue ? TargetPart(project, partIndex, null, false).trackNo : 0);
                    if (track < 0 || track >= project.tracks.Count) {
                        throw Invalid($"The project has no track {track}.");
                    }
                    var descriptor = ScoreBuilder.CurveDescriptor(project, project.tracks[track], abbr);
                    float? value = null;
                    if (hasDefault) {
                        if (defaultValue.ValueKind != JsonValueKind.Number) {
                            throw Invalid("\"default\" must be a number.");
                        }
                        ScoreBuilder.CheckRange(descriptor, defaultValue.GetDouble());
                        value = (float)defaultValue.GetDouble();
                    }
                    Exec(new SetTrackExpressionDefaultCommand(project, project.tracks[track], abbr, value));
                    return new JsonObject { ["abbr"] = abbr, ["track"] = track, ["default"] = value };
                }

                var part = TargetPart(project, partIndex, trackIndex, false);
                var curveDescriptor = ScoreBuilder.CurveDescriptor(project, project.tracks[part.trackNo], abbr);
                if (clear) {
                    if (from.HasValue || to.HasValue) {
                        Exec(new EraseCurveCommand(project, part, abbr,
                            (from ?? part.position) - part.position, (to ?? part.End) - part.position));
                    } else {
                        Exec(new ClearCurveCommand(part, abbr));
                    }
                } else {
                    if (pointsValue.ValueKind != JsonValueKind.Array || pointsValue.GetArrayLength() == 0) {
                        throw Invalid("\"points\" must be a non-empty array of [tick, value].");
                    }
                    var points = new SortedDictionary<int, int>();
                    foreach (var point in pointsValue.EnumerateArray()) {
                        if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2
                                || point[0].ValueKind != JsonValueKind.Number || point[1].ValueKind != JsonValueKind.Number) {
                            throw Invalid("Each point must be [tick, value].");
                        }
                        ScoreBuilder.CheckRange(curveDescriptor, point[1].GetDouble());
                        if (!(point[0].GetDouble() >= part.position && point[0].GetDouble() <= part.End)) {
                            throw Invalid($"Tick {point[0].GetRawText()} is outside its part, {part.position}..{part.End}.");
                        }
                        points[(int)Math.Round(point[0].GetDouble()) - part.position] = (int)Math.Round(point[1].GetDouble());
                    }
                    // As dragging the pen through the points: a line between each two, replacing what was there.
                    var previous = points.First();
                    foreach (var point in points) {
                        Exec(new SetCurveCommand(project, part, abbr, point.Key, point.Value, previous.Key, previous.Value));
                        previous = point;
                    }
                    if (part.curves.All(c => c.abbr != abbr)) {
                        throw new AgentException("curve_unavailable", $"This project cannot draw \"{abbr}\" as a curve.");
                    }
                }
                return new JsonObject {
                    ["abbr"] = abbr,
                    ["part"] = project.parts.IndexOf(part),
                    // Not the number sent: points snap to the curve's grid and those on a straight line go.
                    ["stored_points"] = part.curves.FirstOrDefault(c => c.abbr == abbr)?.xs.Count ?? 0,
                };
            });
        }

        JsonObject SetTempo(Request request) {
            double bpm = request.Number("bpm") ?? throw Invalid("\"bpm\" is required.");
            request.Done(edits: true);
            if (bpm < 10 || bpm > 1000) {
                throw Invalid("\"bpm\" must be between 10 and 1000.");
            }
            return Edit(request, project => {
                Exec(new BpmCommand(project, bpm));
                return new JsonObject { ["project"] = ProjectReport.Project(project) };
            });
        }

        JsonObject SetTrack(Request request) {
            int trackIndex = request.Int("track") ?? 0;
            string? singerName = request.Str("singer");
            string? phonemizerName = request.Str("phonemizer");
            request.Done(edits: true);
            if (singerName == null && phonemizerName == null) {
                throw Invalid("Give \"singer\", \"phonemizer\" or both.");
            }
            var singer = singerName == null ? null : Queries.ResolveSinger(host, singerName);
            return Edit(request, project => {
                if (trackIndex < 0 || trackIndex >= project.tracks.Count) {
                    throw Invalid($"The project has no track {trackIndex}.");
                }
                var track = project.tracks[trackIndex];
                var factory = phonemizerName == null ? null : ScoreBuilder.FindPhonemizer(phonemizerName, singer ?? track.Singer, host);
                if (singer != null) {
                    Exec(new TrackChangeSingerCommand(project, track, singer));
                    // As picking a singer in the app: the phonemizer the user last used with it, else the singer's own.
                    if (factory == null) {
                        factory = SingerPhonemizer(singer);
                        if (factory != null && factory.type != track.Phonemizer?.GetType()) {
                            host.Warnings.Add($"The phonemizer is now {factory.Id}, the one this singer was last used with. Pass \"phonemizer\" to choose.");
                        }
                    }
                    if (!Renderers.GetSupportedRenderers(singer.SingerType).Contains(track.RendererSettings?.renderer)) {
                        Exec(new TrackChangeRenderSettingCommand(project, track, new URenderSettings {
                            renderer = Renderers.GetDefaultRenderer(singer.SingerType),
                        }));
                    }
                }
                if (factory != null) {
                    Exec(new TrackChangePhonemizerCommand(project, track, factory.Create()));
                }
                return new JsonObject { ["tracks"] = ProjectReport.Tracks(project) };
            });
        }

        static OpenUtau.Api.PhonemizerFactory? SingerPhonemizer(USinger singer) {
            var names = new List<string?>();
            if (!string.IsNullOrEmpty(singer.Id) && Util.Preferences.Default.SingerPhonemizers.TryGetValue(singer.Id, out var used)) {
                names.Add(used);
            }
            names.Add(singer.DefaultPhonemizer);
            return names.Where(n => !string.IsNullOrEmpty(n)).Select(n => OpenUtau.Api.PhonemizerFactory.Get(n!)).FirstOrDefault(f => f != null);
        }

        JsonObject LoadPitch(Request request) {
            int? partIndex = request.Int("part");
            string mode = request.Str("mode") ?? AgentOps.PitchUntuned;
            long? expected = request.ExpectRevision;
            request.Done();
            if (mode != AgentOps.PitchUntuned && mode != AgentOps.PitchAll) {
                throw Invalid("\"mode\" must be untuned or all.");
            }
            var (project, part) = host.Invoke(() => {
                var p = RequireProject();
                CheckRevision(expected);
                return (p, partIndex.HasValue ? TargetPart(p, partIndex, null, false) : null);
            });
            AgentOps.CheckSingers(host, project);
            AgentOps.WaitPhonemized(host, project);
            long before = host.Invoke(() => revision);
            AgentOps.LoadRenderedPitch(host, project, mode, part);
            bool written = host.Invoke(() => {
                Settle(before);
                if (mode == AgentOps.PitchAll) {
                    stalePitch.RemoveWhere(p => part == null || p == part);
                }
                if (revision > before) {
                    // One undo step per part: only a single part can be named when undone.
                    if (part != null || project.parts.OfType<UVoicePart>().Count() == 1) {
                        mine.Push((revision, currentOp));
                    } else {
                        mine.Clear();
                    }
                }
                return revision > before;
            });
            if (!written && mode == AgentOps.PitchUntuned) {
                host.Warnings.Add("Nothing was written: every phrase already has a pitch curve. \"mode\": \"all\" rewrites them, replacing pitch drawn by hand too.");
            }
            return new JsonObject { ["mode"] = mode, ["written"] = written };
        }

        JsonObject Render(Request request) {
            string outPath = request.Str("out") ?? throw Invalid("\"out\" is required: the wav file to write.");
            int from = request.Int("from_tick") ?? 0;
            int? toGiven = request.Int("to_tick");
            int? trackGiven = request.Int("track");
            bool mixdown = request.Flag("mixdown");
            bool allowErrors = request.Flag("allow_errors");
            request.Done();
            if (from < 0 || (toGiven.HasValue && toGiven.Value <= from)) {
                throw Invalid("\"from_tick\" must not be negative, and \"to_tick\" must come after it.");
            }
            int to = toGiven ?? -1;
            int track = trackGiven ?? -1;
            outPath = FullPath(outPath, "out");
            var project = host.Invoke(RequireProject);
            if (trackGiven.HasValue && (track < 0 || track >= host.Invoke(() => project.tracks.Count))) {
                throw Invalid($"The project has no track {track}.");
            }
            AgentOps.CheckSingers(host, project);
            AgentOps.WaitPhonemized(host, project);
            var errors = host.Invoke(() => {
                seen = revision;
                // Without the app nothing fills in the pitch as the user's live pitch does.
                if (host.Kind == "headless") {
                    for (int i = 0; i < project.parts.Count; i++) {
                        if (project.parts[i] is UVoicePart p && p.notes.Count > 0 && (track < 0 || p.trackNo == track)
                            && project.tracks[p.trackNo].Singer is DiffSinger.DiffSingerSinger ds && ds.HasPitchPredictor
                            && (p.curves.FirstOrDefault(c => c.abbr == Format.Ustx.PITD)?.IsEmpty ?? true)) {
                            host.Warnings.Add($"Part {i} has no pitch curve: its notes are sung at their plain pitch, which sounds mechanical. "
                                + "load_pitch first writes the pitch this singer's model predicts.");
                        }
                    }
                }
                foreach (var p in stalePitch.Where(p => project.parts.Contains(p) && (track < 0 || p.trackNo == track))) {
                    host.Warnings.Add($"Part {project.parts.IndexOf(p)}: notes changed after its pitch curve was written, so the pitch still follows the old notes. "
                        + "load_pitch with \"mode\": \"all\" rewrites it (pitch drawn by hand goes too, so draw vibrato after).");
                }
                return PhonemeErrors(project);
            });
            if (errors.Count > 0 && !allowErrors) {
                throw new AgentException("phoneme_errors",
                    $"{errors.Count} phoneme(s) have errors, e.g. {string.Join("; ", errors.Take(3))}. Fix the notes, or pass \"allow_errors\": true.");
            }
            var outputs = AgentOps.Render(host, project, outPath, mixdown, track, from, to);
            return new JsonObject {
                ["outputs"] = OutputsJson(outputs),
                ["from_tick"] = from,
                ["to_tick"] = to < 0 ? null : to,
                ["from_ms"] = ProjectReport.Round(host.Invoke(() => project.timeAxis.TickPosToMsPos(from))),
            };
        }

        public static JsonArray OutputsJson(IEnumerable<RenderOutput> outputs) {
            return new JsonArray(outputs.Select(o => (JsonNode)new JsonObject {
                ["track"] = o.Track < 0 ? null : o.Track,
                ["path"] = o.Path,
                ["channels"] = o.Channels,
                ["sample_rate"] = o.SampleRate,
                ["bits"] = o.BitsPerSample,
                ["samples"] = o.Samples,
                ["duration_ms"] = ProjectReport.Round(o.Samples * 1000.0 / o.SampleRate),
                ["sha256"] = o.Sha256,
            }).ToArray());
        }

        JsonObject UndoRedo(Request request, bool undo) {
            long? expected = request.ExpectRevision;
            request.Done();
            return host.Invoke(() => {
                RequireProject();
                CheckRevision(expected);
                var doc = DocManager.Inst;
                if (doc.HasOpenUndoGroup) {
                    throw new AgentException("busy", "An edit is in progress in the app. Try again when it is done.");
                }
                if (!(undo ? doc.GetUndoState(out _) : doc.GetRedoState(out _))) {
                    throw new AgentException(undo ? "nothing_to_undo" : "nothing_to_redo", undo ? "There is nothing to undo." : "There is nothing to redo.");
                }
                long before = revision;
                // Null for a change that is not this session's latest: the user's, one redone, or a load_pitch over several parts.
                string? undone = null;
                if (undo) {
                    if (mine.Count > 0 && mine.Peek().Revision == before) {
                        undone = mine.Pop().Op;
                    }
                    doc.Undo();
                } else {
                    doc.Redo();
                }
                Settle(before);
                if (undone != null && mine.Count > 0) {
                    // The project is back at what that edit left.
                    var top = mine.Pop();
                    mine.Push((revision, top.Op));
                }
                var result = new JsonObject { ["can_undo"] = doc.GetUndoState(out _), ["can_redo"] = doc.GetRedoState(out _) };
                if (undo) {
                    result["undone"] = undone;
                }
                return result;
            });
        }

        JsonObject Singers(Request request) {
            request.Done();
            host.SearchInstalledSingers();
            return new JsonObject { ["singers"] = host.Invoke(Queries.Singers) };
        }

        JsonObject Phonemizers(Request request) {
            string? singerName = request.Str("singer");
            string? query = request.Str("query");
            var lyrics = new List<string>();
            if (request.TryGet("lyrics", out var lyricsValue)) {
                if (lyricsValue.ValueKind != JsonValueKind.Array || lyricsValue.EnumerateArray().Any(l => l.ValueKind != JsonValueKind.String)) {
                    throw Invalid("\"lyrics\" must be an array of strings, one per note.");
                }
                lyrics.AddRange(lyricsValue.EnumerateArray().Select(l => l.GetString()!).Take(MaxTrialLyrics));
            }
            request.Done();
            var singer = singerName == null ? null : Queries.ResolveSinger(host, singerName);
            if (lyrics.Count > 0 && (singer == null || !singer.Found)) {
                throw Invalid("\"lyrics\" needs a \"singer\": the phonemizers are tried with its dictionaries and phonemes.");
            }
            var factories = Queries.PhonemizersOf(singer);
            if (!string.IsNullOrWhiteSpace(query)) {
                factories = PhonemizerTrial.Search(factories, query).ToArray();
            }
            var rows = factories.Select(f => (Factory: f, Row: Queries.PhonemizerRow(f))).ToList();
            if (lyrics.Count > 0) {
                foreach (var row in rows) {
                    row.Row["trial"] = PhonemizerTrial.Try(row.Factory, singer!, lyrics);
                    row.Row.Remove("author");
                    row.Row.Remove("language");
                }
                // Stable: phonemizers that sing as much keep the order of the search.
                rows = rows.OrderByDescending(r => r.Row["trial"]!["sung"]!.GetValue<int>()).ToList();
            }
            return new JsonObject { ["singer_id"] = singer?.Id, ["phonemizers"] = new JsonArray(rows.Select(r => (JsonNode)r.Row).ToArray()) };
        }

        JsonObject Expressions(Request request) {
            string? singerName = request.Str("singer");
            int? trackIndex = request.Int("track");
            request.Done();
            USinger singer;
            if (singerName != null) {
                singer = Queries.ResolveSinger(host, singerName);
            } else {
                singer = host.Invoke(() => {
                    var project = RequireProject();
                    int track = trackIndex ?? 0;
                    if (track < 0 || track >= project.tracks.Count || project.tracks[track].Singer == null || !project.tracks[track].Singer.Found) {
                        throw Invalid($"Track {track} has no singer. Give \"singer\".");
                    }
                    return project.tracks[track].Singer;
                });
            }
            return Queries.Expressions(host, singer);
        }

        JsonObject Shutdown(Request request) {
            request.Done();
            if (host.Kind == "gui") {
                throw new AgentException("unsupported", "The app is closed by its user, not by an agent.");
            }
            ShutdownRequested = true;
            return new JsonObject();
        }

        JsonObject Help(Request request) {
            request.Done();
            var help = Describe();
            help["host"] = host.Kind;
            return help;
        }

        // The protocol in its own words; also what other front ends (MCP) show.
        public static JsonObject Describe() {
            JsonObject Op(string summary, params string[] fields) {
                return new JsonObject { ["summary"] = summary, ["fields"] = new JsonArray(fields.Select(f => (JsonNode)f).ToArray()) };
            }
            return new JsonObject {
                ["protocol"] = ProtocolVersion,
                ["format"] = "One JSON object per line in, one per line out. A request is {\"op\": name, ...fields}; \"id\" is echoed. "
                    + "A reply has ok, revision, warnings, diagnostics, and error {code, message} when ok is false.",
                ["units"] = "Ticks: 480 per quarter note, counted from the start of the project. Tones: MIDI numbers or names like \"C4\". "
                    + "Durations: ticks, or a fraction of a whole note like \"1/8\", dotted with a trailing \".\".",
                ["revisions"] = "\"revision\" goes up by one with every request that changes the project, and with the user's changes in the app. Every op that changes the project takes \"expect_revision\" and fails with code conflict if the project moved on. One such request is one step of undo; load_pitch is one step per part.",
                ["tips"] = "Refer to singers and phonemizers by id: names and tags are not unique. "
                    + "A singer's default phonemizer is often not the one for the language of the lyrics: before writing notes, search phonemizers with singer and query (the language). "
                    + "After an op that changes what is sung, the reply's phoneme_errors counts the phonemes that cannot be sung, and a warning shows the first; get lists them all. "
                    + "A lyric the phonemizer does not know can spell its phonemes: \"word[p h o n e m e s]\". "
                    + "A new project's first note is at tick 1920, one 4/4 bar in, which leaves room for the first consonants. "
                    + "Without the app, notes are sung at their plain pitch until load_pitch writes the singer's own into pitd; do it before a render meant for listening. "
                    + "After changing notes the curve still follows the old ones: load_pitch again with mode all, which replaces everything in pitd. "
                    + "There is no vibrato op: after the last load_pitch, read pitd with get, add the wobble to its values, and write them back with set_curve, in cents. "
                    + "A voicebank folder is saved in the project by its folder name only; the app opens it as the installed singer of that id. "
                    + "Two renders of the same project in one session can differ slightly, seeded or not. Paths are absolute, or relative to where the session was started.",
                ["ops"] = new JsonObject {
                    ["status"] = Op("The open project: tempo, tracks, parts, undo state; in the app also the open part, selection and playhead."),
                    ["new"] = Op("Replace the open project with a new one-track project.",
                        "score: {bpm, singer, phonemizer?, notes?, curves?, name?, time_signature?, start?}",
                        "singer: an installed singer's id, or without the app a voicebank folder",
                        "phonemizer: id, or a tag such as English when only one phonemizer for this singer has it (search phonemizers with singer and query); default is the singer's own, which may be for another language than the lyrics",
                        "notes: as in add_notes, from start on",
                        "curves: {abbr: value for the whole part, or [[tick, value], ...]}",
                        "time_signature: [beats per bar, beat unit], default [4, 4]",
                        "start: tick of the first note, default 1920",
                        "discard_changes: in the app, required when the open project has unsaved changes"),
                    ["open"] = Op("Open a project file.", "path", "discard_changes"),
                    ["save"] = Op("Save as .ustx.", "path: needed the first time"),
                    ["get"] = Op("Notes with their ids, and how they are sung. Waits for the phonemizer.",
                        "part: index; all voice parts if absent",
                        "from_tick, to_tick: only what overlaps this range",
                        "detail: notes (default; each note lists its phoneme names), phonemes (adds each phoneme's timing in ticks, ms and frames), phrases (adds what the renderer is given)",
                        "curves: abbreviations whose drawn points to return, e.g. [\"pitd\"]"),
                    ["add_notes"] = Op("Add notes; they follow one another unless a note has its own tick.",
                        "notes: [[lyric, tone, duration], {\"rest\": duration}, {lyric, tone, duration, tick?}]; lyric \"+\" continues the syllable before it",
                        "at: tick of the first note; or after: id of the note to follow; default: after the part's last note",
                        "part or track: where; default is the part open in the app, else the first part"),
                    ["edit_notes"] = Op("Change notes.", "notes: [{id, lyric?, tone?, tick?, duration?}]"),
                    ["delete_notes"] = Op("Delete notes.", "ids: [id, ...]"),
                    ["set_curve"] = Op("Draw, erase or set the resting value of an expression curve (pitd, dyn, genc, velc, ...). expressions lists them with their ranges; pitd is in cents.",
                        "abbr",
                        "points: [[tick, value], ...] draws straight lines through the points, replacing what was drawn between the first and the last",
                        "clear: true erases the drawn curve, within from_tick..to_tick if given",
                        "default: value the whole track takes wherever no curve is drawn; reset_default: true removes it",
                        "part or track"),
                    ["set_tempo"] = Op("Set the tempo at the start of the project.", "bpm"),
                    ["set_track"] = Op("Change a track's singer or phonemizer.", "track: default 0", "singer: id",
                        "phonemizer: id; a tag or name works when only one phonemizer for this singer has it. With a singer and none given: the one last used with that singer in the app, else the singer's default"),
                    ["load_pitch"] = Op("Write the pitch the singer's model predicts into the pitd curve, as the app's Load Rendered Pitch.",
                        "mode: untuned (default; only phrases that have no pitch curve yet) or all (every phrase, replacing what is in pitd: use it after changing notes)", "part"),
                    ["render"] = Op("Render to wav. Refused while phonemes have errors. Without the app, a part with no pitch curve is sung at plain note pitch: see load_pitch.",
                        "out: file path",
                        "from_tick, to_tick: render only this range; the file then starts at from_tick, and ends with the last sound before to_tick",
                        "track: only this track", "mixdown: one stereo file of all tracks", "allow_errors"),
                    ["undo"] = Op("Undo the last change, the user's included: one request of this session, or one edit of the user's. \"undone\" names the op when it was this session's latest edit."),
                    ["redo"] = Op("Redo."),
                    ["singers"] = Op("The installed singers."),
                    ["phonemizers"] = Op("Phonemizers: all, those offered for a singer, or those a search finds.",
                        "singer", "query: words to look for in id, name, tag and language code, such as english or FI; lists the phonemizers that match any, best match first",
                        "lyrics: [word, ...] (needs singer) tries each listed phonemizer on these words without touching the project: \"trial\" has sung (how many), failed, and the phonemes of the first few. "
                        + "Rule-based phonemizers sing any letters, so read the phonemes: sung alone does not tell the language"),
                    ["expressions"] = Op("The expressions a singer supports, with ranges and defaults.", "singer or track"),
                    ["shutdown"] = Op("End a session that runs without the app."),
                },
            };
        }
    }
}
