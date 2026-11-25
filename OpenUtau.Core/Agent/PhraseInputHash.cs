using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using OpenUtau.Core.Render;

namespace OpenUtau.Core.Agent {
    // A fingerprint of what the renderer is given for a phrase: the phonemes with their timing, the
    // pitch and the expression curves. Unlike RenderPhrase.hash it holds no timestamp, so it is equal
    // between runs, and between the app and the CLI, exactly when that data is equal.
    public static class PhraseInputHash {
        public static string Of(RenderPhrase phrase) {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream)) {
                foreach (var phone in phrase.phones) {
                    writer.Write(phone.phoneme ?? string.Empty);
                    writer.Write(phone.suffix ?? string.Empty);
                    writer.Write(phone.position);
                    writer.Write(phone.duration);
                    writer.Write(phone.tone);
                    writer.Write(phone.positionMs);
                    writer.Write(phone.durationMs);
                }
                void Curve(string name, float[]? values) {
                    // To a renderer, a curve left at zero and a curve the project lacks are the same.
                    if (values == null || values.All(v => v == 0)) {
                        return;
                    }
                    writer.Write(name);
                    writer.Write(values.Length);
                    foreach (float value in values) {
                        writer.Write(value);
                    }
                }
                Curve("pitches", phrase.pitches);
                Curve("pitches_before_bend", phrase.pitchesBeforeBend);
                Curve("dynamics", phrase.dynamics);
                Curve("gender", phrase.gender);
                Curve("breathiness", phrase.breathiness);
                Curve("tone_shift", phrase.toneShift);
                Curve("vocoder_pitch", phrase.vocoderPitch);
                Curve("acoustic_f0_zero", phrase.acousticF0Zero);
                Curve("tension", phrase.tension);
                Curve("mouth_opening", phrase.mouthOpening);
                Curve("voicing", phrase.voicing);
                Curve("xsy", phrase.xsy);
                foreach (var curve in phrase.curves.OrderBy(c => c.Item1, StringComparer.Ordinal)) {
                    Curve("curve:" + curve.Item1, curve.Item2);
                }
            }
            return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
        }
    }
}
