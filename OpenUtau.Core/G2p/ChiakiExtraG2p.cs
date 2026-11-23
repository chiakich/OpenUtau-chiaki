#nullable disable
// Rule tables shared with the Python G2P (work/extra_langs.py), ported from the Chiaki Extra Languages plugin.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiffSingerChiakiExtra {
    // Port of work/extra_langs.py G2p: regex rewrites, then longest-match graphemes, from the shared rules.json.
    public class RuleTable {
        public string Name;
        public string[] Vowels;
        public string[] Consonants;
        readonly List<(Regex, string)> pre = new();
        readonly Dictionary<string, string[]> map = new();
        readonly int maxLen;

        static Dictionary<string, RuleTable> tables;

        public static RuleTable Get(string code) {
            if (tables == null) {
                using var s = typeof(RuleTable).Assembly.GetManifestResourceStream("DiffSingerChiakiExtra.rules.json");
                using var r = new StreamReader(s, Encoding.UTF8);
                var doc = JsonDocument.Parse(r.ReadToEnd());
                tables = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => new RuleTable(p.Value));
            }
            return tables[code];
        }

        RuleTable(JsonElement e) {
            Name = e.GetProperty("name").GetString();
            foreach (var p in e.GetProperty("pre").EnumerateArray()) {
                pre.Add((new Regex(p[0].GetString(), RegexOptions.CultureInvariant), p[1].GetString()));
            }
            foreach (var p in e.GetProperty("map").EnumerateObject()) {
                map[p.Name] = p.Value.GetString().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            }
            maxLen = map.Keys.Max(k => k.Length);
            Vowels = e.GetProperty("vowels").EnumerateArray().Select(x => x.GetString()).ToArray();
            Consonants = e.GetProperty("consonants").EnumerateArray().Select(x => x.GetString()).ToArray();
        }

        public string[] Convert(string word) {
            var w = word.ToLowerInvariant().Normalize(NormalizationForm.FormC);
            foreach (var (re, rep) in pre) w = re.Replace(w, rep);
            var output = new List<string>();
            int i = 0;
            while (i < w.Length) {
                int n = System.Math.Min(maxLen, w.Length - i);
                for (; n > 0; n--) {
                    if (map.TryGetValue(w.Substring(i, n), out var syms)) { output.AddRange(syms); break; }
                }
                i += n > 0 ? n : 1;
            }
            return output.ToArray();
        }
    }
}
