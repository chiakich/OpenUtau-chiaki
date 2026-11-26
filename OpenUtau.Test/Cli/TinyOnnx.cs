#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OpenUtau.Cli {
    // Just enough of the protobuf wire format to write an ONNX model by hand; the tests have no ONNX
    // protobuf classes either.
    static class TinyOnnx {
        public const int GraphNode = 1;
        public const int GraphName = 2;
        public const int GraphInput = 11;
        public const int GraphOutput = 12;

        public static byte[] Varint(ulong value) {
            var bytes = new List<byte>();
            while (value >= 0x80) {
                bytes.Add((byte)(value | 0x80));
                value >>= 7;
            }
            bytes.Add((byte)value);
            return bytes.ToArray();
        }

        public static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        public static byte[] Message(int field, params byte[][] parts) {
            var payload = Join(parts);
            return Join(Varint((ulong)(field << 3 | 2)), Varint((ulong)payload.Length), payload);
        }

        public static byte[] Text(int field, string value) => Message(field, Encoding.UTF8.GetBytes(value));
        public static byte[] Number(int field, ulong value) => Join(Varint((ulong)(field << 3)), Varint(value));
        public static byte[] Float(int field, float value) => Join(Varint((ulong)(field << 3 | 5)), BitConverter.GetBytes(value));

        // ValueInfoProto of a float tensor, as a graph input or output.
        public static byte[] FloatTensor(int graphField, string name, params int[] shape) {
            var dims = Join(shape.Select(d => Message(1, Number(1, (ulong)d))).ToArray());
            var type = Message(1, Number(1, 1), Message(2, dims));
            return Message(graphField, Text(1, name), Message(2, type));
        }

        // ModelProto: IR version 8, opset 15, and the graph.
        public static byte[] Model(params byte[][] graphFields) {
            return Join(Number(1, 8), Message(8, Number(2, 15)), Message(7, graphFields));
        }
    }
}
