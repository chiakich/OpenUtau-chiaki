using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenUtau.Cli {
    // Gives every random node of an ONNX model a "seed" attribute, on a copy of the model in memory.
    // DiffSinger models draw their diffusion noise from RandomNormalLike nodes exported without a seed,
    // which ONNX Runtime then seeds from the clock, and its C# API cannot set that seed.
    // Works on the protobuf wire format directly: no ONNX protobuf classes are available here.
    static class OnnxSeedPatcher {
        const int ModelGraph = 7;
        const int ModelFunctions = 25;
        const int GraphNode = 1;
        const int FunctionNode = 7;
        const int NodeOpType = 4;
        const int NodeAttribute = 5;
        const int NodeDomain = 7;
        const int AttributeName = 1;
        const int AttributeGraph = 6;
        const int AttributeGraphs = 11;

        const int WireVarint = 0;
        const int WireFixed64 = 1;
        const int WireLengthDelimited = 2;
        const int WireFixed32 = 5;

        static readonly HashSet<string> randomOps = new HashSet<string> {
            "RandomNormal", "RandomNormalLike", "RandomUniform", "RandomUniformLike", "Multinomial", "Bernoulli",
        };

        static readonly byte[] Delete = new byte[0];

        delegate bool SegmentRewriter(int field, ReadOnlyMemory<byte> payload, List<ReadOnlyMemory<byte>> replacement);
        delegate byte[]? FieldRewriter(int field, ReadOnlySpan<byte> payload);

        sealed class Counter {
            public readonly uint Seed;
            public int Count;
            public Counter(uint seed) {
                Seed = seed;
            }
        }

        // The seed of the n-th random node of a model, in graph order. ONNX Runtime reads the attribute
        // as a float and casts it to uint32, so it stays within the integers a float holds exactly, and
        // above zero.
        public static float NodeSeed(uint seed, int ordinal) {
            return 1 + (uint)(((ulong)seed + 7919UL * (ulong)ordinal) % 16777215UL);
        }

        // Returns the model itself when it has no random node.
        public static byte[] Patch(byte[] model, uint seed, out int patched) {
            var counter = new Counter(seed);
            var output = new List<ReadOnlyMemory<byte>>();
            bool changed = RewriteSegments(model, (field, payload, replacement) => {
                if (field == ModelGraph) {
                    return RewriteSegments(payload, (f, p, r) => AddIfChanged(f == GraphNode ? PatchNode(p.Span, counter) : null, r), replacement);
                }
                if (field == ModelFunctions) {
                    return AddIfChanged(PatchNodes(payload.Span, FunctionNode, counter), replacement);
                }
                return false;
            }, output);
            patched = counter.Count;
            if (!changed) {
                return model;
            }
            long total = 0;
            foreach (var segment in output) {
                total += segment.Length;
            }
            var result = new byte[total];
            int offset = 0;
            foreach (var segment in output) {
                segment.Span.CopyTo(result.AsSpan(offset));
                offset += segment.Length;
            }
            return result;
        }

        static bool AddIfChanged(byte[]? replacement, List<ReadOnlyMemory<byte>> output) {
            if (replacement == null) {
                return false;
            }
            output.Add(replacement);
            return true;
        }

        // Keeps the untouched stretches of a large message as slices of the original, so a 200 MB model
        // is copied once rather than once per nesting level.
        static bool RewriteSegments(ReadOnlyMemory<byte> message, SegmentRewriter rewrite, List<ReadOnlyMemory<byte>> output) {
            bool changed = false;
            int pos = 0;
            int runStart = 0;
            var replacement = new List<ReadOnlyMemory<byte>>();
            while (pos < message.Length) {
                int fieldStart = pos;
                ulong key = ReadVarint(message.Span, ref pos);
                int wire = (int)(key & 7);
                if (wire != WireLengthDelimited) {
                    Skip(message.Span, ref pos, wire);
                    continue;
                }
                int length = ReadLength(message.Span, ref pos);
                var payload = message.Slice(pos, length);
                pos += length;
                replacement.Clear();
                if (!rewrite((int)(key >> 3), payload, replacement)) {
                    continue;
                }
                long total = 0;
                foreach (var segment in replacement) {
                    total += segment.Length;
                }
                var header = new MemoryStream();
                WriteVarint(header, key);
                WriteVarint(header, (ulong)total);
                output.Add(message.Slice(runStart, fieldStart - runStart));
                output.Add(header.ToArray());
                output.AddRange(replacement);
                runStart = pos;
                changed = true;
            }
            if (changed) {
                output.Add(message.Slice(runStart));
            }
            return changed;
        }

        // Null when nothing changed. A rewriter returns Delete to drop a field.
        static byte[]? Rewrite(ReadOnlySpan<byte> message, FieldRewriter rewrite) {
            MemoryStream? output = null;
            int pos = 0;
            int runStart = 0;
            while (pos < message.Length) {
                int fieldStart = pos;
                ulong key = ReadVarint(message, ref pos);
                int wire = (int)(key & 7);
                if (wire != WireLengthDelimited) {
                    Skip(message, ref pos, wire);
                    continue;
                }
                int length = ReadLength(message, ref pos);
                var payload = message.Slice(pos, length);
                pos += length;
                byte[]? replacement = rewrite((int)(key >> 3), payload);
                if (replacement == null) {
                    continue;
                }
                output ??= new MemoryStream(message.Length + 32);
                output.Write(message.Slice(runStart, fieldStart - runStart));
                if (!ReferenceEquals(replacement, Delete)) {
                    WriteVarint(output, key);
                    WriteVarint(output, (ulong)replacement.Length);
                    output.Write(replacement);
                }
                runStart = pos;
            }
            if (output == null) {
                return null;
            }
            output.Write(message.Slice(runStart));
            return output.ToArray();
        }

        static byte[]? PatchNodes(ReadOnlySpan<byte> message, int nodeField, Counter counter) {
            return Rewrite(message, (field, payload) => field == nodeField ? PatchNode(payload, counter) : null);
        }

        static byte[]? PatchNode(ReadOnlySpan<byte> node, Counter counter) {
            string opType = string.Empty;
            string domain = string.Empty;
            int pos = 0;
            while (pos < node.Length) {
                ulong key = ReadVarint(node, ref pos);
                int wire = (int)(key & 7);
                if (wire != WireLengthDelimited) {
                    Skip(node, ref pos, wire);
                    continue;
                }
                int length = ReadLength(node, ref pos);
                var payload = node.Slice(pos, length);
                pos += length;
                switch ((int)(key >> 3)) {
                    case NodeOpType:
                        opType = Encoding.UTF8.GetString(payload);
                        break;
                    case NodeDomain:
                        domain = Encoding.UTF8.GetString(payload);
                        break;
                }
            }
            bool random = (domain.Length == 0 || domain == "ai.onnx") && randomOps.Contains(opType);
            if (!random) {
                // Control flow nodes carry their bodies as graph attributes.
                return Rewrite(node, (field, payload) => field == NodeAttribute ? PatchAttribute(payload, counter) : null);
            }
            var withoutSeed = Rewrite(node, (field, payload) =>
                field == NodeAttribute && AttributeNameOf(payload) == "seed" ? Delete : null);
            var output = new MemoryStream(node.Length + 32);
            if (withoutSeed != null) {
                output.Write(withoutSeed);
            } else {
                output.Write(node);
            }
            var attribute = SeedAttribute(NodeSeed(counter.Seed, counter.Count++));
            WriteVarint(output, (NodeAttribute << 3) | WireLengthDelimited);
            WriteVarint(output, (ulong)attribute.Length);
            output.Write(attribute);
            return output.ToArray();
        }

        static byte[]? PatchAttribute(ReadOnlySpan<byte> attribute, Counter counter) {
            return Rewrite(attribute, (field, payload) =>
                field == AttributeGraph || field == AttributeGraphs ? PatchNodes(payload, GraphNode, counter) : null);
        }

        static string AttributeNameOf(ReadOnlySpan<byte> attribute) {
            int pos = 0;
            while (pos < attribute.Length) {
                ulong key = ReadVarint(attribute, ref pos);
                int wire = (int)(key & 7);
                if (wire != WireLengthDelimited) {
                    Skip(attribute, ref pos, wire);
                    continue;
                }
                int length = ReadLength(attribute, ref pos);
                if ((int)(key >> 3) == AttributeName) {
                    return Encoding.UTF8.GetString(attribute.Slice(pos, length));
                }
                pos += length;
            }
            return string.Empty;
        }

        // AttributeProto { name: "seed", f: value, type: FLOAT }
        static byte[] SeedAttribute(float value) {
            var bytes = new byte[14];
            bytes[0] = 0x0A;
            bytes[1] = 4;
            bytes[2] = (byte)'s';
            bytes[3] = (byte)'e';
            bytes[4] = (byte)'e';
            bytes[5] = (byte)'d';
            bytes[6] = 0x15;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(7), value);
            bytes[11] = 0xA0;
            bytes[12] = 0x01;
            bytes[13] = 1;
            return bytes;
        }

        static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos) {
            ulong result = 0;
            int shift = 0;
            while (true) {
                if (pos >= data.Length || shift > 63) {
                    throw new InvalidDataException("Malformed ONNX model.");
                }
                byte b = data[pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) {
                    return result;
                }
                shift += 7;
            }
        }

        // The length of a length-delimited field, checked against what is left of the message.
        static int ReadLength(ReadOnlySpan<byte> data, ref int pos) {
            ulong length = ReadVarint(data, ref pos);
            if (length > (ulong)(data.Length - pos)) {
                throw new InvalidDataException("Malformed ONNX model.");
            }
            return (int)length;
        }

        static void Skip(ReadOnlySpan<byte> data, ref int pos, int wire) {
            switch (wire) {
                case WireVarint:
                    ReadVarint(data, ref pos);
                    break;
                case WireFixed64:
                    pos += 8;
                    break;
                case WireFixed32:
                    pos += 4;
                    break;
                default:
                    throw new InvalidDataException("Malformed ONNX model.");
            }
            if (pos > data.Length) {
                throw new InvalidDataException("Malformed ONNX model.");
            }
        }

        static void WriteVarint(Stream output, ulong value) {
            while (value >= 0x80) {
                output.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            output.WriteByte((byte)value);
        }
    }
}
