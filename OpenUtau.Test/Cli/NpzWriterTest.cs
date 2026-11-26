#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace OpenUtau.Cli {
    public class NpzWriterTest {
        static NpyArray Array<T>(string name, T[] values, params int[] shape) {
            var array = NpyArray.From(name, NamedOnnxValue.CreateFromTensor(name, new DenseTensor<T>(values, shape)));
            Assert.NotNull(array);
            return array!;
        }

        static (string Header, byte[] Data) ReadNpy(ZipArchiveEntry entry) {
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            Assert.Equal(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0 }, bytes.Take(8).ToArray());
            int headerLength = BitConverter.ToUInt16(bytes, 8);
            // numpy requires the data to start on a 64-byte boundary.
            Assert.Equal(0, (10 + headerLength) % 64);
            string header = Encoding.ASCII.GetString(bytes, 10, headerLength);
            Assert.EndsWith("\n", header);
            return (header.TrimEnd(), bytes.Skip(10 + headerLength).ToArray());
        }

        [Fact]
        public void WritesArraysNumpyCanLoad() {
            string path = Path.Combine(Path.GetTempPath(), $"npz-{Guid.NewGuid():N}.npz");
            try {
                NpzWriter.Write(path, new[] {
                    Array("in.f0", new float[] { 1.5f, -2f, 440f }, 1, 3),
                    Array("in.tokens", new long[] { 7, 8 }, 2),
                    Array("in.retake", new bool[] { true, false, false, true }, 2, 2),
                });
                using var zip = ZipFile.OpenRead(path);
                Assert.Equal(new[] { "in.f0.npy", "in.tokens.npy", "in.retake.npy" }, zip.Entries.Select(e => e.FullName).ToArray());

                var (f0Header, f0) = ReadNpy(zip.Entries[0]);
                Assert.Equal("{'descr': '<f4', 'fortran_order': False, 'shape': (1, 3), }", f0Header);
                Assert.Equal(new float[] { 1.5f, -2f, 440f }, Enumerable.Range(0, 3).Select(i => BitConverter.ToSingle(f0, i * 4)).ToArray());

                var (tokensHeader, tokens) = ReadNpy(zip.Entries[1]);
                Assert.Equal("{'descr': '<i8', 'fortran_order': False, 'shape': (2,), }", tokensHeader);
                Assert.Equal(new long[] { 7, 8 }, Enumerable.Range(0, 2).Select(i => BitConverter.ToInt64(tokens, i * 8)).ToArray());

                var (retakeHeader, retake) = ReadNpy(zip.Entries[2]);
                Assert.Equal("{'descr': '|b1', 'fortran_order': False, 'shape': (2, 2), }", retakeHeader);
                Assert.Equal(new byte[] { 1, 0, 0, 1 }, retake);
            } finally {
                File.Delete(path);
            }
        }

        [Fact]
        public void SameDataGivesTheSameBytes() {
            string first = Path.Combine(Path.GetTempPath(), $"npz-{Guid.NewGuid():N}.npz");
            string second = Path.Combine(Path.GetTempPath(), $"npz-{Guid.NewGuid():N}.npz");
            try {
                var arrays = new[] { Array("out.mel", new float[] { 0.25f, 0.5f }, 1, 2) };
                NpzWriter.Write(first, arrays);
                // A zip entry carries its write time unless told otherwise.
                System.Threading.Thread.Sleep(2100);
                NpzWriter.Write(second, arrays);
                Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
            } finally {
                File.Delete(first);
                File.Delete(second);
            }
        }

        [Fact]
        public void RefusesElementTypesNumpyHasNoPlainFormOf() {
            var strings = NamedOnnxValue.CreateFromTensor("s", new DenseTensor<string>(new[] { "a" }, new[] { 1 }));
            Assert.Null(NpyArray.From("s", strings));
        }
    }
}
