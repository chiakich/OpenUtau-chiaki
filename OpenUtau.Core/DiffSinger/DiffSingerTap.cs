using System;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntime;
using OpenUtau.Core.Render;

namespace OpenUtau.Core.DiffSinger {
    public sealed class DiffSingerTapEvent {
        public string Stage = string.Empty;
        // Null for the phonemizer stages, which run before phrases exist.
        public RenderPhrase? Phrase;
        // The phonemizer, and the tick of the sentence's first note, for the phonemizer stages.
        public object? Source;
        public int Tick;
        public IEnumerable<NamedOnnxValue> Inputs = Array.Empty<NamedOnnxValue>();
        public IEnumerable<NamedOnnxValue>? Outputs;
        // The tensor cache entry of this run, when the cache is on; it exists before a run that hits it.
        public string? CacheFile;
        // The outputs came from a cache for certain; the model did not run.
        public bool Cached;
    }

    // Lets a host see what each DiffSinger model is fed. Unset in the app.
    public static class DiffSingerTap {
        public const string DurLinguistic = "dur_linguistic";
        public const string Dur = "dur";
        public const string PitchLinguistic = "pitch_linguistic";
        public const string Pitch = "pitch";
        public const string VarianceLinguistic = "variance_linguistic";
        public const string Variance = "variance";
        public const string Acoustic = "acoustic";
        public const string Vocoder = "vocoder";

        public static Action<DiffSingerTapEvent>? Handler;

        internal static void Report(string stage, RenderPhrase? phrase,
                IEnumerable<NamedOnnxValue> inputs, IEnumerable<NamedOnnxValue>? outputs,
                DiffSingerCache? cache = null, bool cached = false, object? source = null, int tick = 0) {
            Handler?.Invoke(new DiffSingerTapEvent {
                Stage = stage,
                Phrase = phrase,
                Source = source,
                Tick = tick,
                Inputs = inputs,
                Outputs = outputs,
                CacheFile = cache?.Filename,
                Cached = cached,
            });
        }
    }
}
