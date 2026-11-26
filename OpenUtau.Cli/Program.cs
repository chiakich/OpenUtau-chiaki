using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using OpenUtau.Core;
using OpenUtau.Core.Agent;
using OpenUtau.Core.DiffSinger;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace OpenUtau.Cli {
    static class Program {
        public static readonly JsonSerializerOptions Indented = new JsonSerializerOptions {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        static readonly JsonSerializerOptions Compact = new JsonSerializerOptions {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        static Stream stdout = Stream.Null;
        static bool pretty;

        static int Main(string[] argv) {
            // As the app: number formatting must not follow the system locale.
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
            // stdout carries the result and nothing else; whatever else prints goes to stderr.
            stdout = Console.OpenStandardOutput();
            Console.SetOut(Console.Error);

            int exit;
            try {
                var args = Args.Parse(argv);
                pretty = args.Flag("pretty");
                exit = Run(args);
            } catch (AgentException e) {
                WriteError(e.Code, e.Message);
                exit = e.ExitCode;
            } catch (Exception e) {
                WriteError("internal", e.ToString());
                exit = AgentException.ExitFailure;
            }
            stdout.Flush();
            Console.Error.Flush();
            // Not a plain return: native inference threads can keep the process from ending.
            Environment.Exit(exit);
            return exit;
        }

        const string CommandNames = "phonemize, render, ustx, singers, expressions, phonemizers, serve, send, mcp, help";

        static int Run(Args args) {
            if (args.Flag("help") || args.Command == "help") {
                WriteResult(Help());
                return 0;
            }
            switch (args.Command) {
                case "phonemize": return Phonemize(args);
                case "render": return Render(args);
                case "ustx": return Ustx(args);
                case "singers": return Singers(args);
                case "expressions": return Expressions(args);
                case "phonemizers": return Phonemizers(args);
                case "serve": return Serve(args);
                case "send": return Send(args);
                case "mcp": return McpServer(args);
                case "":
                    throw AgentException.Usage($"No command. Commands: {CommandNames}.");
                default:
                    throw AgentException.Usage($"Unknown command \"{args.Command}\". Commands: {CommandNames}.");
            }
        }

        static void WriteResult(JsonObject result) {
            string json = result.ToJsonString(pretty ? Indented : Compact) + "\n";
            stdout.Write(Encoding.UTF8.GetBytes(json));
        }

        static void WriteError(string code, string message) {
            var error = new JsonObject {
                ["ok"] = false,
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            };
            Console.Error.WriteLine(error.ToJsonString(Compact));
        }

        // ---- Options shared by the commands --------------------------------------------------------

        static HostOptions ReadHostOptions(Args args, bool render) {
            var options = new HostOptions {
                Prefs = args.Get("prefs") ?? HostOptions.PrefsGui,
                CacheDir = args.Get("cache-dir"),
                Verbose = args.Flag("verbose"),
                LogFile = args.Get("log"),
            };
            if (options.Prefs != HostOptions.PrefsGui && options.Prefs != HostOptions.PrefsDefault) {
                throw AgentException.Usage("--prefs must be gui or default.");
            }
            foreach (string pair in args.GetAll("set")) {
                var (key, value) = SplitPair(pair, "--set", "Key=Value");
                options.PrefOverrides.Add(new KeyValuePair<string, string>(key, value));
            }
            if (!render) {
                return options;
            }
            void Override(string key, string? value) {
                if (value != null) {
                    options.PrefOverrides.Add(new KeyValuePair<string, string>(key, value));
                }
            }
            string? runner = args.Get("runner");
            if (runner != null) {
                var known = new[] { "CPU", "WebGPU", "DirectML", "CUDA", "NNAPI" };
                runner = known.FirstOrDefault(k => string.Equals(k, runner, StringComparison.OrdinalIgnoreCase))
                    ?? throw AgentException.Usage($"--runner must be one of {string.Join(", ", known)}.");
            }
            Override(nameof(Preferences.SerializablePreferences.OnnxRunner), runner);
            Override(nameof(Preferences.SerializablePreferences.DiffSingerSteps), Positive(args.GetInt("steps"), "--steps"));
            Override(nameof(Preferences.SerializablePreferences.DiffSingerStepsVariance), Positive(args.GetInt("steps-variance"), "--steps-variance"));
            Override(nameof(Preferences.SerializablePreferences.DiffSingerStepsPitch), Positive(args.GetInt("steps-pitch"), "--steps-pitch"));
            double? depth = args.GetDouble("depth");
            if (depth.HasValue && (depth.Value <= 0 || depth.Value > 1)) {
                throw AgentException.Usage("--depth must be above 0 and at most 1.");
            }
            Override(nameof(Preferences.SerializablePreferences.DiffSingerDepth), depth?.ToString("R", CultureInfo.InvariantCulture));
            string? seed = args.Get("seed");
            if (seed != null) {
                if (!uint.TryParse(seed, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)) {
                    throw AgentException.Usage("--seed must be a whole number from 0 to 4294967295.");
                }
                options.Seed = value;
            }
            return options;
        }

        static string? Positive(int? value, string option) {
            if (value.HasValue && value.Value <= 0) {
                throw AgentException.Usage($"{option} must be positive.");
            }
            return value?.ToString(CultureInfo.InvariantCulture);
        }

        static (string, string) SplitPair(string pair, string option, string form) {
            int eq = pair.IndexOf('=');
            if (eq <= 0) {
                throw AgentException.Usage($"{option} takes {form}, got \"{pair}\".");
            }
            return (pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim());
        }

        sealed class ProjectOptions {
            public readonly List<(int? Track, string Value)> Singers = new List<(int?, string)>();
            public readonly List<(int? Track, string Value)> Phonemizers = new List<(int?, string)>();
            public readonly List<(string Abbr, double Value)> Curves = new List<(string, double)>();
            public double? Bpm;
            public double? TempoScale;
            public bool AllTracksGetSinger => Singers.Any(s => !s.Track.HasValue);
        }

        static ProjectOptions ReadProjectOptions(Args args, bool render) {
            var options = new ProjectOptions();
            foreach (string value in args.GetAll("singer")) {
                options.Singers.Add(PerTrack(value));
            }
            foreach (string value in args.GetAll("phonemizer")) {
                options.Phonemizers.Add(PerTrack(value));
            }
            options.Bpm = args.GetDouble("bpm");
            options.TempoScale = args.GetDouble("tempo-scale");
            if (options.Bpm.HasValue && options.TempoScale.HasValue) {
                throw AgentException.Usage("Give --bpm or --tempo-scale, not both.");
            }
            if ((options.Bpm ?? 1) <= 0 || (options.TempoScale ?? 1) <= 0) {
                throw AgentException.Usage("--bpm and --tempo-scale must be positive.");
            }
            if (render) {
                foreach (string pair in args.GetAll("curve")) {
                    var (abbr, text) = SplitPair(pair, "--curve", "ABBR=VALUE");
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) {
                        throw AgentException.Usage($"--curve {abbr}: \"{text}\" is not a number.");
                    }
                    options.Curves.Add((abbr.ToLowerInvariant(), value));
                }
            }
            return options;
        }

        // "VALUE" for every track, or "N=VALUE" for track N.
        static (int?, string) PerTrack(string value) {
            int eq = value.IndexOf('=');
            if (eq > 0 && int.TryParse(value.Substring(0, eq), NumberStyles.None, CultureInfo.InvariantCulture, out int track)) {
                return (track, value.Substring(eq + 1));
            }
            return (null, value);
        }

        // Loads the singers the options name. Before the project is read, so that tracks naming one of
        // them by id find it.
        static List<(int? Track, USinger Singer)> PrepareSingers(HeadlessHost host, ProjectOptions options) {
            if (!options.AllTracksGetSinger) {
                host.SearchInstalledSingers();
            }
            return options.Singers.Select(s => (s.Track, host.AddSingerDirectory(s.Value))).ToList();
        }

        static void ApplyProjectOptions(UProject project, HeadlessHost host, ProjectOptions options,
                List<(int? Track, USinger Singer)> singers) {
            IEnumerable<UTrack> Tracks(int? index, string option) {
                if (!index.HasValue) {
                    return project.tracks;
                }
                if (index.Value >= project.tracks.Count) {
                    throw AgentException.Usage($"{option}: the project has no track {index.Value}.");
                }
                return new[] { project.tracks[index.Value] };
            }
            foreach (var (index, singer) in singers) {
                foreach (var track in Tracks(index, "--singer")) {
                    track.Singer = singer;
                    if (!Renderers.GetSupportedRenderers(singer.SingerType).Contains(track.RendererSettings?.renderer)) {
                        track.RendererSettings = new URenderSettings {
                            renderer = Renderers.GetDefaultRenderer(singer.SingerType),
                        };
                    }
                }
            }
            foreach (var (index, name) in options.Phonemizers) {
                foreach (var track in Tracks(index, "--phonemizer")) {
                    track.Phonemizer = ScoreBuilder.FindPhonemizer(name, track.Singer, host).Create();
                }
            }
            if (options.Bpm.HasValue) {
                project.tempos = new List<UTempo> { new UTempo(0, options.Bpm.Value) };
            } else if (options.TempoScale.HasValue) {
                foreach (var tempo in project.tempos) {
                    tempo.bpm *= options.TempoScale.Value;
                }
            }
            foreach (var (abbr, value) in options.Curves) {
                foreach (var track in project.tracks) {
                    try {
                        var descriptor = ScoreBuilder.CurveDescriptor(project, track, abbr);
                        ScoreBuilder.SetConstantCurve(project, track, descriptor, value);
                    } catch (AgentException e) {
                        throw AgentException.Usage($"--curve: {e.Message}");
                    }
                    if (!DiffSingerUtils.IsExpressionAvailable(track.Singer, abbr)) {
                        host.Warnings.Add($"Track {project.tracks.IndexOf(track)}: singer \"{track.Singer?.Id}\" does not use {abbr}.");
                    }
                }
            }
        }

        static JsonObject Settings(HeadlessHost host, HostOptions hostOptions, ProjectOptions projectOptions) {
            var prefs = Preferences.Default;
            var curves = new JsonObject();
            foreach (var (abbr, value) in projectOptions.Curves) {
                curves[abbr] = value;
            }
            return new JsonObject {
                ["prefs"] = hostOptions.Prefs,
                ["prefs_file"] = host.PrefsFileUsed ? PathManager.Inst.PrefsFilePath : null,
                ["runner"] = host.EffectiveRunner(),
                ["steps"] = prefs.DiffSingerSteps,
                ["steps_variance"] = prefs.DiffSingerStepsVariance,
                ["steps_pitch"] = prefs.DiffSingerStepsPitch,
                ["depth"] = prefs.DiffSingerDepth,
                ["tensor_cache"] = prefs.DiffSingerTensorCache,
                ["merge_overlapping_phrases"] = prefs.DiffSingerMergeOverlappingPhrases,
                ["merge_phrases_sec"] = prefs.MergePhrasesSec,
                ["acoustic_flat_pitch"] = prefs.DiffSingerAcousticFlatPitch,
                ["acoustic_f0_zero"] = prefs.DiffSingerAcousticF0ZeroEnabled,
                ["acoustic_local_retake"] = prefs.DiffSingerAcousticLocalRetake,
                ["variance_local_pitch_patch"] = prefs.DiffSingerVarianceLocalPitchPatch,
                ["unvoiced_consonant_f0_interpolate"] = prefs.DiffSingerUnvoicedConsonantAcousticF0Interpolate,
                ["phoneme_variance_remap"] = prefs.DiffSingerPhonemeVarianceRemap,
                ["skip_muted_tracks"] = prefs.SkipRenderingMutedTracks,
                ["seed"] = hostOptions.Seed,
                ["cache_dir"] = hostOptions.CacheDir == null ? null : host.CachePath,
                ["bpm"] = projectOptions.Bpm,
                ["tempo_scale"] = projectOptions.TempoScale,
                ["curves"] = curves,
            };
        }

        // Before the host starts: a wrong path should fail at once, not after the singers were searched.
        static void RequireFile(string path) {
            if (!File.Exists(path)) {
                throw new AgentException("file_not_found", $"Project not found: {path}");
            }
        }

        static JsonArray Strings(IEnumerable<string> values) {
            return new JsonArray(values.Select(v => (JsonNode)v).ToArray());
        }

        // Reports the outcome of a command that read a project. Phoneme errors fail the command unless
        // allowed, but the report is still written: it says which notes went wrong.
        static int Finish(JsonObject result, HeadlessHost host, JsonArray errors, bool allowErrors) {
            bool failed = errors.Count > 0 && !allowErrors;
            result["ok"] = !failed;
            result["errors"] = errors;
            result["warnings"] = Strings(host.Warnings);
            result["diagnostics"] = Strings(host.Diagnostics);
            WriteResult(result);
            if (failed) {
                WriteError("phoneme_errors", $"{errors.Count} phoneme(s) have errors; see \"errors\" in the result. Pass --allow-errors to accept them.");
                return AgentException.ExitFailure;
            }
            return 0;
        }

        // ---- Commands ------------------------------------------------------------------------------

        static int Phonemize(Args args) {
            string path = args.Positional(0, "the project file");
            var hostOptions = ReadHostOptions(args, render: false);
            var projectOptions = ReadProjectOptions(args, render: false);
            bool allowErrors = args.Flag("allow-errors");
            args.EnsureAllUsed(1);
            RequireFile(path);

            using var host = HeadlessHost.Start(hostOptions);
            var singers = PrepareSingers(host, projectOptions);
            var project = host.OpenProject(path, p => ApplyProjectOptions(p, host, projectOptions, singers));
            host.CheckSingers(project);
            host.WaitPhonemized(project);

            var errors = new JsonArray();
            var result = host.Loop.Invoke(() => new JsonObject {
                ["ok"] = true,
                ["command"] = "phonemize",
                ["project"] = ProjectReport.Project(project),
                ["tracks"] = ProjectReport.Tracks(project),
                ["parts"] = ProjectReport.Parts(project, errors),
                ["settings"] = Settings(host, hostOptions, projectOptions),
            });
            return Finish(result, host, errors, allowErrors);
        }

        static int Render(Args args) {
            string path = args.Positional(0, "the project file");
            string outPath = args.Get("out") ?? throw AgentException.Usage("render needs --out FILE.wav.");
            var hostOptions = ReadHostOptions(args, render: true);
            var projectOptions = ReadProjectOptions(args, render: true);
            bool allowErrors = args.Flag("allow-errors");
            bool mixdown = args.Flag("mixdown");
            int? trackOption = args.GetInt("track");
            if (trackOption < 0) {
                throw AgentException.Usage("--track must not be negative.");
            }
            int track = trackOption ?? -1;
            string? dumpDir = args.Get("dump");
            string loadPitch = args.Get("load-pitch") ?? AgentOps.PitchNone;
            if (loadPitch != AgentOps.PitchNone && loadPitch != AgentOps.PitchUntuned && loadPitch != AgentOps.PitchAll) {
                throw AgentException.Usage("--load-pitch must be none, untuned or all.");
            }
            args.EnsureAllUsed(1);
            RequireFile(path);

            using var host = HeadlessHost.Start(hostOptions);
            var singers = PrepareSingers(host, projectOptions);
            var project = host.OpenProject(path, p => ApplyProjectOptions(p, host, projectOptions, singers));
            if (track >= project.tracks.Count) {
                throw AgentException.Usage($"--track: the project has no track {track}.");
            }
            host.CheckSingers(project);
            host.WaitPhonemized(project);

            using var dump = dumpDir == null ? null : new ModelDump(dumpDir, project);
            if (loadPitch != AgentOps.PitchNone) {
                host.LoadRenderedPitch(project, loadPitch);
            }
            if (dump != null) {
                dump.RecordPhonemizer = true;
                host.Rephonemize(project);
                dump.RecordPhonemizer = false;
            }

            var errors = new JsonArray();
            var settings = Settings(host, hostOptions, projectOptions);
            settings["load_pitch"] = loadPitch;
            settings["mixdown"] = mixdown;
            settings["seeded_random_nodes"] = null;
            var result = host.Loop.Invoke(() => new JsonObject {
                ["ok"] = true,
                ["command"] = "render",
                ["outputs"] = new JsonArray(),
                ["project"] = ProjectReport.Project(project),
                ["tracks"] = ProjectReport.Tracks(project),
                ["parts"] = ProjectReport.Parts(project, errors),
                ["settings"] = settings,
                ["dump"] = null,
            });
            if (errors.Count > 0 && !allowErrors) {
                return Finish(result, host, errors, allowErrors);
            }

            var outputs = host.Render(project, outPath, mixdown, track);
            result["outputs"] = new JsonArray(outputs.Select(o => (JsonNode)new JsonObject {
                ["track"] = o.Track < 0 ? null : o.Track,
                ["path"] = o.Path,
                ["channels"] = o.Channels,
                ["sample_rate"] = o.SampleRate,
                ["bits"] = o.BitsPerSample,
                ["samples"] = o.Samples,
                ["duration_ms"] = ProjectReport.Round(o.Samples * 1000.0 / o.SampleRate),
                ["sha256"] = o.Sha256,
            }).ToArray());
            // Sessions are created while rendering, so the count is only final now.
            settings["seeded_random_nodes"] = hostOptions.Seed.HasValue ? host.SeededNodes : null;
            if (dump != null) {
                string manifest = host.Loop.Invoke(() => dump.Finish());
                result["dump"] = new JsonObject {
                    ["dir"] = Path.GetDirectoryName(manifest),
                    ["manifest"] = manifest,
                };
            }
            return Finish(result, host, errors, allowErrors);
        }

        static int Ustx(Args args) {
            string scorePath = args.Positional(0, "the score file (or - for stdin)");
            string outPath = args.Get("out") ?? throw AgentException.Usage("ustx needs --out FILE.ustx.");
            var hostOptions = ReadHostOptions(args, render: false);
            args.EnsureAllUsed(1);

            string text;
            string? scoreDirectory = null;
            if (scorePath == "-") {
                text = Console.In.ReadToEnd();
            } else if (File.Exists(scorePath)) {
                text = File.ReadAllText(scorePath, Encoding.UTF8);
                scoreDirectory = Path.GetDirectoryName(Path.GetFullPath(scorePath));
            } else {
                throw new AgentException("file_not_found", $"Score not found: {scorePath}");
            }
            JsonDocument document;
            try {
                document = JsonDocument.Parse(text, new JsonDocumentOptions {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            } catch (JsonException e) {
                throw new AgentException("invalid_score", $"The score is not valid JSON: {e.Message}", AgentException.ExitUsage);
            }

            using var host = HeadlessHost.Start(hostOptions);
            using (document) {
                var project = ScoreBuilder.Build(document.RootElement, host, scoreDirectory);
                ScoreBuilder.Save(project, outPath, host);
                var result = host.Loop.Invoke(() => {
                    var track = project.tracks[0];
                    var part = project.parts.OfType<UVoicePart>().First();
                    return new JsonObject {
                        ["ok"] = true,
                        ["command"] = "ustx",
                        ["path"] = project.FilePath,
                        ["singer_id"] = track.Singer.Id,
                        ["singer_path"] = track.Singer.Found ? track.Singer.Location : null,
                        ["phonemizer"] = track.Phonemizer.Id,
                        ["tempos"] = new JsonArray(project.tempos
                            .Select(t => (JsonNode)new JsonObject { ["tick"] = t.position, ["bpm"] = t.bpm })
                            .ToArray()),
                        ["notes"] = part.notes.Count,
                        ["start_tick"] = part.position + part.notes.First().position,
                        ["end_tick"] = part.position + part.notes.Last().End,
                    };
                });
                result["warnings"] = Strings(host.Warnings);
                result["diagnostics"] = Strings(host.Diagnostics);
                WriteResult(result);
            }
            return 0;
        }

        static int Singers(Args args) {
            var hostOptions = ReadHostOptions(args, render: false);
            var directories = args.GetAll("singer");
            args.EnsureAllUsed(0);

            using var host = HeadlessHost.Start(hostOptions);
            host.SearchInstalledSingers();
            foreach (string directory in directories) {
                host.AddSingerDirectory(directory);
            }
            WriteResult(new JsonObject {
                ["ok"] = true,
                ["command"] = "singers",
                ["search_paths"] = Strings(PathManager.Inst.SingersPaths),
                ["singers"] = Queries.Singers(),
                ["warnings"] = Strings(host.Warnings),
                ["diagnostics"] = Strings(host.Diagnostics),
            });
            return 0;
        }

        static int Expressions(Args args) {
            string singerName = args.Get("singer") ?? throw AgentException.Usage("expressions needs --singer DIR or --singer ID.");
            var hostOptions = ReadHostOptions(args, render: false);
            args.EnsureAllUsed(0);

            using var host = HeadlessHost.Start(hostOptions);
            var singer = Queries.ResolveSinger(host, singerName);
            var result = new JsonObject { ["ok"] = true, ["command"] = "expressions" };
            foreach (var pair in Queries.Expressions(host, singer).ToList()) {
                result[pair.Key] = pair.Value?.DeepClone();
            }
            result["warnings"] = Strings(host.Warnings);
            result["diagnostics"] = Strings(host.Diagnostics);
            WriteResult(result);
            return 0;
        }

        static int Phonemizers(Args args) {
            string? singerName = args.Get("singer");
            var hostOptions = ReadHostOptions(args, render: false);
            args.EnsureAllUsed(0);

            using var host = HeadlessHost.Start(hostOptions);
            var singer = singerName == null ? null : Queries.ResolveSinger(host, singerName);
            WriteResult(new JsonObject {
                ["ok"] = true,
                ["command"] = "phonemizers",
                ["singer_id"] = singer?.Id,
                ["phonemizers"] = Queries.Phonemizers(singer),
                ["warnings"] = Strings(host.Warnings),
                ["diagnostics"] = Strings(host.Diagnostics),
            });
            return 0;
        }

        // A session: one project kept open and edited request by request, with the models staying loaded.
        static int Serve(Args args) {
            var hostOptions = ReadHostOptions(args, render: true);
            string? socket = args.Get("socket");
            var singers = args.GetAll("singer");
            args.EnsureAllUsed(0);

            using var host = HeadlessHost.Start(hostOptions);
            foreach (string directory in singers) {
                host.AddSingerDirectory(directory);
            }
            using var session = new AgentSession(host);
            if (socket == null) {
                AgentTransport.Serve(session, Console.OpenStandardInput(), stdout);
                return 0;
            }
            using var server = new AgentSocketServer(session, socket);
            using var interrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGINT, context => {
                    context.Cancel = true;
                    server.Stopped.Set();
                });
            using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGTERM, context => {
                    context.Cancel = true;
                    server.Stopped.Set();
                });
            WriteResult(new JsonObject {
                ["ok"] = true,
                ["command"] = "serve",
                ["socket"] = server.Path,
                ["pid"] = Environment.ProcessId,
            });
            stdout.Flush();
            server.Stopped.Wait();
            return 0;
        }

        // One request to a session that is already running: the app's, or a serve --socket one.
        static int McpServer(Args args) {
            bool gui = args.Flag("gui");
            string? socket = args.Get("socket");
            if (gui && socket != null) {
                throw AgentException.Usage("mcp takes --gui or --socket PATH, not both.");
            }
            if (gui || socket != null) {
                double timeout = args.GetDouble("timeout") ?? 1800;
                args.EnsureAllUsed(0);
                if (timeout <= 0) {
                    throw AgentException.Usage("--timeout must be positive.");
                }
                string path = gui ? AgentTransport.GuiSocketPath : socket!;
                string where = gui
                    ? "This server edits the project the user has open in the OpenUtau app: they see every change and can undo it. The app must be running with its agent connection on."
                    : "This server works on the project of a running serve session.";
                new Mcp(request => AgentTransport.Send(path, request, TimeSpan.FromSeconds(timeout)), where)
                    .Serve(Console.OpenStandardInput(), stdout);
                return 0;
            }
            var hostOptions = ReadHostOptions(args, render: true);
            var singers = args.GetAll("singer");
            args.EnsureAllUsed(0);
            using var host = HeadlessHost.Start(hostOptions);
            var given = singers.Select(host.AddSingerDirectory).ToList();
            string own = "This server has its own project and no window: save_project and render write the results to files.";
            if (given.Count > 0) {
                // Only made available: a new project still names its singer.
                own += " Voicebank folders given on the command line, by id: " + string.Join(", ", given.Select(s => $"\"{s.Id}\" ({s.Location})")) + ".";
                foreach (var singer in given) {
                    if (host.Invoke(() => host.InstalledTwin(singer)) is USinger twin) {
                        own += $" Installed singer \"{twin.Id}\" is the same voicebank as \"{singer.Id}\": use \"{twin.Id}\" for a project the user will open in the app, which knows singers by id only.";
                    }
                }
            }
            using var session = new AgentSession(host);
            new Mcp(session.HandleLine, own)
                .Serve(Console.OpenStandardInput(), stdout);
            return 0;
        }

        static int Send(Args args) {
            bool gui = args.Flag("gui");
            string? socket = args.Get("socket");
            double timeout = args.GetDouble("timeout") ?? 1800;
            string request = args.Positional(0, "the request: a JSON object, an op name, or - for stdin");
            args.EnsureAllUsed(1);
            if (gui == (socket != null)) {
                throw AgentException.Usage("send needs --gui (the running app) or --socket PATH (a serve --socket session).");
            }
            if (timeout <= 0) {
                throw AgentException.Usage("--timeout must be positive.");
            }
            if (request == "-") {
                request = Console.In.ReadToEnd();
            } else if (!request.TrimStart().StartsWith("{")) {
                request = new JsonObject { ["op"] = request }.ToJsonString(Compact);
            }
            string reply;
            try {
                reply = AgentTransport.Send(gui ? AgentTransport.GuiSocketPath : socket!, request, TimeSpan.FromSeconds(timeout));
            } catch (AgentException e) when (gui && e.Code == "not_listening") {
                throw new AgentException("not_listening",
                    $"The app is not listening. Start OpenUtau with the agent connection turned on (see the README). {e.Message}");
            }
            var json = JsonNode.Parse(reply) as JsonObject
                ?? throw new AgentException("bad_reply", $"The session sent something that is not a JSON object: {reply}");
            if (json["ok"]?.GetValue<bool>() == true) {
                WriteResult(json);
                return 0;
            }
            Console.Error.WriteLine(json.ToJsonString(Compact));
            return AgentException.ExitFailure;
        }

        static JsonObject Help() {
            JsonObject Command(string usage, string summary, params string[] options) {
                return new JsonObject { ["usage"] = usage, ["summary"] = summary, ["options"] = Strings(options) };
            }
            string[] project = {
                "--singer DIR | N=DIR: use the voicebank in DIR for every track, or for track N; it need not be installed",
                "--phonemizer ID | N=ID: phonemizer by id, tag or name",
                "--bpm N: replace the tempo map with one tempo",
                "--tempo-scale X: multiply every tempo by X",
                "--allow-errors: exit 0 even if some phonemes have errors",
            };
            string[] common = {
                "--prefs gui|default: take settings from the app's prefs.json (default) or from built-in defaults",
                "--set Key=Value: override one preference by its name in prefs.json",
                "--cache-dir DIR: keep render caches in DIR instead of a temporary folder",
                "--log FILE: write the log to FILE",
                "--verbose: log to stderr",
                "--pretty: indent the JSON",
            };
            string[] render = {
                "--out FILE.wav: required; one mono 16-bit file per track, as the app's Export Wav",
                "--mixdown: one stereo file of all tracks, with volume, pan and mix effects",
                "--track N: render only track N",
                "--seed N: fix the random numbers, so two runs give the same audio",
                "--load-pitch none|untuned|all: run Load Rendered Pitch first, on phrases without pitch or on everything",
                "--curve ABBR=VALUE: set an expression curve to one value for the whole track, e.g. velc=150",
                "--dump DIR: save each model's inputs and outputs as .npz, with a manifest.json",
                "--runner cpu|webgpu|directml|cuda",
                "--steps N, --steps-variance N, --steps-pitch N, --depth X",
            };
            return new JsonObject {
                ["ok"] = true,
                ["command"] = "help",
                ["output"] = "One JSON object on stdout. On failure stdout is empty, stderr ends with one line {\"ok\":false,\"error\":{\"code\",\"message\"}}, and the exit code is 1, or 2 for bad arguments.",
                ["commands"] = new JsonObject {
                    ["phonemize"] = Command("phonemize IN.ustx [options]",
                        "Notes, phonemes and render phrases with their timing, from the app's own phonemizer.",
                        project.Concat(common).ToArray()),
                    ["render"] = Command("render IN.ustx --out OUT.wav [options]",
                        "Render to wav. The result also carries everything phonemize reports.",
                        render.Concat(project).Concat(common).ToArray()),
                    ["ustx"] = Command("ustx SCORE.json|- --out OUT.ustx",
                        "Write a project from a JSON score: {bpm, singer, phonemizer, notes: [[lyric, tone, duration], {\"rest\": duration}]}.",
                        common),
                    ["singers"] = Command("singers [--singer DIR]", "List the installed singers.", common),
                    ["expressions"] = Command("expressions --singer DIR|ID",
                        "The expressions a singer supports, with their ranges and defaults.", common),
                    ["phonemizers"] = Command("phonemizers [--singer DIR|ID]",
                        "List the phonemizers, or those the app offers for a singer.", common),
                    ["serve"] = Command("serve [--socket PATH] [--singer DIR] [--seed N] [--runner ...]",
                        "Keep one project open and edit it request by request: JSON lines on stdin and stdout, or on a local socket. Send {\"op\":\"help\"} for the requests.",
                        new[] {
                            "--socket PATH: listen on a Unix socket instead of stdin; requests then come from the send command",
                            "--singer DIR: make a voicebank folder available; may be repeated",
                            "--seed N, --runner, --steps N, --steps-variance N, --steps-pitch N, --depth X: as render",
                        }.Concat(common).ToArray()),
                    ["send"] = Command("send (--gui | --socket PATH) REQUEST",
                        "Send one request to a running session and print its reply. --gui is the project open in the app.",
                        "REQUEST: a JSON object, a bare op name such as status, or - to read it from stdin",
                        "--timeout SECONDS: default 1800",
                        "--pretty: indent the JSON"),
                    ["mcp"] = Command("mcp [--gui | --socket PATH] [--singer DIR] [--seed N] [--runner ...]",
                        "A Model Context Protocol server on stdin and stdout, for MCP clients: the session requests as tools.",
                        new[] {
                            "--gui: work on the project open in the app; --socket PATH: on a serve --socket session; neither: a project of its own",
                            "--singer DIR, --seed N, --runner, --steps ...: as serve, without --gui and --socket",
                            "--timeout SECONDS: with --gui or --socket, how long to wait for a reply; default 1800",
                        }.Concat(common).ToArray()),
                },
            };
        }
    }
}
