using System.Linq;
using DiffSingerLatin;
using Xunit;

namespace OpenUtau.Core.G2p {
    public class G2pTest {
        readonly ITestOutputHelper output;

        public G2pTest(ITestOutputHelper output) {
            this.output = output;
        }

        [Fact]
        public void ArpabetG2pTest() {
            var g2p = new ArpabetG2p();
            Assert.Null(g2p.Query(""));
            Assert.Null(g2p.Query(","));
            Assert.Null(g2p.Query("-"));
            Assert.Equal("v ow k ah l", string.Join(' ', g2p.Query("vocal")));
            Assert.Equal("v ow k ah l", string.Join(' ', g2p.Query("%v,oca l")));
            Assert.Equal("v uw ow", string.Join(' ', g2p.Query("voooo")));
            Assert.Equal("m iy", string.Join(' ', g2p.Query("meeee")));
        }

        [Fact]
        public void RussianG2pTest() {
            var g2p = new RussianG2p();
            Assert.Null(g2p.Query(""));
            Assert.Null(g2p.Query(","));
            Assert.Null(g2p.Query("-"));
            Assert.Equal("nn i vv je zh d ay", string.Join(' ', g2p.Query("невежда")));
            Assert.Equal("nn i vv je zh d ay", string.Join(' ', g2p.Query("?нев,.еж да ")));
            Assert.Equal("nn i nn i nn i nn i", string.Join(' ', g2p.Query("нененене")));
            Assert.Equal("d ay d ay d a d aa", string.Join(' ', g2p.Query("дададада")));
        }

        [Fact]
        public void PortugueseG2pTest() {
            var g2p = new PortugueseG2p();
            Assert.Null(g2p.Query(""));
            Assert.Null(g2p.Query(","));
            Assert.Null(g2p.Query("-"));
            Assert.Equal("i~ v e R n e s", string.Join(' ', g2p.Query("inverness")));
            Assert.Equal("i~ v e R n e s", string.Join(' ', g2p.Query("inve  rn,.ess")));
            Assert.Equal("p i r i m i tS i", string.Join(' ', g2p.Query("pírímítí")));
        }

        [Fact]
        public void ArpabetPlusG2pTest() {
            var g2p = new ArpabetPlusG2p();
            Assert.Null(g2p.Query(""));
            Assert.Null(g2p.Query(","));
            Assert.Null(g2p.Query("-"));
            Assert.Equal("f l aa p tr aa p ih k ax", string.Join(' ', g2p.Query("floptropica")));
            Assert.Equal("f l aa p tr aa p ih k ax", string.Join(' ', g2p.Query("%fl,optro pica")));
            Assert.Equal("s l ey d", string.Join(' ', g2p.Query("slayed")));
            Assert.Equal("d er eh k sh ax n", string.Join(' ', g2p.Query("direction")));
        }

        [Fact]
        public void LatinG2pTest() {
            var g2p = new LatinG2p();
            Assert.Null(g2p.Query(""));
            Assert.Equal("e k sh e l s i s", string.Join(' ', g2p.Query("excelsis")));
            Assert.Equal("g r a ts i a", string.Join(' ', g2p.Query("gratia")));
            Assert.Equal("a ny u s", string.Join(' ', g2p.Query("Agnus")));
            Assert.Equal("j e s u s", string.Join(' ', g2p.Query("Iesus")));
            Assert.Equal("e g z a u_ d i", string.Join(' ', g2p.Query("exaudi")));
            Assert.Equal("a l e l u j a", string.Join(' ', g2p.Query("alleluia")));
            Assert.Equal("a e r i a", string.Join(' ', g2p.Query("aëria")));
        }

        // Expected outputs come from the standalone Chiaki Extra Languages plugin.
        [Theory]
        [InlineData("id", "selamat", "s ax l a m a t")]
        [InlineData("ms", "cinta", "ch i n t a")]
        [InlineData("sw", "jambo", "dj a m b o")]
        [InlineData("haw", "aloha", "a l o h a")]
        [InlineData("mi", "kia ora", "k i a o r a")]
        [InlineData("eo", "ĉiuj", "ch i u j")]
        [InlineData("ca", "cançó", "k a n s o")]
        [InlineData("gl", "xente", "sh e n t e")]
        [InlineData("be", "песня", "pj e s nj a")]
        [InlineData("bg", "щастие", "sh t a s t i e")]
        [InlineData("mk", "љубов", "lj u b o v")]
        [InlineData("hbs", "ljubav", "lj u b a v")]
        [InlineData("lo", "ສະບາຍ", "s a B a Y")]
        public void ChiakiExtraG2pTest(string code, string word, string expected) {
            var g2p = new DiffSingerChiakiExtra.RuleG2p(code);
            Assert.Null(g2p.Query(""));
            Assert.Equal(expected, string.Join(' ', g2p.Query(word)));
        }

        [Fact]
        public void ChiakiExtraPhonemizersBuiltInTest() {
            var types = typeof(DiffSingerChiakiExtra.ExtraPhonemizer).Assembly.GetExportedTypes()
                .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(DiffSingerChiakiExtra.ExtraPhonemizer)))
                .ToArray();
            Assert.Equal(13, types.Length);
            Assert.All(types, t => Assert.NotNull(OpenUtau.Api.PhonemizerFactory.Get(t)));
        }
    }
}
