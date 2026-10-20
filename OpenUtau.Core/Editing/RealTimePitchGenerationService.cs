using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Serilog;

namespace OpenUtau.Core.Editing {
    /// <summary>
    /// When enabled, regenerates DiffSinger pitch curves (Ctrl+R) after relevant piano roll edits,
    /// and for every part an opened or imported file brings in once it has been phonemized.
    /// </summary>
    public sealed class RealTimePitchGenerationService : ICmdSubscriber {
        public static RealTimePitchGenerationService Inst { get; } = new();

        internal static bool SuppressCallbacks;

        static readonly HashSet<Type> TriggerCommandTypes = new HashSet<Type> {
            typeof(AddNoteCommand),
            typeof(RemoveNoteCommand),
            typeof(ResizeNoteCommand),
            typeof(MoveNoteCommand),
            typeof(PhonemeOffsetCommand),
            typeof(PhonemePreutterCommand),
            typeof(PhonemeOverlapCommand),
            typeof(ClearPhonemeTimingCommand),
            typeof(ChangePhonemeAliasCommand),
        };

        readonly object scheduleLock = new();
        readonly Dictionary<UVoicePart, CancellationTokenSource> debounceTokens = new();
        readonly Dictionary<UVoicePart, HashSet<UNote>> pendingNotesByPart = new();
        readonly HashSet<UVoicePart> lyricPendingParts = new();
        // Parts already in the project, so an import only loads pitch for the parts it adds.
        HashSet<UPart> knownParts = new();
        readonly HashSet<UVoicePart> loadPendingParts = new();
        // Parts an opened or imported file brought in; a singer or phonemizer change fills them in again.
        readonly HashSet<UVoicePart> loadedParts = new();
        CancellationTokenSource loadCts = new();
        UProject? loadedProject;
        // Lets the phonemizer finish every batch for a part before pitch is generated from it.
        const int AfterLoadPhonemizeMs = 300;
        readonly LoadRenderedPitch pitchLoader = new();

        readonly struct RealtimePitchSettings {
            public double PitchSteps { get; init; }
            public int DebounceMs { get; init; }
            public int LyricFallbackMs { get; init; }
            public int AfterPhonemizeMs { get; init; }
            public bool FastRealtime { get; init; }
        }

        static LivePitchMode ActiveMode =>
            (LivePitchMode)Preferences.Default.RealTimePitchMode;

        static bool IsEnabled => ActiveMode != LivePitchMode.Off;

        static RealtimePitchSettings GetSettings() => ActiveMode switch {
            LivePitchMode.Normal => new RealtimePitchSettings {
                PitchSteps = 2,
                DebounceMs = 200,
                LyricFallbackMs = 1200,
                AfterPhonemizeMs = 50,
                FastRealtime = false,
            },
            LivePitchMode.SuperFast => new RealtimePitchSettings {
                PitchSteps = 0.1,
                DebounceMs = 80,
                LyricFallbackMs = 800,
                AfterPhonemizeMs = 30,
                FastRealtime = true,
            },
            _ => default,
        };

        RealTimePitchGenerationService() { }

        public void Initialize() {
            DocManager.Inst.AddSubscriber(this);
        }

        public void OnNext(UCommand cmd, bool isUndo) {
            if (cmd is LoadProjectNotification load) {
                OnProjectLoaded(load.project);
                return;
            }
            if (cmd is PartCommand || cmd is TrackCommand) {
                lock (scheduleLock) {
                    knownParts = DocManager.Inst.Project.parts.ToHashSet();
                }
            }
            // Phrases a phonemizer could not handle get no pitch; once the voice is fixed, fill them in.
            if (cmd is TrackChangeSingerCommand || cmd is TrackChangePhonemizerCommand
                    || cmd is TrackChangeRenderSettingCommand) {
                var track = ((TrackCommand)cmd).track;
                lock (scheduleLock) {
                    if (IsEnabled) {
                        loadPendingParts.UnionWith(loadedParts.Where(p => p.trackNo == track.TrackNo));
                    }
                }
            }
            if (isUndo || SuppressCallbacks || !IsEnabled) {
                return;
            }
            var settings = GetSettings();
            if (cmd is ChangeNoteLyricCommand lyricCmd) {
                lock (scheduleLock) {
                    lyricPendingParts.Add(lyricCmd.Part);
                    TrackAffectedNotes(lyricCmd.Part, lyricCmd.Notes);
                }
                SchedulePart(lyricCmd.Part, settings.LyricFallbackMs);
                return;
            }
            if (cmd is PhonemizedNotification phonemized) {
                bool schedule;
                bool afterLoad;
                lock (scheduleLock) {
                    schedule = lyricPendingParts.Remove(phonemized.part);
                    // A part whose track is not DiffSinger yet (e.g. an imported UST) stays pending until its singer is switched.
                    afterLoad = loadPendingParts.Contains(phonemized.part) && IsDiffSingerPart(phonemized.part);
                }
                if (afterLoad) {
                    SchedulePart(phonemized.part, AfterLoadPhonemizeMs);
                } else if (schedule) {
                    SchedulePart(phonemized.part, settings.AfterPhonemizeMs);
                }
                return;
            }
            if (cmd is NoteCommand noteCmd && TriggerCommandTypes.Contains(cmd.GetType())) {
                lock (scheduleLock) {
                    TrackAffectedNotes(noteCmd.Part, noteCmd.Notes);
                }
                SchedulePart(noteCmd.Part, settings.DebounceMs);
            }
        }

