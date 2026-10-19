#nullable disable
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Api;

namespace DiffSingerFinnish {
    // Port of work/finnish_g2p.py: Finnish spelling to bank-neutral symbols; dsdict-fi.yaml maps them to the voicebank.
    public class FinnishG2p : IG2p {
        static readonly Dictionary<char, string> Vowels = new() {
            ['a'] = "a", ['e'] = "e", ['i'] = "i", ['o'] = "o", ['u'] = "u", ['y'] = "y", ['ä'] = "ae", ['ö'] = "oe", ['å'] = "o",
        };
        // Second element of a diphthong is non-syllabic so a syllable carries one vowel.
        static readonly Dictionary<char, string> Glides = new() {
            ['i'] = "i_", ['u'] = "u_", ['y'] = "y_", ['e'] = "e_", ['o'] = "o_", ['ö'] = "oe_",
        };
        static readonly HashSet<string> Falling = new() { "ai", "ei", "oi", "ui", "yi", "äi", "öi", "au", "eu", "iu", "ou", "ey", "äy", "öy", "iy" };
        static readonly HashSet<string> Rising = new() { "ie", "uo", "yö" };
        static readonly Dictionary<char, string> Consonants = new() {
            ['b'] = "b", ['c'] = "k", ['d'] = "d", ['f'] = "f", ['g'] = "g", ['h'] = "h", ['j'] = "j", ['k'] = "k", ['l'] = "l", ['m'] = "m",
            ['n'] = "n", ['p'] = "p", ['q'] = "k", ['r'] = "r", ['s'] = "s", ['t'] = "t", ['v'] = "v", ['w'] = "v", ['z'] = "ts", ['š'] = "sh", ['ž'] = "zh",
        };
        public static readonly string[] VowelSymbols = Vowels.Values.Distinct().ToArray();
        public static readonly string[] ConsonantSymbols = Glides.Values.Concat(Consonants.Values).Concat(new[] { "ng", "rr", "cl" }).Distinct().ToArray();
        static readonly HashSet<string> All = new(VowelSymbols.Concat(ConsonantSymbols));

        public bool IsValidSymbol(string symbol) => All.Contains(symbol);
        public bool IsVowel(string symbol) => VowelSymbols.Contains(symbol);
        public bool IsGlide(string symbol) => symbol == "j" || symbol == "v";
        public string[] UnpackHint(string hint, char separator = ' ') => hint.Split(separator).Where(All.Contains).ToArray();

        public string[] Query(string grapheme) {
            if (string.IsNullOrWhiteSpace(grapheme)) return null;
            var w = grapheme.ToLowerInvariant();
            if (!w.Any(c => Vowels.ContainsKey(c) || Consonants.ContainsKey(c))) return null;
            var output = new List<string>();
            int i = 0;
            bool first = true;
            while (i < w.Length) {
                char c = w[i];
                char n = i + 1 < w.Length ? w[i + 1] : '\0';
                if (Vowels.ContainsKey(c)) {
                    output.Add(Vowels[c]);
                    int j = i + 1;
                    if (n == c) j++;  // long vowel: one phoneme, length comes from the note
                    char m = j < w.Length ? w[j] : '\0';
                    var pair = $"{c}{m}";
                    if (m != '\0' && (Falling.Contains(pair) || (first && Rising.Contains(pair)))) {
                        output.Add(Glides[m]);
                        j++;
                        if (j < w.Length && w[j] == m) j++;
                    }
                    first = false;
                    i = j;
                } else if (c == 'n' && n == 'g') {
                    output.Add("ng"); i += 2;
                } else if (c == 'n' && n == 'k') {
                    output.Add("ng"); i += 1;
                } else if (c == 'x') {
                    output.Add("k"); output.Add("s"); i += 1;
                } else if (Consonants.ContainsKey(c)) {
                    if (n == c) {
                        // Geminate stops get a closure; rr is a longer trill; other doubles stay single.
                        if ("kpt".IndexOf(c) >= 0) { output.Add("cl"); output.Add(Consonants[c]); }
                        else if (c == 'r') output.Add("rr");
                        else output.Add(Consonants[c]);
                        i += 2;
                    } else {
                        output.Add(Consonants[c]); i += 1;
                    }
                } else {
                    i += 1;
                }
            }
            return output.Count > 0 ? output.ToArray() : null;
        }
    }
}
