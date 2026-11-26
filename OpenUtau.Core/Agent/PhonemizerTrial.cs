using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Agent {
    // Finds a phonemizer by words, or by running the candidates on lyrics outside the project.
    public static class PhonemizerTrial {
        const int MaxListed = 3;

        public static IEnumerable<PhonemizerFactory> Search(IEnumerable<PhonemizerFactory> factories, string query) {
            var words = query.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            return factories
                .Select(f => (Factory: f, Rank: words.Max(w => Rank(f, w))))
                .Where(m => m.Rank > 0)
                .OrderByDescending(m => m.Rank)
                .Select(m => m.Factory);
        }

        // A whole tag or language code counts above a word found somewhere in the id or name.
        static int Rank(PhonemizerFactory f, string word) {
            bool Same(string? s) => string.Equals(s, word, StringComparison.OrdinalIgnoreCase);
            bool Has(string? s) => s != null && s.Contains(word, StringComparison.OrdinalIgnoreCase);
            if (Same(f.tag) || Same(f.language) || Same(f.Id)) {
                return 3;
            }
            if ((f.tag ?? string.Empty).Split(' ').Any(Same)) {
                return 2;
            }
            return Has(f.tag) || Has(f.name) || Has(f.Id) ? 1 : 0;
        }

        // One quarter note per lyric at middle C; "+" holds the syllable before it.
        public static JsonObject Try(PhonemizerFactory factory, USinger singer, IReadOnlyList<string> lyrics) {
            var failed = new List<string>();
            var examples = new JsonObject();
            string? setUpError = null;
            try {
                var project = Format.Ustx.Create();
                var track = project.tracks[0];
                track.Singer = singer;
                var timeAxis = new TimeAxis();
                timeAxis.BuildSegments(project);
                var groups = new List<List<Phonemizer.Note>>();
                for (int i = 0; i < lyrics.Count; i++) {
                    var note = new Phonemizer.Note { lyric = lyrics[i], tone = 60, position = 480 * (i + 4), duration = 480 };
                    if (lyrics[i].StartsWith("+") && groups.Count > 0) {
                        groups[groups.Count - 1].Add(note);
                    } else {
                        groups.Add(new List<Phonemizer.Note> { note });
                    }
                }
                var notes = groups.Select(g => g.ToArray()).ToArray();
                var phonemizer = factory.Create();
                phonemizer.SetSinger(singer);
                phonemizer.SetTiming(timeAxis);
                phonemizer.SetUp(notes, project, track);
                for (int i = 0; i < notes.Length; i++) {
                    string lyric = notes[i][0].lyric;
                    Phonemizer.Phoneme[] phonemes;
                    try {
                        Phonemizer.Note? prev = i > 0 ? notes[i - 1][0] : (Phonemizer.Note?)null;
                        Phonemizer.Note? next = i < notes.Length - 1 ? notes[i + 1][0] : (Phonemizer.Note?)null;
                        phonemes = phonemizer.Process(notes[i], prev, next, prev, next, i > 0 ? notes[i - 1] : new Phonemizer.Note[0]).phonemes;
                    } catch (Exception) {
                        failed.Add(lyric);
                        continue;
                    }
                    if (phonemes == null || phonemes.Length == 0 || phonemes.Any(p => p.error != null || !singer.TryGetOto(p.phoneme, out _))) {
                        failed.Add(lyric);
                    } else if (examples.Count < MaxListed && !examples.ContainsKey(lyric)) {
                        examples[lyric] = string.Join(" ", phonemes.Select(p => p.phoneme));
                    }
                }
                phonemizer.CleanUp();
            } catch (Exception e) {
                setUpError = e.Message;
            }
            var result = new JsonObject {
                ["sung"] = setUpError != null ? 0 : lyrics.Count(l => !l.StartsWith("+")) - failed.Count,
                ["failed"] = new JsonArray(failed.Distinct().Take(MaxListed).Select(l => (JsonNode)l).ToArray()),
                ["phonemes"] = examples,
            };
            if (setUpError != null) {
                result["error"] = setUpError;
            }
            return result;
        }
    }
}