        void OnProjectLoaded(UProject project) {
            lock (scheduleLock) {
                if (project != loadedProject) {
                    loadCts.Cancel();
                    loadCts.Dispose();
                    loadCts = new CancellationTokenSource();
                    loadedProject = project;
                }
                loadPendingParts.RemoveWhere(p => !project.parts.Contains(p));
                loadedParts.RemoveWhere(p => !project.parts.Contains(p));
                foreach (var part in project.parts.OfType<UVoicePart>()) {
                    if (!knownParts.Contains(part) && part.notes.Count > 0) {
                        loadedParts.Add(part);
                        if (IsEnabled) {
                            loadPendingParts.Add(part);
                        }
                    }
                }
                knownParts = project.parts.ToHashSet();
            }
        }

        void TrackAffectedNotes(UVoicePart part, UNote[] notes) {
            if (notes == null || notes.Length == 0) {
                return;
            }
            if (!pendingNotesByPart.TryGetValue(part, out var set)) {
                set = new HashSet<UNote>();
                pendingNotesByPart[part] = set;
            }
            foreach (var note in notes) {
                set.Add(note);
            }
        }

        void SchedulePart(UVoicePart part, int delayMs) {
            if (!IsDiffSingerPart(part)) {
                return;
            }
            CancellationTokenSource cts;
            lock (scheduleLock) {
                if (debounceTokens.TryGetValue(part, out var existing)) {
                    existing.Cancel();
                    existing.Dispose();
                }
                cts = new CancellationTokenSource();
                debounceTokens[part] = cts;
            }
            var token = cts.Token;
            _ = Task.Run(async () => {
                try {
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                } catch (TaskCanceledException) {
                    return;
                }
                if (token.IsCancellationRequested) {
                    return;
                }
                lock (scheduleLock) {
                    if (debounceTokens.TryGetValue(part, out var current) && current == cts) {
                        debounceTokens.Remove(part);
                    }
                }
                RunForPart(part, token);
            });
        }

        void RunForPart(UVoicePart part, CancellationToken cancellationToken) {
            if (cancellationToken.IsCancellationRequested || !IsEnabled) {
                return;
            }
            var settings = GetSettings();
            var project = DocManager.Inst.Project;
            if (!project.parts.Contains(part) || !IsDiffSingerPart(part)) {
                return;
            }
            bool afterLoad;
            CancellationToken loadToken;
            lock (scheduleLock) {
                afterLoad = loadPendingParts.Remove(part);
                loadToken = loadCts.Token;
                if (afterLoad) {
                    pendingNotesByPart.Remove(part);
                }
            }
            if (afterLoad) {
                try {
                    // Full quality and undoable, unlike edit-time live pitch.
                    pitchLoader.RunAfterLoad(project, part, DocManager.Inst, loadToken);
                } catch (Exception e) {
                    Log.Warning(e, "Loading rendered pitch after opening a project failed.");
                }
                return;
            }
            List<UNote> affectedNotes;
            lock (scheduleLock) {
                if (!pendingNotesByPart.TryGetValue(part, out var set) || set.Count == 0) {
                    return;
                }
                affectedNotes = set.ToList();
                pendingNotesByPart.Remove(part);
            }
            affectedNotes = affectedNotes
                .Where(n => n != null)
                .Distinct()
                .ToList();
            if (affectedNotes.Count == 0) {
                return;
            }
            try {
                pitchLoader.RunLive(
                    project, part, affectedNotes,
                    DocManager.Inst,
                    cancellationToken,
                    settings.PitchSteps,
                    settings.FastRealtime);
            } catch (Exception e) {
                Log.Warning(e, "Real-time pitch generation failed.");
            }
        }

        static bool IsDiffSingerPart(UVoicePart part) {
            if (part == null || part.trackNo < 0 || part.trackNo >= DocManager.Inst.Project.tracks.Count) {
                return false;
            }
            var renderer = DocManager.Inst.Project.tracks[part.trackNo].RendererSettings.Renderer;
            return renderer != null
                && renderer.SupportsRenderPitch
                && renderer.SingerType == USingerType.DiffSinger;
        }
    }
}
