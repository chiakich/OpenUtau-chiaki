#nullable disable
// Namespace kept so projects saved with the standalone Chiaki Extra Languages plugin still resolve.
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Api;
using OpenUtau.Core.DiffSinger;

namespace DiffSingerChiakiExtra {
    public class RuleG2p : IG2p {
        readonly RuleTable table;
        readonly HashSet<string> all;
        public RuleG2p(string code) { table = RuleTable.Get(code); all = new(table.Vowels.Concat(table.Consonants)); }
        public bool IsValidSymbol(string symbol) => all.Contains(symbol);
        public bool IsVowel(string symbol) => table.Vowels.Contains(symbol);
        public bool IsGlide(string symbol) => symbol is "j" or "w" or "i_" or "u_";
        public string[] UnpackHint(string hint, char separator = ' ') => hint.Split(separator).Where(all.Contains).ToArray();
        public string[] Query(string grapheme) {
            if (string.IsNullOrWhiteSpace(grapheme)) return null;
            var output = table.Convert(grapheme);
            return output.Length > 0 ? output : null;
        }
    }

    public abstract class ExtraPhonemizer : DiffSingerG2pPhonemizer {
        protected abstract string Code { get; }
        protected override string GetDictionaryName() => $"dsdict-{Code}.yaml";
        public override string GetLangCode() => Code;
        protected override IG2p LoadBaseG2p() => new RuleG2p(Code);
        protected override string[] GetBaseG2pVowels() => RuleTable.Get(Code).Vowels;
        protected override string[] GetBaseG2pConsonants() => RuleTable.Get(Code).Consonants;
    }

    [Phonemizer("DiffSinger Indonesian Phonemizer", "DIFFS ID", language: "ID")]
    public class IndonesianPhonemizer : ExtraPhonemizer { protected override string Code => "id"; }
    [Phonemizer("DiffSinger Malay Phonemizer", "DIFFS MS", language: "MS")]
    public class MalayPhonemizer : ExtraPhonemizer { protected override string Code => "ms"; }
    [Phonemizer("DiffSinger Swahili Phonemizer", "DIFFS SW", language: "SW")]
    public class SwahiliPhonemizer : ExtraPhonemizer { protected override string Code => "sw"; }
    [Phonemizer("DiffSinger Hawaiian Phonemizer", "DIFFS HAW", language: "HAW")]
    public class HawaiianPhonemizer : ExtraPhonemizer { protected override string Code => "haw"; }
    [Phonemizer("DiffSinger Maori Phonemizer", "DIFFS MI", language: "MI")]
    public class MaoriPhonemizer : ExtraPhonemizer { protected override string Code => "mi"; }
    [Phonemizer("DiffSinger Esperanto Phonemizer", "DIFFS EO", language: "EO")]
    public class EsperantoPhonemizer : ExtraPhonemizer { protected override string Code => "eo"; }
    [Phonemizer("DiffSinger Catalan Phonemizer", "DIFFS CA", language: "CA")]
    public class CatalanPhonemizer : ExtraPhonemizer { protected override string Code => "ca"; }
    [Phonemizer("DiffSinger Galician Phonemizer", "DIFFS GL", language: "GL")]
    public class GalicianPhonemizer : ExtraPhonemizer { protected override string Code => "gl"; }
    [Phonemizer("DiffSinger Belarusian Phonemizer", "DIFFS BE", language: "BE")]
    public class BelarusianPhonemizer : ExtraPhonemizer { protected override string Code => "be"; }
    [Phonemizer("DiffSinger Bulgarian Phonemizer", "DIFFS BG", language: "BG")]
    public class BulgarianPhonemizer : ExtraPhonemizer { protected override string Code => "bg"; }
    [Phonemizer("DiffSinger Macedonian Phonemizer", "DIFFS MK", language: "MK")]
    public class MacedonianPhonemizer : ExtraPhonemizer { protected override string Code => "mk"; }
    [Phonemizer("DiffSinger Serbo-Croatian Phonemizer", "DIFFS HBS", language: "HBS")]
    public class SerboCroatianPhonemizer : ExtraPhonemizer { protected override string Code => "hbs"; }
    [Phonemizer("DiffSinger Lao Phonemizer", "DIFFS LO", language: "LO")]
    public class LaoPhonemizer : ExtraPhonemizer { protected override string Code => "lo"; }
}
