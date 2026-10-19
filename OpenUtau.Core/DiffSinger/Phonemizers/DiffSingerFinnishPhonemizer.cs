#nullable disable
// Namespace kept so projects saved with the standalone DiffSingerFinnish plugin still resolve.
using OpenUtau.Api;
using OpenUtau.Core.DiffSinger;

namespace DiffSingerFinnish {
    [Phonemizer("DiffSinger Finnish Phonemizer", "DIFFS FI", language: "FI")]
    public class DiffSingerFinnishPhonemizer : DiffSingerG2pPhonemizer {
        protected override string GetDictionaryName() => "dsdict-fi.yaml";
        public override string GetLangCode() => "fi";
        protected override IG2p LoadBaseG2p() => new FinnishG2p();
        protected override string[] GetBaseG2pVowels() => FinnishG2p.VowelSymbols;
        protected override string[] GetBaseG2pConsonants() => FinnishG2p.ConsonantSymbols;
    }
}
