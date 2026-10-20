#nullable disable
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OpenUtau.Api;

namespace DiffSingerLatin {
    // Port of work/latin_g2p.py: Ecclesiastical (Liber Usualis) Latin spelling to bank-neutral symbols; dsdict-la.yaml maps them to the voicebank.
    public class LatinG2p : IG2p {
        static readonly Dictionary<char, string> Vowels = new() {
            ['a'] = "a", ['e'] = "e", ['i'] = "i", ['o'] = "o", ['u'] = "u", ['y'] = "i", ['ë'] = "e", ['ï'] = "i", ['ü'] = "u",
        };
        static readonly string[] Glides = { "u_", "i_" };
        static readonly string[] Consonants = {
            "p", "b", "t", "d", "k", "g", "f", "v", "s", "z", "l", "m", "n", "r", "rr", "ch", "dj", "sh", "ny", "ts", "dz", "j", "w", "cl",
        };
        // Traditional exceptions: h read as k; eu and ui as diphthongs only in these words (Deus, tuis stay two syllables).
        static readonly Dictionary<string, string> Respell = new() { ["mihi"] = "miki", ["nihil"] = "nikil", ["nihilum"] = "nikilum" };
        static readonly HashSet<string> EuDiph = new() { "heu", "eheu", "euge" };
        static readonly HashSet<string> UiDiph = new() { "cui", "huic" };
        public static readonly string[] VowelSymbols = Vowels.Values.Distinct().ToArray();
        public static readonly string[] ConsonantSymbols = Glides.Concat(Consonants).ToArray();
        static readonly HashSet<string> All = new(VowelSymbols.Concat(ConsonantSymbols));

        public bool IsValidSymbol(string symbol) => All.Contains(symbol);
        public bool IsVowel(string symbol) => VowelSymbols.Contains(symbol);
        public bool IsGlide(string symbol) => symbol == "j" || symbol == "w";
        public string[] UnpackHint(string hint, char separator = ' ') => hint.Split(separator).Where(All.Contains).ToArray();

        public static string Normalize(string word) {
            // Chant books mark stress with acute accents and ae/oe with ligatures; a diaeresis (Israël) keeps the vowel apart.
            var sb = new StringBuilder();
            foreach (var c in word.ToLowerInvariant().Normalize(NormalizationForm.FormD)) {
                if ("̀́̂̄̆".IndexOf(c) < 0) sb.Append(c);
            }
            var s = sb.ToString().Normalize(NormalizationForm.FormC).Replace("æ", "ae").Replace("œ", "oe");
            return Respell.TryGetValue(s, out var r) ? r : s;
        }

