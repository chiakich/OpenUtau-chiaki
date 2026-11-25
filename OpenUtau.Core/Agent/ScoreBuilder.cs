using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace OpenUtau.Core.Agent {
    // Builds a one-track project from a small JSON score, so a .ustx need not be written by hand.
    static class ScoreBuilder {
        const int WholeNote = 1920;
        const int DefaultStart = 1920;

        public static UProject Build(JsonElement score, IAgentHost host, string? scoreDirectory, bool allowEmpty = false) {
            if (score.ValueKind != JsonValueKind.Object) {
                throw Invalid("The score must be a JSON object.");
            }
            var known = new HashSet<string> {
                "name", "bpm", "time_signature", "start", "singer", "phonemizer", "notes", "curves",
            };
            foreach (var property in score.EnumerateObject()) {
                if (!known.Contains(property.Name)) {
                    throw Invalid($"Unknown score field \"{property.Name}\".");
                }
            }

            double bpm = score.TryGetProperty("bpm", out var bpmValue) ? Number(bpmValue, "bpm") : 120;
            if (bpm <= 0) {
                throw Invalid("bpm must be positive.");
            }
            int beatPerBar = 4;
            int beatUnit = 4;
            if (score.TryGetProperty("time_signature", out var signature)) {
                if (signature.ValueKind != JsonValueKind.Array || signature.GetArrayLength() != 2) {
                    throw Invalid("time_signature must be [beats per bar, beat unit].");
                }
                beatPerBar = Integer(signature[0], "time_signature");
                beatUnit = Integer(signature[1], "time_signature");
                if (beatPerBar <= 0 || beatUnit <= 0) {
                    throw Invalid("time_signature values must be positive.");
                }
            }
            int start = score.TryGetProperty("start", out var startValue) ? Integer(startValue, "start") : DefaultStart;
            if (start < 0) {
                throw Invalid("start must not be negative.");
            }

            if (!score.TryGetProperty("singer", out var singerValue) || singerValue.ValueKind != JsonValueKind.String) {
                throw Invalid("singer is required: a voicebank folder, or the id of an installed singer.");
            }
            var singer = ResolveSinger(singerValue.GetString()!, host, scoreDirectory);
            string? phonemizerName = singer.Found ? singer.DefaultPhonemizer : null;
            if (score.TryGetProperty("phonemizer", out var phonemizerValue)) {
                if (phonemizerValue.ValueKind != JsonValueKind.String) {
                    throw Invalid("phonemizer must be a string: an id, tag or name.");
                }
                phonemizerName = phonemizerValue.GetString();
            }
            if (string.IsNullOrEmpty(phonemizerName)) {
                throw Invalid("phonemizer is required: this singer names no default phonemizer.");
            }
            var factory = FindPhonemizer(phonemizerName);

            bool hasNotes = score.TryGetProperty("notes", out var notesValue);
            if (hasNotes ? notesValue.ValueKind != JsonValueKind.Array : !allowEmpty) {
                throw Invalid("notes is required: an array of [lyric, tone, duration] or {\"rest\": duration}.");
            }

            return host.Invoke(() => {
                var project = Core.Format.Ustx.Create();
                if (score.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) {
                    project.name = name.GetString()!;
                }
                project.tempos = new List<UTempo> { new UTempo(0, bpm) };
                project.timeSignatures = new List<UTimeSignature> { new UTimeSignature(0, beatPerBar, beatUnit) };
                var track = project.tracks[0];
                track.Singer = singer;
                track.Phonemizer = factory.Create();
                if (singer.Found) {
                    track.RendererSettings = new URenderSettings {
                        renderer = Renderers.GetDefaultRenderer(singer.SingerType),
                    };
                }
                var part = new UVoicePart { name = "Part", trackNo = 0, position = 0 };
                int cursor = start;
                if (hasNotes) {
                    foreach (var note in ParseNotes(project, notesValue, ref cursor)) {
                        part.notes.Add(note);
                    }
                }
                if (part.notes.Count == 0 && !allowEmpty) {
                    throw Invalid("The score has no notes.");
                }
                project.parts.Add(part);
                // Not a full validate: that would start the phonemizer for a file that is only being written.
                project.Validate(new ValidateOptions { SkipPhonemizer = true });
                part.Duration = Math.Max(part.GetMinDurTick(project), part.notes.Count == 0 ? WholeNote * 4 : 0);
                if (score.TryGetProperty("curves", out var curves)) {
                    AddCurves(project, track, part, curves);
                }
                return project;
            });
        }

        // As Format.Ustx.Save, which reports a failure to the UI instead of throwing.
        public static void Save(UProject project, string path, IAgentHost host) {
            host.Invoke(() => Save(project, path));
        }

        public static void Save(UProject project, string path) {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            project.ustxVersion = Core.Format.Ustx.kUstxVersion;
            project.FilePath = path;
            project.BeforeSave();
            File.WriteAllText(path, Yaml.DefaultSerializer.Serialize(project), Encoding.UTF8);
            project.Saved = true;
            project.AfterSave();
        }

        static USinger ResolveSinger(string value, IAgentHost host, string? scoreDirectory) {
            string path = scoreDirectory != null && !Path.IsPathRooted(value) ? Path.Combine(scoreDirectory, value) : value;
            if (Directory.Exists(path)) {
                return host.AddSingerDirectory(path);
            }
            host.SearchInstalledSingers();
            var singer = SingerManager.Inst.GetSinger(value)
                ?? SingerManager.Inst.Singers.Values.FirstOrDefault(s => s.Name == value);
            if (singer == null) {
                host.Warnings.Add($"Singer \"{value}\" is not installed; the project refers to it by this id.");
                singer = USinger.CreateMissing(value);
            }
            return singer;
        }

        public static PhonemizerFactory FindPhonemizer(string name) {
            var all = PhonemizerFactory.GetAll();
            var factory = PhonemizerFactory.Get(name)
                ?? all.FirstOrDefault(f => string.Equals(f.Id, name, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(f => string.Equals(f.tag, name, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(f => string.Equals(f.name, name, StringComparison.OrdinalIgnoreCase));
            if (factory == null) {
                throw new AgentException("phonemizer_not_found",
                    $"Phonemizer \"{name}\" not found. List them with the phonemizers command.");
            }
            return factory;
        }

        // Notes at absolute ticks, each following the one before unless it names its own position.
        public static List<UNote> ParseNotes(UProject project, JsonElement notes, ref int cursor) {
            if (notes.ValueKind != JsonValueKind.Array) {
                throw Invalid("notes must be an array of [lyric, tone, duration] or {\"rest\": duration}.");
            }
            var result = new List<UNote>();
            int index = 0;
            foreach (var item in notes.EnumerateArray()) {
                AddNote(project, result, item, ref cursor, index++);
            }
            return result;
        }

        static void AddNote(UProject project, List<UNote> notes, JsonElement item, ref int cursor, int index) {
            string where = $"notes[{index}]";
            string? lyric;
            JsonElement toneValue;
            JsonElement durationValue;
            if (item.ValueKind == JsonValueKind.Array) {
                if (item.GetArrayLength() != 3) {
                    throw Invalid($"{where} must be [lyric, tone, duration].");
                }
                lyric = item[0].ValueKind == JsonValueKind.String ? item[0].GetString() : null;
                toneValue = item[1];
                durationValue = item[2];
            } else if (item.ValueKind == JsonValueKind.Object) {
                foreach (var property in item.EnumerateObject()) {
                    if (property.Name != "rest" && property.Name != "lyric" && property.Name != "tone"
                            && property.Name != "duration" && property.Name != "position" && property.Name != "tick") {
                        throw Invalid($"{where}: unknown field \"{property.Name}\".");
                    }
                }
                if (item.TryGetProperty("rest", out var rest)) {
                    cursor += Duration(rest, where);
                    return;
                }
                if (item.TryGetProperty("position", out var position) || item.TryGetProperty("tick", out position)) {
                    cursor = Integer(position, where + ".tick");
                    if (cursor < 0) {
                        throw Invalid($"{where}.tick must not be negative.");
                    }
                }
                lyric = item.TryGetProperty("lyric", out var lyricValue) && lyricValue.ValueKind == JsonValueKind.String
                    ? lyricValue.GetString()
                    : null;
                if (!item.TryGetProperty("tone", out toneValue) || !item.TryGetProperty("duration", out durationValue)) {
                    throw Invalid($"{where} needs tone and duration.");
                }
            } else {
                throw Invalid($"{where} must be an array or an object.");
            }
            if (string.IsNullOrEmpty(lyric)) {
                throw Invalid($"{where} needs a lyric.");
            }
            int duration = Duration(durationValue, where);
            var note = project.CreateNote(Tone(toneValue, where), cursor, duration);
            note.lyric = lyric;
            notes.Add(note);
            cursor += duration;
        }

        public static int Tone(JsonElement value, string where) {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) {
                if (number < 0 || number > 127) {
                    throw Invalid($"{where}: tone {number} is out of range.");
                }
                return number;
            }
            if (value.ValueKind == JsonValueKind.String) {
                int tone = MusicMath.NameToTone(value.GetString()!);
                if (tone >= 0) {
                    return tone;
                }
            }
            throw Invalid($"{where}: tone must be a MIDI number or a name like \"C4\", got {value.GetRawText()}.");
        }

        // Ticks (480 per quarter note), or a fraction of a whole note such as "1/8", dotted with a trailing ".".
        public static int Duration(JsonElement value, string where) {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int ticks)) {
                if (ticks <= 0) {
                    throw Invalid($"{where}: duration must be positive.");
                }
                return ticks;
            }
            if (value.ValueKind == JsonValueKind.String) {
                string text = value.GetString()!.Trim();
                int dots = text.Length - text.TrimEnd('.').Length;
                var fraction = text.TrimEnd('.').Split('/');
                if (fraction.Length == 2
                        && int.TryParse(fraction[0], NumberStyles.None, CultureInfo.InvariantCulture, out int numerator)
                        && int.TryParse(fraction[1], NumberStyles.None, CultureInfo.InvariantCulture, out int denominator)
                        && numerator > 0 && denominator > 0) {
                    double length = (double)WholeNote * numerator / denominator * (2 - Math.Pow(0.5, dots));
                    if (length >= 1 && Math.Abs(length - Math.Round(length)) < 1e-9) {
                        return (int)Math.Round(length);
                    }
                }
            }
            throw Invalid($"{where}: duration must be ticks or a fraction like \"1/8\", got {value.GetRawText()}.");
        }

        // A number sets the value for the whole track; [[tick, value], ...] draws a curve in the part.
        static void AddCurves(UProject project, UTrack track, UVoicePart part, JsonElement curves) {
            if (curves.ValueKind != JsonValueKind.Object) {
                throw Invalid("curves must be an object of expression abbreviation to value or points.");
            }
            foreach (var property in curves.EnumerateObject()) {
                var descriptor = CurveDescriptor(project, track, property.Name);
                if (property.Value.ValueKind == JsonValueKind.Number) {
                    SetConstantCurve(project, track, descriptor, property.Value.GetDouble());
                    continue;
                }
                if (property.Value.ValueKind != JsonValueKind.Array) {
                    throw Invalid($"curves.{property.Name} must be a number or an array of [tick, value].");
                }
                var points = new SortedDictionary<int, int>();
                foreach (var point in property.Value.EnumerateArray()) {
                    if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2) {
                        throw Invalid($"curves.{property.Name}: each point must be [tick, value].");
                    }
                    int x = (int)Math.Round(Number(point[0], "tick") / UCurve.interval) * UCurve.interval - part.position;
                    double y = Number(point[1], "value");
                    CheckRange(descriptor, y);
                    points[x] = (int)Math.Round(y);
                }
                part.curves.RemoveAll(c => c.abbr == descriptor.abbr);
                part.curves.Add(new UCurve(descriptor) {
                    xs = points.Keys.ToList(),
                    ys = points.Values.ToList(),
                });
            }
        }

        public static UExpressionDescriptor CurveDescriptor(UProject project, UTrack track, string abbr) {
            abbr = abbr.ToLowerInvariant();
            if (!track.TryGetExpDescriptor(project, abbr, out var descriptor)) {
                throw Invalid($"Unknown expression \"{abbr}\".");
            }
            if (descriptor.type != UExpressionType.Curve) {
                throw Invalid($"Expression \"{abbr}\" is not a curve.");
            }
            return descriptor;
        }

        // The track's empty value for the expression, as the expression defaults panel sets it: every
        // position without a drawn curve samples it.
        public static void SetConstantCurve(UProject project, UTrack track, UExpressionDescriptor descriptor, double value) {
            CheckRange(descriptor, value);
            foreach (var part in project.parts.OfType<UVoicePart>().Where(p => project.tracks[p.trackNo] == track)) {
                part.curves.RemoveAll(c => c.abbr == descriptor.abbr);
            }
            track.ExpressionDefaultOverrides ??= new Dictionary<string, float>();
            track.ExpressionDefaultOverrides.Remove(descriptor.abbr);
            if ((float)value != descriptor.CustomDefaultValue) {
                track.ExpressionDefaultOverrides[descriptor.abbr] = (float)value;
            }
        }

        public static void CheckRange(UExpressionDescriptor descriptor, double value) {
            if (value < descriptor.min || value > descriptor.max) {
                throw Invalid($"{descriptor.abbr} must be between {descriptor.min} and {descriptor.max}, got {value}.");
            }
        }

        static double Number(JsonElement value, string what) {
            if (value.ValueKind != JsonValueKind.Number) {
                throw Invalid($"{what} must be a number.");
            }
            return value.GetDouble();
        }

        static int Integer(JsonElement value, string what) {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result)) {
                throw Invalid($"{what} must be an integer.");
            }
            return result;
        }

        static AgentException Invalid(string message) => new AgentException("invalid_score", message, AgentException.ExitUsage);
    }
}
