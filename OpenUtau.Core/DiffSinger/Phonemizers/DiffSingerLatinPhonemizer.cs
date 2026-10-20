#nullable disable
// Namespace kept so projects saved with the standalone DiffSingerLatin plugin still resolve.
using OpenUtau.Api;
using OpenUtau.Core.DiffSinger;

namespace DiffSingerLatin {
    [Phonemizer("DiffSinger Latin Phonemizer", "DIFFS LA", language: "LA")]
    public class DiffSingerLatinPhonemizer : DiffSingerG2pPhonemizer {
        protected override string GetDictionaryName() => "dsdict-la.yaml";
        public override string GetLangCode() => "la";
        protected override IG2p LoadBaseG2p() => new LatinG2p();
        protected override string[] GetBaseG2pVowels() => LatinG2p.VowelSymbols;
        protected override string[] GetBaseG2pConsonants() => LatinG2p.ConsonantSymbols;
    }
}
