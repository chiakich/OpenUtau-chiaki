using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core.Editing {
    // Fork-only: once phonemized, every part an opened or imported file brings in gets its pitch loaded.
    public sealed partial class RealTimePitchGenerationService {
        // Parts already in the project, so an import only loads pitch for the parts it adds.
        HashSet<UPart> knownParts = new();
        readonly HashSet<UVoicePart> loadPendingParts = new();
        // Parts an opened or imported file brought in; a singer or phonemizer change fills them in again.
        readonly HashSet<UVoicePart> loadedParts = new();
        CancellationTokenSource loadCts = new();
        UProject? loadedProject;
        // Lets the phonemizer finish every batch for a part before pitch is generated from it.
        const int AfterLoadPhonemizeMs = 300;

        // Returns true when the command is fully handled here.
        bool HandleAfterLoadCommand(UCommand cmd) {
            if (cmd is LoadProjectNotification load) {
                OnProjectLoaded(load.project);
                return true;
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
            return false;
        }

        // Caller holds scheduleLock. A part whose track is not DiffSinger yet (e.g. an imported UST)
        // stays pending until its singer is switched.
        bool IsAfterLoadPending(UVoicePart part) {
            return loadPendingParts.Contains(part) && IsDiffSingerPart(part);
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

        bool TryRunAfterLoad(UProject project, UVoicePart part) {
            bool afterLoad;
            CancellationToken loadToken;
            lock (scheduleLock) {
                afterLoad = loadPendingParts.Remove(part);
                loadToken = loadCts.Token;
                if (afterLoad) {
                    pendingNotesByPart.Remove(part);
                }
            }
            if (!afterLoad) {
                return false;
            }
            try {
                // Full quality and undoable, unlike edit-time live pitch.
                pitchLoader.RunAfterLoad(project, part, DocManager.Inst, loadToken);
            } catch (Exception e) {
                Log.Warning(e, "Loading rendered pitch after opening a project failed.");
            }
            return true;
        }
    }
}
