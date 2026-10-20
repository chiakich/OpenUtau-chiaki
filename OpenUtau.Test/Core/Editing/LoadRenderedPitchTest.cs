using System.Linq;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Pipeline;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Test.Core.Editing {
    public class LoadRenderedPitchTest {
        [Fact]
        public void UntunedNotesSkipsPhrasesWithPitchDeviation() {
            // The fixture's PITD sits under the first phrase (notes 0 and 1) only.
            var (project, track, part) = PhraseSourceHashTest.BuildFixture();
            var phrases = RenderPhrase.FromPart(project, track, part);
            var notes = LoadRenderedPitch.UntunedNotes(part, phrases);
            Assert.Equal(new[] { 1200, 1680 }, notes.Select(n => n.position).OrderBy(x => x));
        }

        [Fact]
        public void UntunedNotesTakesEveryPhraseWithoutPitchData() {
            var (project, track, part) = PhraseSourceHashTest.BuildFixture();
            part.curves.RemoveAll(c => c.abbr == "pitd");
            var phrases = RenderPhrase.FromPart(project, track, part);
            var notes = LoadRenderedPitch.UntunedNotes(part, phrases);
            Assert.Equal(new[] { 0, 480, 1200, 1680 }, notes.Select(n => n.position).OrderBy(x => x));
        }

        [Fact]
        public void UntunedNotesSkipsPhrasesWithPitchOverride() {
            var (project, track, part) = PhraseSourceHashTest.BuildFixture();
            var pito = new UMaskedCurve("pito");
            pito.SetValues(new[] { (1300, 6400f), (1305, 6410f) });
            part.maskedCurves.Add(pito);
            var phrases = RenderPhrase.FromPart(project, track, part);
            Assert.Empty(LoadRenderedPitch.UntunedNotes(part, phrases));
        }
    }
}
