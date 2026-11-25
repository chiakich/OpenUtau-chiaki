using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenUtau.Core;
using OpenUtau.Core.DiffSinger;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Agent {
    // The document as JSON: what the piano roll shows (notes, phonemes) and what the renderer gets (phrases).
    // Reads live document state, so call it on the main thread.
    static class ProjectReport {
        public static double Round(double value) => Math.Round(value, 6);

        public static JsonObject Project(UProject project) {
            return new JsonObject {
                ["path"] = project.FilePath,
                ["name"] = project.name,
                ["resolution"] = project.resolution,
                ["tempos"] = new JsonArray(project.tempos
                    .Select(t => (JsonNode)new JsonObject { ["tick"] = t.position, ["bpm"] = Round(t.bpm) })
                    .ToArray()),
                ["time_signatures"] = new JsonArray(project.timeSignatures
                    .Select(t => (JsonNode)new JsonObject {
                        ["bar"] = t.barPosition,
                        ["beat_per_bar"] = t.beatPerBar,
                        ["beat_unit"] = t.beatUnit,
                    })
                    .ToArray()),
            };
        }

        public static JsonArray Tracks(UProject project) {
            var result = new JsonArray();
            for (int i = 0; i < project.tracks.Count; i++) {
                var track = project.tracks[i];
                var singer = track.Singer;
                result.Add(new JsonObject {
                    ["index"] = i,
                    ["name"] = track.TrackName,
                    ["singer_id"] = singer?.Id,
                    ["singer_name"] = singer?.Name,
                    ["singer_path"] = singer != null && singer.Found ? singer.Location : null,
                    ["singer_type"] = singer != null && singer.Found ? singer.SingerType.ToString().ToLowerInvariant() : null,
                    ["phonemizer"] = track.Phonemizer?.Id,
                    ["phonemizer_tag"] = track.Phonemizer?.Tag,
                    ["renderer"] = track.RendererSettings?.renderer,
                    ["muted"] = track.Muted,
                });
            }
            return result;
        }

        public static JsonArray Parts(UProject project, JsonArray errors, Func<UNote, int>? noteId = null) {
            var result = new JsonArray();
            for (int i = 0; i < project.parts.Count; i++) {
                if (project.parts[i] is UVoicePart part) {
                    result.Add(Part(project, part, i, errors, noteId));
                }
            }
            return result;
        }

        static JsonObject Part(UProject project, UVoicePart part, int partIndex, JsonArray errors, Func<UNote, int>? noteId) {
            var axis = project.timeAxis;
            var notes = part.notes.ToList();
            var noteIndex = new Dictionary<UNote, int>();
            for (int i = 0; i < notes.Count; i++) {
                noteIndex[notes[i]] = i;
            }
            var phonemes = part.phonemes.ToList();
            RenderPhrase[] phrases;
            lock (part) {
                phrases = part.renderPhrases.ToArray();
            }

            // Phrases hold copies of the phonemes; the part-relative tick identifies the original.
            var phonemeAt = new Dictionary<int, int>();
            for (int i = 0; i < phonemes.Count; i++) {
                if (!phonemes[i].Error) {
                    phonemeAt[phonemes[i].position] = i;
                }
            }
            var phraseOf = new int?[phonemes.Count];
            var frameOf = new int?[phonemes.Count];
            var framesOf = new int?[phonemes.Count];
            var phrasesJson = new JsonArray();
            for (int p = 0; p < phrases.Length; p++) {
                phrasesJson.Add(Phrase(part, phrases[p], p, phonemeAt, phraseOf, frameOf, framesOf));
            }

            var notePhonemes = notes.Select(_ => new JsonArray()).ToArray();
            var phonemesJson = new JsonArray();
            for (int i = 0; i < phonemes.Count; i++) {
                var phoneme = phonemes[i];
                int? parent = phoneme.Parent != null && noteIndex.TryGetValue(phoneme.Parent, out int n) ? n : (int?)null;
                if (parent.HasValue) {
                    notePhonemes[parent.Value].Add(i);
                }
                int tick = part.position + phoneme.position;
                string? error = phoneme.Error
                    ? phoneme.ErrorException?.Message ?? "Phoneme has an error."
                    : null;
                // An errored phoneme has no validated duration.
                phonemesJson.Add(new JsonObject {
                    ["index"] = i,
                    ["phoneme"] = phoneme.phoneme,
                    ["note"] = parent,
                    ["index_in_note"] = phoneme.index,
                    ["tick"] = tick,
                    ["end_tick"] = phoneme.Error ? null : part.position + phoneme.End,
                    ["ms"] = Round(axis.TickPosToMsPos(tick)),
                    ["end_ms"] = phoneme.Error ? null : Round(phoneme.EndMs),
                    ["duration_ms"] = phoneme.Error ? null : Round(phoneme.EndMs - axis.TickPosToMsPos(tick)),
                    ["phrase"] = phraseOf[i],
                    ["frame"] = frameOf[i],
                    ["frames"] = framesOf[i],
                    ["error"] = error,
                });
                if (error != null) {
                    errors.Add(new JsonObject {
                        ["part"] = partIndex,
                        ["phoneme"] = i,
                        ["note"] = parent,
                        ["lyric"] = phoneme.Parent?.lyric,
                        ["message"] = error,
                    });
                }
            }

            var notesJson = new JsonArray();
            for (int i = 0; i < notes.Count; i++) {
                var note = notes[i];
                int tick = part.position + note.position;
                int endTick = part.position + note.End;
                var noteJson = new JsonObject {
                    ["index"] = i,
                    ["lyric"] = note.lyric,
                    ["tone"] = note.tone,
                    ["tone_name"] = MusicMath.GetToneName(note.tone),
                    ["tick"] = tick,
                    ["end_tick"] = endTick,
                    ["ms"] = Round(axis.TickPosToMsPos(tick)),
                    ["end_ms"] = Round(axis.TickPosToMsPos(endTick)),
                    ["phonemes"] = notePhonemes[i],
                };
                if (noteId != null) {
                    noteJson["id"] = noteId(note);
                }
                notesJson.Add(noteJson);
            }

            var curves = new JsonObject();
            foreach (var curve in part.curves.OrderBy(c => c.abbr, StringComparer.Ordinal)) {
                curves[curve.abbr] = curve.xs.Count;
            }
            return new JsonObject {
                ["index"] = partIndex,
                ["track"] = part.trackNo,
                ["name"] = part.name,
                ["position_tick"] = part.position,
                ["end_tick"] = part.End,
                ["curve_points"] = curves,
                ["notes"] = notesJson,
                ["phonemes"] = phonemesJson,
                ["phrases"] = phrasesJson,
            };
        }

        static JsonObject Phrase(UVoicePart part, RenderPhrase phrase, int index, Dictionary<int, int> phonemeAt,
                int?[] phraseOf, int?[] frameOf, int?[] framesOf) {
            var phoneIndexes = phrase.phones
                .Select(phone => phonemeAt.TryGetValue(phrase.position + phone.position - part.position, out int i) ? i : (int?)null)
                .ToArray();
            foreach (var i in phoneIndexes) {
                if (i.HasValue) {
                    phraseOf[i.Value] = index;
                }
            }
            var result = new JsonObject {
                ["index"] = index,
                ["tick"] = phrase.position,
                ["end_tick"] = phrase.end,
                ["ms"] = Round(phrase.positionMs),
                ["end_ms"] = Round(phrase.endMs),
                ["phonemes"] = new JsonArray(phoneIndexes.Select(i => (JsonNode?)i).ToArray()),
                ["input_hash"] = PhraseInputHash.Of(phrase),
            };
            if (!(phrase.singer is DiffSingerSinger singer)) {
                return result;
            }
            // Same frame budget as DiffSingerRenderer: head padding, phonemes with silence in gaps, tail padding.
            double frameMs = singer.dsConfig.frameMs();
            int head = DiffSingerUtils.headFrames;
            int tail = DiffSingerUtils.tailFrames;
            List<(string Phoneme, double DurationMs, int PhoneIndex)> segments;
            int[] frames;
            try {
                segments = DiffSingerUtils.PaddedSegments(phrase, frameMs, head, tail);
                frames = DiffSingerUtils.DurationsMsToFrames(segments.Select(s => s.DurationMs), frameMs);
            } catch (Exception) {
                return result;
            }
            var segmentsJson = new JsonArray();
            int start = 0;
            for (int s = 0; s < segments.Count; s++) {
                int? phoneme = segments[s].PhoneIndex >= 0 ? phoneIndexes[segments[s].PhoneIndex] : null;
                if (phoneme.HasValue) {
                    frameOf[phoneme.Value] = start;
                    framesOf[phoneme.Value] = frames[s];
                }
                segmentsJson.Add(new JsonObject {
                    ["phoneme"] = segments[s].Phoneme,
                    ["frame"] = start,
                    ["frames"] = frames[s],
                    ["phoneme_index"] = phoneme,
                });
                start += frames[s];
            }
            result["frame_ms"] = Round(frameMs);
            result["head_frames"] = head;
            result["tail_frames"] = tail;
            result["total_frames"] = start;
            result["frame0_ms"] = Round(phrase.positionMs - head * frameMs);
            result["segments"] = segmentsJson;
            return result;
        }
    }
}
