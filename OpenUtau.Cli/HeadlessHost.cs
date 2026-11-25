using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using OpenUtau.Classic;
using OpenUtau.Core;
using OpenUtau.Core.Agent;
using OpenUtau.Core.DiffSinger;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace OpenUtau.Cli {
    sealed class HostOptions {
        public const string PrefsGui = "gui";
        public const string PrefsDefault = "default";

        public string Prefs = PrefsGui;
        public List<KeyValuePair<string, string>> PrefOverrides = new List<KeyValuePair<string, string>>();
        public string? CacheDir;
        public uint? Seed;
        public bool Verbose;
        public string? LogFile;
    }

    // Runs OpenUtau.Core the way the app does, minus the window: a main thread for document work, the
    // user's preferences and singers read only, and a cache of its own.
    sealed class HeadlessHost : IAgentHost, IDisposable, ICmdSubscriber {
        static readonly TimeSpan PhonemizeTimeout = TimeSpan.FromMinutes(30);

        readonly HostOptions options;
        readonly LogSink sink;
        readonly bool ownsCache;
        int seededNodes;

        readonly List<USinger> added = new List<USinger>();
        bool searched;

        public MainLoop Loop { get; }
        public string CachePath { get; }
        public bool PrefsFileUsed { get; private set; }
        public IList<string> Warnings { get; } = new List<string>();
        public int SeededNodes => seededNodes;
        public string Kind => "headless";
        public TaskScheduler Scheduler => Loop.Scheduler;
        public uint? Seed => options.Seed;

        public T Invoke<T>(Func<T> func) => Loop.Invoke(func);
        public System.Text.Json.Nodes.JsonObject? UiState(Func<UNote, int> noteId) => null;

        HeadlessHost(HostOptions options, string cachePath, bool ownsCache, LogSink sink) {
            this.options = options;
            this.sink = sink;
            this.ownsCache = ownsCache;
            CachePath = cachePath;
            Loop = new MainLoop {
                OnError = e => Log.Error(e, "Unhandled error on the main thread."),
            };
        }

        public IReadOnlyList<string> Diagnostics => sink.Errors();
        public IReadOnlyList<string> TakeDiagnostics() => sink.Errors(take: true);

        public static HeadlessHost Start(HostOptions options) {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // The cache cleanup deletes files older than a week from its folder, so the folder is
            // always one this tool made, never the one the user named.
            bool ownsCache = string.IsNullOrEmpty(options.CacheDir);
            string cachePath = ownsCache
                ? Path.Combine(Path.GetTempPath(), "openutau-cli", $"cache-{Environment.ProcessId}-{Guid.NewGuid():N}")
                : Path.Combine(Path.GetFullPath(options.CacheDir!), "openutau-cli",
                    options.Seed.HasValue ? $"seed-{options.Seed.Value}" : "unseeded");
            Directory.CreateDirectory(cachePath);
            Headless.ReadOnlyUserData = true;
            Headless.CachePath = cachePath;

            var sink = new LogSink(options.Verbose, options.LogFile);
            Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

            var host = new HeadlessHost(options, cachePath, ownsCache, sink);
            try {
                host.Initialize();
            } catch {
                host.Dispose();
                throw;
            }
            return host;
        }

        void Initialize() {
            LoadPreferences();
            if (options.Seed.HasValue) {
                uint seed = options.Seed.Value;
                Headless.NoiseSeed = seed;
                Headless.OnnxModelFilter = model => {
                    var patched = OnnxSeedPatcher.Patch(model, seed, out int count);
                    Interlocked.Add(ref seededNodes, count);
                    return patched;
                };
            }
            ToolsManager.Inst.Initialize();
            DocManager.Inst.InitializeHeadless(Loop.Thread, Loop.Scheduler);
            DocManager.Inst.PostOnUIThread = Loop.Post;
            DocManager.Inst.AddSubscriber(this);
            // Created here rather than by whichever worker thread touches it first.
            _ = PlaybackManager.Inst;
        }

        void LoadPreferences() {
            if (options.Prefs == HostOptions.PrefsDefault) {
                Preferences.Default = new Preferences.SerializablePreferences();
            } else {
                PrefsFileUsed = File.Exists(PathManager.Inst.PrefsFilePath);
                _ = Preferences.Default;
            }
            var prefs = Preferences.Default;
            // Both would start model runs behind the command's back.
            prefs.PreRender = false;
            prefs.RealTimePitchMode = (int)LivePitchMode.Off;
            foreach (var pair in options.PrefOverrides) {
                SetPreference(pair.Key, pair.Value);
            }
            var runners = Onnx.getRunnerOptions();
            if (!string.IsNullOrEmpty(prefs.OnnxRunner) && !runners.Contains(prefs.OnnxRunner)) {
                if (options.PrefOverrides.Any(p => p.Key == nameof(prefs.OnnxRunner))) {
                    throw new AgentException("runner_unavailable",
                        $"Runner \"{prefs.OnnxRunner}\" is not available here. Available: {string.Join(", ", runners)}.");
                }
                Warnings.Add($"Runner \"{prefs.OnnxRunner}\" from the preferences is not available here; using CPU.");
                prefs.OnnxRunner = string.Empty;
            }
        }

        static void SetPreference(string key, string value) {
            var type = typeof(Preferences.SerializablePreferences);
            var field = type.GetField(key, BindingFlags.Public | BindingFlags.Instance)
                ?? type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));
            if (field == null) {
                throw AgentException.Usage($"Unknown preference \"{key}\".");
            }
            try {
                field.SetValue(Preferences.Default, ConvertValue(value, field.FieldType));
            } catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException) {
                throw AgentException.Usage($"Cannot set preference {field.Name} ({field.FieldType.Name}) to \"{value}\".");
            }
        }

        static object ConvertValue(string value, Type type) {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(bool)) {
                switch (value.ToLowerInvariant()) {
                    case "true": case "1": case "on": case "yes": return true;
                    case "false": case "0": case "off": case "no": return false;
                    default: throw new FormatException();
                }
            }
            if (type == typeof(string)) {
                return value;
            }
            if (type.IsPrimitive) {
                return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            }
            throw new InvalidCastException();
        }

        public string EffectiveRunner() {
            var runners = Onnx.getRunnerOptions();
            string runner = Preferences.Default.OnnxRunner;
            if (string.IsNullOrEmpty(runner)) {
                return runners[0];
            }
            return runners.Contains(runner) ? runner : "CPU";
        }

        // Once: a search disposes the singers it replaces, and a session may ask many times.
        public void SearchInstalledSingers() {
            if (searched) {
                return;
            }
            searched = true;
            SingerManager.Inst.SearchAllSingers();
            foreach (var singer in added) {
                SingerManager.Inst.Singers[singer.Id] = singer;
            }
        }

        public USinger AddSingerDirectory(string directory) {
            var singer = AgentOps.AddSingerDirectory(directory);
            added.Add(singer);
            return singer;
        }

        public UProject OpenProject(string path) => OpenProject(path, null);

        public void LoadProject(UProject project) {
            Loop.Invoke(() => {
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
                // Loading validated against the previous document; phonemize again now that this one is current.
                project.ValidateFull();
            });
        }

        public void SaveProject(UProject project, string path) => Loop.Invoke(() => AgentOps.Save(project, path));

        // Reads a project and makes it the current document, as File > Open does. configure runs on
        // the project before it is installed, for overrides that are meant to look loaded from the file.
        public UProject OpenProject(string path, Action<UProject>? configure) {
            if (!File.Exists(path)) {
                throw new AgentException("file_not_found", $"Project not found: {path}");
            }
            // Wave parts are found relative to the project's path.
            path = Path.GetFullPath(path);
            return Loop.Invoke(() => {
                UProject? project;
                try {
                    project = Formats.ReadProject(new[] { path });
                } catch (Exception e) {
                    throw new AgentException("invalid_project", $"Failed to read {path}: {e.Message}", inner: e);
                }
                if (project == null) {
                    throw new AgentException("invalid_project", $"Failed to read {path}.");
                }
                configure?.Invoke(project);
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
                // Loading validated against the previous document; phonemize again now that this one is current.
                project.ValidateFull();
                return project;
            });
        }

        public void CheckSingers(UProject project) => AgentOps.CheckSingers(this, project);
        public void WaitPhonemized(UProject project) => AgentOps.WaitPhonemized(this, project);
        public void Rephonemize(UProject project) => AgentOps.Rephonemize(this, project);
        public void LoadRenderedPitch(UProject project, string mode) => AgentOps.LoadRenderedPitch(this, project, mode);
        public List<RenderOutput> Render(UProject project, string outPath, bool mixdown, int trackNo)
            => AgentOps.Render(this, project, outPath, mixdown, trackNo);

        public void OnNext(UCommand cmd, bool isUndo) {
            if (cmd is ErrorMessageNotification error) {
                string message = error.e is MessageCustomizableException mce
                    ? $"{mce.Message} {mce.SubstanceException?.Message}".Trim()
                    : $"{error.message} {error.e?.Message}".Trim();
                sink.AddError(message);
            }
        }

        public void Dispose() {
            DocManager.Inst.RemoveSubscriber(this);
            Loop.Dispose();
            Log.CloseAndFlush();
            sink.Dispose();
            if (ownsCache) {
                try {
                    Directory.Delete(CachePath, true);
                } catch (Exception) {
                    // A leftover temp folder is harmless.
                }
            }
        }

        sealed class LogSink : ILogEventSink, IDisposable {
            const int MaxErrors = 100;
            readonly bool verbose;
            readonly StreamWriter? file;
            readonly List<string> errors = new List<string>();

            public LogSink(bool verbose, string? logFile) {
                this.verbose = verbose;
                if (!string.IsNullOrEmpty(logFile)) {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFile))!);
                    file = new StreamWriter(logFile, false, new UTF8Encoding(false));
                }
            }

            public void Emit(LogEvent logEvent) {
                if (logEvent.Level >= LogEventLevel.Error) {
                    string message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
                    if (logEvent.Exception != null) {
                        message += ": " + logEvent.Exception.Message;
                    }
                    AddError(message);
                }
                bool toStderr = verbose && logEvent.Level >= LogEventLevel.Information;
                bool toFile = file != null && logEvent.Level >= LogEventLevel.Debug;
                if (!toStderr && !toFile) {
                    return;
                }
                string line = $"{logEvent.Timestamp:HH:mm:ss.fff} [{logEvent.Level}] {logEvent.RenderMessage(CultureInfo.InvariantCulture)}";
                if (logEvent.Exception != null) {
                    line += Environment.NewLine + logEvent.Exception;
                }
                lock (errors) {
                    if (toStderr) {
                        Console.Error.WriteLine(line);
                    }
                    if (toFile) {
                        file!.WriteLine(line);
                    }
                }
            }

            public void AddError(string message) {
                lock (errors) {
                    if (errors.Count < MaxErrors && !errors.Contains(message)) {
                        errors.Add(message);
                    }
                }
            }

            public IReadOnlyList<string> Errors(bool take = false) {
                lock (errors) {
                    var result = errors.ToArray();
                    if (take) {
                        errors.Clear();
                    }
                    return result;
                }
            }

            public void Dispose() {
                lock (errors) {
                    file?.Dispose();
                }
            }
        }
    }
}
