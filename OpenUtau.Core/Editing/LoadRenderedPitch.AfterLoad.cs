using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Editing {
    // Fork-only: fill in pitch for parts an opened or imported file brings in.
    public partial class LoadRenderedPitch {
        internal void RunAfterLoad(
            UProject project, UVoicePart part, DocManager docManager, CancellationToken cancellationToken) {
            var notes = UntunedNotes(part, part.renderPhrases);
            Serilog.Log.Information("Live pitch: loading rendered pitch for {Notes} of {Total} notes in {Part} ({Phrases} phrases).",
                notes.Count, part.notes.Count, part.name, part.renderPhrases.Count);
            // An empty selection would mean the whole part.
            if (notes.Count == 0) {
                return;
            }
            RunInternal(
                project, part, notes, docManager,
                (_, _) => { }, cancellationToken,
                showUnsupportedError: false);
        }

        // Phrases with PITD or PITO were hand tuned or loaded before, so they are left alone.
        internal static List<UNote> UntunedNotes(UVoicePart part, IEnumerable<RenderPhrase> phrases) {
            var pitd = part.curves.FirstOrDefault(c => c.abbr == Format.Ustx.PITD);
            var pito = part.maskedCurves.FirstOrDefault(c => c.abbr == Format.Ustx.PITO);
            var result = new List<UNote>();
            foreach (var phrase in phrases) {
                int from = phrase.position - phrase.leading - part.position;
                int to = phrase.end - part.position;
                bool tuned = (pitd != null && !pitd.IsEmptyBetween(from, to, 0))
                    || (pito != null && pito.runs.Any(r => r.ys.Length > 0 && r.x <= to && r.End >= from));
                if (tuned) {
                    continue;
                }
                // Phrase notes include touching neighbours; selecting those would retake the next phrase too.
                int start = phrase.position - part.position;
                var positions = phrase.notes.Select(n => phrase.position + n.position - part.position)
                    .Where(x => x >= start && x < to)
                    .ToHashSet();
                result.AddRange(part.notes.Where(n => positions.Contains(n.position)));
            }
            return result;
        }
    }
}
