#nullable enable
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using OpenUtau.Core;
using OpenUtau.Core.DiffSinger;
using OpenUtau.Core.Util;
using Xunit;
using static OpenUtau.Cli.TinyOnnx;

namespace OpenUtau.Cli {
    // Models that take their diffusion noise as an input get it from DiffSingerNoise, seeded per phrase.
    // A host can pin that seed; the app, which sets nothing, must keep getting the phrase's own.
    [Collection(RenderSingletonCollection.Name)]
    public class HeadlessNoiseSeedTest {
        static float[] FilledNoise(uint phraseSeed) {
            var identity = Join(Text(1, "noise"), Text(2, "y"), Text(4, "Identity"));
            var model = Model(Message(GraphNode, identity), Text(GraphName, "g"),
                FloatTensor(GraphInput, "noise", 1, 1, 2, 3), FloatTensor(GraphOutput, "y", 1, 1, 2, 3));
            using var session = new InferenceSession(model);
            var inputs = new List<NamedOnnxValue>();
            DiffSingerOnnxExtras.FillMissingInputs(session, inputs, new DiffSingerOnnxFillContext {
                TotalFrames = 3,
                NoiseSeed = phraseSeed,
            });
            return inputs.Single(i => i.Name == "noise").AsTensor<float>().ToArray();
        }

        static float[] Noise(uint seed) => DiffSingerNoise.Create(DiffSingerNoise.StageAcoustic, seed, 1, 2, 3).ToArray();

        [Fact]
        public void TheHostSeedReplacesThePhraseSeedOnlyWhenSet() {
            var previous = Headless.NoiseSeed;
            try {
                Headless.NoiseSeed = null;
                Assert.Equal(Noise(111), FilledNoise(111));
                Headless.NoiseSeed = 5;
                Assert.Equal(Noise(5), FilledNoise(111));
                Assert.NotEqual(Noise(111), FilledNoise(111));
            } finally {
                Headless.NoiseSeed = previous;
            }
        }
    }
}
