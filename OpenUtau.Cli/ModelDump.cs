using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using OpenUtau.Core;
using OpenUtau.Core.Agent;
using OpenUtau.Core.DiffSinger;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Cli {
    // Saves what each DiffSinger model was fed, and what it returned, as one .npz per model run.
    sealed class ModelDump : IDisposable {
        public const string ManifestName = "manifest.json";
        const string InputPrefix = "in.";
        const string OutputPrefix = "out.";

        sealed class Entry {
            public string File = string.Empty;
            public string Stage = string.Empty;
            public int? Part;
            public int? Phrase;
            public int? Sentence;
            public int? Tick;
            public bool Cached;
            public int? RunIndex;
            public int Runs = 1;
            public List<NpyArray> Arrays = new List<NpyArray>();
        }

        sealed class Sentence {
            public string Stage = string.Empty;
            public object? Source;
            public int Tick;
            public bool Cached;
            public List<NpyArray> Arrays = new List<NpyArray>();
        }

        readonly string directory;
        readonly UProject project;
        readonly object gate = new object();
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        readonly List<Sentence> sentences = new List<Sentence>();
        // A model's random nodes keep drawing from one stream, so what a run gets depends on how many
        // runs of the same session came before it.
        readonly Dictionary<(string Singer, string Stage), int> runCounts = new Dictionary<(string, string), int>();
        // Tensor cache entries that existed before a run asked for them: that run was served from the cache.
        readonly HashSet<string> cacheFiles;
        int unknown;
        Exception? failure;

        // The phonemizer runs several times while a project loads; only the pass taken with this set is kept.
        public volatile bool RecordPhonemizer;

        public ModelDump(string directory, UProject project) {
            this.directory = Path.GetFullPath(directory);
            this.project = project;
            Directory.CreateDirectory(this.directory);
            cacheFiles = Directory.Exists(PathManager.Inst.CachePath)
                ? Directory.EnumerateFiles(PathManager.Inst.CachePath, "*.tensorcache").Select(Path.GetFileName).ToHashSet()!
                : new HashSet<string>();
            DiffSingerTap.Handler = Handle;
        }

        void Handle(DiffSingerTapEvent e) {
            try {
                if (e.Phrase == null) {
                    if (!RecordPhonemizer) {
                        return;
                    }
                    var sentence = new Sentence {
                        Stage = e.Stage, Source = e.Source, Tick = e.Tick, Cached = IsCached(e), Arrays = Copy(e),
                    };
                    lock (gate) {
                        sentences.Add(sentence);
                    }
                    return;
                }
                var entry = new Entry { Stage = e.Stage, Cached = IsCached(e), Arrays = Copy(e) };
                if (!entry.Cached) {
                    lock (gate) {
                        var key = (e.Phrase.singer?.Id ?? string.Empty, e.Stage);
                        runCounts.TryGetValue(key, out int count);
                        runCounts[key] = count + 1;
                        entry.RunIndex = count;
                    }
                }
                if (Locate(e.Phrase, out int part, out int phrase)) {
                    entry.Part = part;
                    entry.Phrase = phrase;
                    entry.File = $"part{part:00}_phrase{phrase:000}.{e.Stage}.npz";
                } else {
                    lock (gate) {
                        entry.File = $"unknown{unknown++:000}.{e.Stage}.npz";
                    }
                }
                Add(entry);
            } catch (Exception ex) {
                lock (gate) {
                    failure ??= ex;
                }
            }
        }

        bool IsCached(DiffSingerTapEvent e) {
            if (e.Cached || e.CacheFile == null) {
                return e.Cached;
            }
            lock (gate) {
                return !cacheFiles.Add(e.CacheFile);
            }
        }

        static List<NpyArray> Copy(DiffSingerTapEvent e) {
            var arrays = new List<NpyArray>();
            foreach (var value in e.Inputs) {
                AddArray(arrays, InputPrefix + value.Name, value);
            }
            if (e.Outputs != null) {
                foreach (var value in e.Outputs) {
                    AddArray(arrays, OutputPrefix + value.Name, value);
                }
            }
            return arrays;
        }

        static void AddArray(List<NpyArray> arrays, string name, NamedOnnxValue value) {
            var array = NpyArray.From(name, value);
            if (array == null) {
                throw new NotSupportedException($"Cannot dump tensor \"{value.Name}\": unsupported element type.");
            }
            arrays.Add(array);
        }

        bool Locate(RenderPhrase phrase, out int partIndex, out int phraseIndex) {
            for (int i = 0; i < project.parts.Count; i++) {
                if (!(project.parts[i] is UVoicePart part)) {
                    continue;
                }
                lock (part) {
                    int index = part.renderPhrases.IndexOf(phrase);
                    if (index >= 0) {
                        partIndex = i;
                        phraseIndex = index;
                        return true;
                    }
                }
            }
            partIndex = phraseIndex = -1;
            return false;
        }

        void Add(Entry entry) {
            lock (gate) {
                if (entries.TryGetValue(entry.File, out var existing)) {
                    entry.Runs = existing.Runs + 1;
                    // A cache hit carries no more than the run that filled the cache.
                    if (entry.Cached && !existing.Cached) {
                        existing.Runs = entry.Runs;
                        return;
                    }
                }
                entries[entry.File] = entry;
            }
            NpzWriter.Write(Path.Combine(directory, entry.File), entry.Arrays);
        }

        // Call on the main thread once rendering is done. Returns the manifest path.
        public string Finish() {
            DiffSingerTap.Handler = null;
            Sentence[] pending;
            lock (gate) {
                if (failure != null) {
                    throw new AgentException("dump_failed", $"Failed to dump model inputs: {failure.Message}", inner: failure);
                }
                pending = sentences.ToArray();
                sentences.Clear();
            }
            var byPart = pending
                .Select(s => (Sentence: s, Part: PartOf(s)))
                .GroupBy(x => x.Part)
                .OrderBy(g => g.Key);
            foreach (var group in byPart) {
                var ticks = group.Select(x => x.Sentence.Tick).Distinct().OrderBy(t => t).ToList();
                foreach (var (sentence, part) in group) {
                    int index = ticks.IndexOf(sentence.Tick);
                    Add(new Entry {
                        Stage = sentence.Stage,
                        Part = part < 0 ? (int?)null : part,
                        Sentence = index,
                        Tick = sentence.Tick,
                        Cached = sentence.Cached,
                        Arrays = sentence.Arrays,
                        File = part < 0
                            ? $"unknown_sentence{index:000}.{sentence.Stage}.npz"
                            : $"part{part:00}_sentence{index:000}.{sentence.Stage}.npz",
                    });
                }
            }

            var manifest = new JsonObject {
                ["format"] = 1,
                ["models"] = Models(),
                ["entries"] = new JsonArray(entries.Values
                    .OrderBy(e => e.File, StringComparer.Ordinal)
                    .Select(e => (JsonNode)EntryJson(e))
                    .ToArray()),
            };
            string path = Path.Combine(directory, ManifestName);
            File.WriteAllText(path, manifest.ToJsonString(Program.Indented) + "\n", new UTF8Encoding(false));
            return path;
        }

        // The phonemizer knows notes, not parts: find the part by the track it serves and the first note.
        int PartOf(Sentence sentence) {
            int track = project.tracks.FindIndex(t => ReferenceEquals(t.Phonemizer, sentence.Source));
            for (int i = 0; i < project.parts.Count; i++) {
                if (project.parts[i] is UVoicePart part
                        && (track < 0 || part.trackNo == track)
                        && part.notes.Any(n => part.position + n.position == sentence.Tick)) {
                    return i;
                }
            }
            return -1;
        }

        static JsonObject EntryJson(Entry entry) {
            var inputs = new JsonObject();
            var outputs = new JsonObject();
            foreach (var array in entry.Arrays) {
                var info = new JsonObject {
                    ["dtype"] = array.Dtype,
                    ["shape"] = new JsonArray(array.Shape.Select(d => (JsonNode)d).ToArray()),
                };
                if (array.Name.StartsWith(InputPrefix)) {
                    inputs[array.Name.Substring(InputPrefix.Length)] = info;
                } else {
                    outputs[array.Name.Substring(OutputPrefix.Length)] = info;
                }
            }
            return new JsonObject {
                ["file"] = entry.File,
                ["stage"] = entry.Stage,
                ["part"] = entry.Part,
                ["phrase"] = entry.Phrase,
                ["sentence"] = entry.Sentence,
                ["tick"] = entry.Tick,
                ["cached"] = entry.Cached,
                ["run_index"] = entry.RunIndex,
                ["runs"] = entry.Runs,
                ["inputs"] = inputs,
                ["outputs"] = outputs,
            };
        }

        // The model file behind each stage, per track, so a dump can be replayed.
        JsonArray Models() {
            var result = new JsonArray();
            for (int i = 0; i < project.tracks.Count; i++) {
                if (!(project.tracks[i].Singer is DiffSingerSinger singer)) {
                    continue;
                }
                var stages = new JsonObject();
                AddPredictor(stages, singer.Location, "dsdur", DiffSingerTap.DurLinguistic, DiffSingerTap.Dur, c => c.dur);
                AddPredictor(stages, singer.Location, "dspitch", DiffSingerTap.PitchLinguistic, DiffSingerTap.Pitch, c => c.pitch);
                AddPredictor(stages, singer.Location, "dsvariance", DiffSingerTap.VarianceLinguistic, DiffSingerTap.Variance, c => c.variance);
                if (!string.IsNullOrEmpty(singer.dsConfig.acoustic)) {
                    stages[DiffSingerTap.Acoustic] = Path.GetFullPath(Path.Combine(singer.Location, singer.dsConfig.acoustic));
                }
                if (singer.IsVocoderLoaded) {
                    var vocoder = singer.getVocoder();
                    stages[DiffSingerTap.Vocoder] = Path.GetFullPath(Path.Combine(vocoder.Location, vocoder.config.model));
                }
                result.Add(new JsonObject {
                    ["track"] = i,
                    ["singer_id"] = singer.Id,
                    ["singer_path"] = singer.Location,
                    ["stages"] = stages,
                });
            }
            return result;
        }

        static void AddPredictor(JsonObject stages, string singerPath, string folder,
                string linguisticStage, string modelStage, Func<DsConfig, string> model) {
            string root = Path.Combine(singerPath, folder);
            string configPath = Path.Combine(root, "dsconfig.yaml");
            if (!File.Exists(configPath)) {
                return;
            }
            var config = Yaml.DefaultDeserializer.Deserialize<DsConfig>(File.ReadAllText(configPath, Encoding.UTF8));
            if (!string.IsNullOrEmpty(config.linguistic)) {
                stages[linguisticStage] = Path.GetFullPath(Path.Combine(root, config.linguistic));
            }
            if (!string.IsNullOrEmpty(model(config))) {
                stages[modelStage] = Path.GetFullPath(Path.Combine(root, model(config)));
            }
        }

        public void Dispose() {
            DiffSingerTap.Handler = null;
        }
    }
}
