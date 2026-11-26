using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OpenUtau.Cli {
    sealed class NpyArray {
        public string Name = string.Empty;
        public string Dtype = string.Empty;
        public string Descr = string.Empty;
        public int[] Shape = Array.Empty<int>();
        public byte[] Data = Array.Empty<byte>();

        // Copies the tensor: model outputs live in native memory that is freed after the run.
        public static NpyArray? From(string name, NamedOnnxValue value) {
            if (!(value.Value is TensorBase tensor)) {
                return null;
            }
            switch (tensor.GetTypeInfo().ElementType) {
                case TensorElementType.Float:
                    return Of(name, "float32", "<f4", value.AsTensor<float>());
                case TensorElementType.Double:
                    return Of(name, "float64", "<f8", value.AsTensor<double>());
                case TensorElementType.Int64:
                    return Of(name, "int64", "<i8", value.AsTensor<long>());
                case TensorElementType.Int32:
                    return Of(name, "int32", "<i4", value.AsTensor<int>());
                case TensorElementType.Bool:
                    return Of(name, "bool", "|b1", value.AsTensor<bool>());
                default:
                    return null;
            }
        }

        static NpyArray Of<T>(string name, string dtype, string descr, Tensor<T> tensor) where T : unmanaged {
            // ToArray is row-major, which is numpy's C order.
            T[] values = tensor.ToArray();
            return new NpyArray {
                Name = name,
                Dtype = dtype,
                Descr = descr,
                Shape = tensor.Dimensions.ToArray(),
                Data = MemoryMarshal.AsBytes(values.AsSpan()).ToArray(),
            };
        }
    }

    // Writes numpy's .npz: a zip of .npy files, readable with numpy.load.
    static class NpzWriter {
        // Zip stores a timestamp per entry; a fixed one keeps identical data byte-identical on disk.
        static readonly DateTimeOffset Timestamp = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public static void Write(string path, IEnumerable<NpyArray> arrays) {
            using var file = File.Create(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var array in arrays) {
                var entry = zip.CreateEntry(array.Name + ".npy", CompressionLevel.Fastest);
                entry.LastWriteTime = Timestamp;
                using var stream = entry.Open();
                WriteNpy(stream, array);
            }
        }

        static void WriteNpy(Stream stream, NpyArray array) {
            string shape = array.Shape.Length == 1
                ? $"({array.Shape[0]},)"
                : $"({string.Join(", ", array.Shape)})";
            string header = $"{{'descr': '{array.Descr}', 'fortran_order': False, 'shape': {shape}, }}";
            // Format 1.0: magic, version, 16-bit header length, then the header padded so the data starts
            // on a 64-byte boundary.
            const int prefix = 10;
            int padding = 64 - (prefix + header.Length + 1) % 64;
            header += new string(' ', padding % 64) + "\n";
            stream.Write(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0 });
            stream.Write(BitConverter.GetBytes((ushort)header.Length));
            stream.Write(Encoding.ASCII.GetBytes(header));
            stream.Write(array.Data);
        }
    }
}
