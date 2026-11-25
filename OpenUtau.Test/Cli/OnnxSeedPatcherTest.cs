#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Xunit;
using static OpenUtau.Cli.TinyOnnx;

namespace OpenUtau.Cli {
    public class OnnxSeedPatcherTest {
        static byte[] SeedAttribute(float seed) => Join(Text(1, "seed"), Float(2, seed), Number(20, 1));

        // NodeProto: a random node writing `count` floats to `output`.
        static byte[] RandomNode(string output, int count, params byte[][] extraAttributes) {
            var shape = Message(5, Text(1, "shape"), Number(8, 1), Number(8, (ulong)count), Number(20, 7));
            return Join(Text(2, output), Text(4, "RandomNormal"), shape,
                Join(extraAttributes.Select(a => Message(5, a)).ToArray()));
        }

        static byte[] RandomModel(params byte[][] extraAttributes) {
            return Model(Message(GraphNode, RandomNode("y", 8, extraAttributes)), Text(GraphName, "g"),
                FloatTensor(GraphOutput, "y", 1, 8));
        }

        static float[] Run(byte[] model) {
            using var session = new InferenceSession(model);
            using var outputs = session.Run(new List<NamedOnnxValue>());
            return outputs.First().AsTensor<float>().ToArray();
        }

        static int Count(byte[] haystack, byte[] needle) {
            int count = 0;
            for (int i = 0; i + needle.Length <= haystack.Length; i++) {
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) {
                    count++;
                }
            }
            return count;
        }

        [Fact]
        public void SameSeedGivesTheSameNumbersInEverySession() {
            var seeded = OnnxSeedPatcher.Patch(RandomModel(), 5, out int patched);
            Assert.Equal(1, patched);
            var first = Run(seeded);
            Assert.Equal(first, Run(seeded));
            Assert.NotEqual(first, Run(OnnxSeedPatcher.Patch(RandomModel(), 6, out _)));
        }

        [Fact]
        public void ReplacesASeedTheModelAlreadyHas() {
            var seeded = OnnxSeedPatcher.Patch(RandomModel(SeedAttribute(123)), 5, out int patched);
            Assert.Equal(1, patched);
            Assert.Equal(0, Count(seeded, SeedAttribute(123)));
            Assert.Equal(1, Count(seeded, SeedAttribute(OnnxSeedPatcher.NodeSeed(5, 0))));
            Assert.Equal(Run(OnnxSeedPatcher.Patch(RandomModel(), 5, out _)), Run(seeded));
        }

        [Fact]
        public void SeedsNodesInsideControlFlowBodies() {
            // An If node whose branch is a graph holding a random node, after a top-level random node.
            var body = Join(Message(1, RandomNode("z", 4)), Text(2, "body"));
            var branch = Join(Text(1, "then_branch"), Message(6, body), Number(20, 5));
            var ifNode = Join(Text(4, "If"), Message(5, branch));
            var model = Model(Message(1, RandomNode("y", 8)), Message(1, ifNode), Text(2, "g"));

            var seeded = OnnxSeedPatcher.Patch(model, 9, out int patched);

            Assert.Equal(2, patched);
            Assert.Equal(1, Count(seeded, SeedAttribute(OnnxSeedPatcher.NodeSeed(9, 0))));
            Assert.Equal(1, Count(seeded, SeedAttribute(OnnxSeedPatcher.NodeSeed(9, 1))));
            // The enclosing length prefixes were rewritten too: the result still parses, to the same thing.
            Assert.Equal(seeded, OnnxSeedPatcher.Patch(seeded, 9, out int again));
            Assert.Equal(2, again);
        }

        [Fact]
        public void LeavesAModelWithoutRandomNodesAlone() {
            var node = Join(Text(1, "x"), Text(2, "y"), Text(4, "Identity"));
            var model = Model(Message(GraphNode, node), Text(GraphName, "g"), FloatTensor(GraphOutput, "y", 1, 8));
            Assert.Same(model, OnnxSeedPatcher.Patch(model, 5, out int patched));
            Assert.Equal(0, patched);
        }

        [Fact]
        public void SeedsStayExactAsFloats() {
            foreach (uint seed in new uint[] { 0, 1, 16777214, 16777215, uint.MaxValue }) {
                for (int ordinal = 0; ordinal < 4; ordinal++) {
                    float value = OnnxSeedPatcher.NodeSeed(seed, ordinal);
                    Assert.InRange(value, 1f, 16777215f);
                    Assert.Equal(value, (float)(uint)value);
                }
            }
            Assert.NotEqual(OnnxSeedPatcher.NodeSeed(5, 0), OnnxSeedPatcher.NodeSeed(5, 1));
        }

        [Fact]
        public void RejectsTruncatedInput() {
            var model = RandomModel();
            Assert.Throws<InvalidDataException>(() => OnnxSeedPatcher.Patch(model.Take(model.Length - 3).ToArray(), 5, out _));
        }
    }
}
