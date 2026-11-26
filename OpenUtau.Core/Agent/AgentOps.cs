using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenUtau.Classic;
using OpenUtau.Core.DiffSinger;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace OpenUtau.Core.Agent {
    public sealed class RenderOutput {
        public int Track;
        public string Path = string.Empty;
        public int Channels;
        public int SampleRate;
        public int BitsPerSample;
        public long Samples;
        public string Sha256 = string.Empty;
    }

    // Document work that is the same with and without a window. Call from a thread other than the host's.
    public static class AgentOps {
        static readonly TimeSpan PhonemizeTimeout = TimeSpan.FromMinutes(30);

        public const string PitchNone = "none";
        public const string PitchUntuned = "untuned";
        public const string PitchAll = "all";

        // Registers a voicebank folder under its folder name, as if it were installed.
        public static USinger AddSingerDirectory(string directory) {
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string charTxt = Path.Combine(dir, VoicebankLoader.kCharTxt);
            if (!File.Exists(charTxt)) {
                throw new AgentException("singer_not_found", $"Not a voicebank folder (no {VoicebankLoader.kCharTxt}): {dir}");
            }
            var voicebank = new Voicebank();
            try {
                VoicebankLoader.LoadInfo(voicebank, charTxt, Path.GetDirectoryName(dir) ?? dir);
            } catch (Exception e) {
                throw new AgentException("singer_not_found", $"Failed to read the voicebank in {dir}: {e.Message}", inner: e);
            }
            USinger singer;
            switch (voicebank.SingerType) {
                case USingerType.Enunu:
                    singer = new Enunu.EnunuSinger(voicebank);
                    break;
                case USingerType.DiffSinger:
                    singer = new DiffSingerSinger(voicebank);
                    break;
                case USingerType.Voicevox:
                    singer = new Voicevox.VoicevoxSinger(voicebank);
                    break;
                default:
                    singer = new ClassicSinger(voicebank);
                    break;
            }
            SingerManager.Inst.Singers[singer.Id] = singer;
            return singer;
        }

        public static void CheckSingers(IAgentHost host, UProject project, int track = -1) {
            host.Invoke(() => {
                foreach (var part in project.parts.OfType<UVoicePart>().Where(p => p.notes.Count > 0 && (track < 0 || p.trackNo == track))) {
                    var owner = project.tracks[part.trackNo];
                    var singer = owner.Singer;
                    if (singer == null || !singer.Found) {
                        throw new AgentException("singer_not_found",
                            $"Track {part.trackNo}: singer \"{singer?.Name ?? owner.singer}\" is not installed.");
                    }
                    if (!singer.Loaded) {
                        throw new AgentException("singer_not_loaded",
                            $"Track {part.trackNo}: singer \"{singer.Id}\" failed to load: {string.Join("; ", singer.Errors ?? Array.Empty<string>())}");
                    }
                }
            });
        }

        // Returns once every part has the phonemes and phrases of its current notes.
        public static void WaitPhonemized(IAgentHost host, UProject project) {
            var deadline = DateTime.UtcNow + PhonemizeTimeout;
            while (true) {
                bool done = host.Invoke(() => project.parts.OfType<UVoicePart>().All(p => p.PhonemesUpToDate));
                if (done) {
                    break;
                }
                if (DateTime.UtcNow > deadline) {
                    throw new AgentException("timeout", "Timed out waiting for the phonemizer.");
                }
                Thread.Sleep(10);
            }
            var parts = host.Invoke(() => project.parts.OfType<UVoicePart>().ToArray());
            foreach (var part in parts) {
                if (!part.WaitPhraseSource(PhonemizeTimeout)) {
                    throw new AgentException("timeout", "Timed out waiting for the phrases to build.");
                }
            }
            host.Invoke(() => { });
        }

        // Runs the phonemizer once more; with a tap attached this captures exactly one pass.
        public static void Rephonemize(IAgentHost host, UProject project) {
            host.Invoke(() => project.ValidateFull());
            WaitPhonemized(host, project);
        }

        // "all" is the piano roll's Load Rendered Pitch on a whole part; "untuned" is what live pitch
        // does to a freshly opened project, leaving phrases that already have pitch alone.
        public static void LoadRenderedPitch(IAgentHost host, UProject project, string mode, UVoicePart? only = null, int track = -1) {
            var parts = host.Invoke(() => project.parts.OfType<UVoicePart>()
                .Where(p => p.notes.Count > 0 && (only == null || p == only) && (track < 0 || p.trackNo == track)).ToArray());
            foreach (var part in parts) {
                var renderer = host.Invoke(() => project.tracks[part.trackNo].RendererSettings.Renderer);
                if (renderer == null || !renderer.SupportsRenderPitch) {
                    throw new AgentException("load_pitch_failed",
                        $"Track {part.trackNo}: renderer {renderer} cannot generate pitch.");
                }
                var edit = new Editing.LoadRenderedPitch();
                try {
                    if (mode == PitchAll) {
                        edit.Run(project, part, new List<UNote>(), DocManager.Inst);
                    } else {
                        edit.RunAfterLoad(project, part, DocManager.Inst, CancellationToken.None);
                    }
                } catch (Exception e) {
                    throw new AgentException("load_pitch_failed", $"Load rendered pitch failed: {e.Message}", inner: e);
                }
            }
            // The edit commits on the main thread, in its turn, and waits out an edit the user has in progress.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            do {
                using var applied = new ManualResetEventSlim();
                DocManager.Inst.PostOnUIThread(applied.Set);
                applied.Wait(PhonemizeTimeout);
            } while (host.Invoke(() => DocManager.Inst.HasOpenUndoGroup) && DateTime.UtcNow < deadline && Wait(50));
            WaitPhonemized(host, project);
        }

        static bool Wait(int ms) {
            Thread.Sleep(ms);
            return true;
        }

        // As File > Save: the app's own save, which also marks the undo history as saved. Main thread.
        public static void Save(UProject project, string path) {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!DocManager.Inst.SaveChecked(path) || !File.Exists(path)) {
                throw new AgentException("save_failed", $"Failed to save {path}.");
            }
        }

        // endTick -1 renders to the end. With a range, only the phrases that reach into it are rendered,
        // each one whole, and the file is cut to the range.
        public static List<RenderOutput> Render(IAgentHost host, UProject project, string outPath, bool mixdown,
                int trackNo, int startTick = 0, int endTick = -1) {
            outPath = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var engine = new RenderEngine(project, startTick, endTick, trackNo);
            var (startMs, endMs) = host.Invoke(() => (
                project.timeAxis.TickPosToMsPos(startTick),
                endTick < 0 ? double.PositiveInfinity : project.timeAxis.TickPosToMsPos(endTick)));
            ISampleProvider Cut(ISampleProvider source) {
                if (startTick <= 0 && endTick < 0) {
                    return source;
                }
                var format = source.WaveFormat;
                int skip = (int)Math.Round(startMs / 1000 * format.SampleRate);
                var cut = new OffsetSampleProvider(source) { SkipOverSamples = skip * format.Channels };
                if (!double.IsPositiveInfinity(endMs)) {
                    cut.TakeSamples = ((int)Math.Round(endMs / 1000 * format.SampleRate) - skip) * format.Channels;
                }
                return cut;
            }
            var planner = new MixPlanner();
            CancellationTokenSource? cancellation = null;
            var outputs = new List<RenderOutput>();
            try {
                if (mixdown) {
                    // Wave parts load in the background; the mix only takes the loaded ones.
                    Task.WaitAll(host.Invoke(() => project.parts.OfType<UWavePart>().Select(p => (Task)p.Peaks).Where(t => t != null).ToArray()));
                    var mix = engine.RenderMixdown(host.Scheduler, ref cancellation!, wait: true,
                        applyMixFx: Preferences.Default.MixFxApplyOnExportMixdown, planner).Item1;
                    WaveFileWriter.CreateWaveFile16(outPath, Cut(new ExportAdapter(mix)));
                    outputs.Add(Describe(outPath, -1));
                } else {
                    var trackMixes = engine.RenderTracks(host.Scheduler, ref cancellation!, planner);
                    var tracks = host.Invoke(() => project.tracks.ToArray());
                    var exported = Enumerable.Range(0, trackMixes.Count)
                        .Where(i => trackMixes[i] != null && i < tracks.Length && !tracks[i].Muted)
                        .ToArray();
                    foreach (int i in exported) {
                        string file = exported.Length == 1 ? outPath : PathManager.Inst.GetExportPath(outPath, tracks[i]);
                        WaveFileWriter.CreateWaveFile16(file, Cut(new ExportAdapter(trackMixes[i])).ToMono(1, 0));
                        outputs.Add(Describe(file, i));
                    }
                }
            } catch (AgentException) {
                throw;
            } catch (Exception e) {
                var inner = e is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.First() : e;
                while (inner.InnerException != null && (inner is AggregateException || inner is TargetInvocationException)) {
                    inner = inner.InnerException;
                }
                throw new AgentException("render_failed", $"Render failed: {inner.Message}", inner: e);
            }
            if (outputs.Count == 0) {
                throw new AgentException("nothing_to_render", "The project has nothing to render.");
            }
            return outputs;
        }

        static RenderOutput Describe(string path, int track) {
            using var reader = new WaveFileReader(path);
            int blockAlign = reader.WaveFormat.BlockAlign;
            var output = new RenderOutput {
                Track = track,
                Path = path,
                Channels = reader.WaveFormat.Channels,
                SampleRate = reader.WaveFormat.SampleRate,
                BitsPerSample = reader.WaveFormat.BitsPerSample,
                Samples = blockAlign == 0 ? 0 : reader.Length / blockAlign,
            };
            using var stream = File.OpenRead(path);
            output.Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return output;
        }
    }
}