        public string[] Query(string grapheme) {
            if (string.IsNullOrWhiteSpace(grapheme)) return null;
            var w = Normalize(grapheme);
            int n = w.Length;
            char At(int j) => j >= 0 && j < n ? w[j] : '\0';
            bool Vowel(int j) => Vowels.ContainsKey(At(j));
            bool Two(int j, string s) => j >= 0 && j + 1 < n && w[j] == s[0] && w[j + 1] == s[1];
            // ae/oe read e, so they soften c/g/sc like e and i do.
            bool Front(int j) => "eiyëï".IndexOf(At(j)) >= 0 && At(j) != '\0' || Two(j, "ae") || Two(j, "oe");
            bool ConsU(int j) => At(j) == 'u' && (At(j - 1) == 'q' || (At(j - 1) == 'g' && At(j - 2) == 'n' && Vowel(j + 1)));
            bool ConsI(int j) {
                // Initial i before a vowel (iam, Iesus) and i between vowels (eius, alleluia) is j.
                if (At(j) != 'i' || !Vowel(j + 1) || At(j + 1) == 'i' || At(j + 1) == 'ï') return false;
                return j == 0 || (Vowel(j - 1) && !ConsU(j - 1));
            }
            if (!w.Any(c => Vowels.ContainsKey(c))) return null;
            var o = new List<string>();
            int i = 0;
            while (i < n) {
                char c = w[i], nx = At(i + 1);
                if (c == 'j' || ConsI(i)) { o.Add("j"); i += 1; }
                else if (Vowels.ContainsKey(c)) {
                    if ((c == 'a' || c == 'o') && nx == 'e') { o.Add("e"); i += 2; }
                    else if (c == 'a' && nx == 'u') { o.Add("a"); o.Add("u_"); i += 2; }
                    else if (c == 'e' && nx == 'u' && (i == 0 || EuDiph.Contains(w))) { o.Add("e"); o.Add("u_"); i += 2; }
                    else if (c == 'u' && nx == 'i' && UiDiph.Contains(w)) { o.Add("u"); o.Add("i_"); i += 2; }
                    else { o.Add(Vowels[c]); i += 1; }
                } else if (c == 'h') { i += 1; }
                else if (c == 'c') {
                    if (nx == 'c') {
                        if (Front(i + 2)) { o.Add("cl"); o.Add("ch"); i += 2; }
                        else { o.Add("cl"); o.Add("k"); i += At(i + 2) == 'h' ? 3 : 2; }
                    } else if (nx == 'h') { o.Add("k"); i += 2; }
                    else { o.Add(Front(i + 1) ? "ch" : "k"); i += 1; }
                } else if (c == 's') {
                    if (nx == 'c' && Front(i + 2)) { o.Add("sh"); i += 2; }
                    else { o.Add("s"); i += nx == 's' ? 2 : 1; }
                } else if (c == 'x') {
                    if (nx == 'c' && Front(i + 2)) { o.Add("k"); o.Add("sh"); i += 2; }
                    // ex- before a vowel (exaudi, exsultet) is voiced.
                    else if (i == 1 && w[0] == 'e' && (Vowel(i + 1) || nx == 'h')) { o.Add("g"); o.Add("z"); i += 1; }
                    else if (i == 1 && w[0] == 'e' && nx == 's' && Vowel(i + 2)) { o.Add("g"); o.Add("z"); i += 2; }
                    // The s after x is absorbed (exspecto = ekspekto).
                    else { o.Add("k"); o.Add("s"); i += nx == 's' ? 2 : 1; }
                } else if (c == 'g') {
                    if (nx == 'n') { o.Add("ny"); i += 2; }
                    else if (nx == 'g') { o.Add("cl"); o.Add(Front(i + 2) ? "dj" : "g"); i += 2; }
                    else if (ConsU(i + 1)) { o.Add("g"); o.Add("w"); i += 2; }
                    else { o.Add(Front(i + 1) ? "dj" : "g"); i += 1; }
                } else if (c == 'q') {
                    o.Add("k");
                    if (nx == 'u') { o.Add("w"); i += 2; } else i += 1;
                } else if (c == 't') {
                    if (nx == 'h') { o.Add("t"); i += 2; }
                    // ti before a vowel is tsi, except after s, t, x (hostia) and at the start of a word.
                    else if (nx == 'i' && Vowel(i + 2) && i > 0 && "stx".IndexOf(At(i - 1)) < 0) { o.Add("ts"); i += 1; }
                    else if (nx == 't') { o.Add("cl"); o.Add("t"); i += 2; }
                    else { o.Add("t"); i += 1; }
                } else if (c == 'p') {
                    if (nx == 'h') { o.Add("f"); i += 2; }
                    else if (nx == 'p') { o.Add("cl"); o.Add("p"); i += 2; }
                    else { o.Add("p"); i += 1; }
                } else if (c == 'r') {
                    if (nx == 'h') { o.Add("r"); i += 2; }
                    else if (nx == 'r') { o.Add("rr"); i += 2; }
                    else { o.Add("r"); i += 1; }
                } else if (c == 'b' || c == 'd' || c == 'k') {
                    if (nx == c) { o.Add("cl"); o.Add(c.ToString()); i += 2; }
                    else { o.Add(c.ToString()); i += 1; }
                } else if (c == 'z') { o.Add("dz"); i += nx == 'z' ? 2 : 1; }
                else if ("fvwlmn".IndexOf(c) >= 0) {
                    // Doubled continuants stay one phoneme; the note gives the length.
                    o.Add(c == 'w' ? "v" : c.ToString()); i += nx == c ? 2 : 1;
                } else { i += 1; }
            }
            return o.Count > 0 ? o.ToArray() : null;
        }
    }
}
