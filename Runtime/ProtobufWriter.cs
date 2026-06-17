using System;
using System.Buffers;
using System.Text;

namespace Framedash
{
    /// <summary>
    /// Zero-dependency Protobuf wire format writer (serialize-only).
    /// Implements the subset of wire types needed for TelemetryBatch encoding.
    /// All multi-byte values are written in little-endian per the Protobuf spec.
    /// Uses pooled buffers; callers should dispose instances to return them.
    /// After disposal, writer methods are fail-safe no-ops and ToArray returns empty.
    /// </summary>
    public sealed class ProtobufWriter : IDisposable
    {
        // Wire type constants
        private const int WireVarint = 0;
        private const int Wire64Bit = 1;
        private const int WireLengthDelimited = 2;
        private const int Wire32Bit = 5;

        private const int DefaultInitialCapacity = 256;
        private const int DefaultUtf8BufferCapacity = 256;

        private byte[] _buffer;
        private byte[] _utf8Buffer;
        private int _length;
        private bool _disposed;

        public ProtobufWriter(int initialCapacity = 256)
        {
            if (initialCapacity <= 0)
            {
                initialCapacity = DefaultInitialCapacity;
            }

            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
            _utf8Buffer = ArrayPool<byte>.Shared.Rent(DefaultUtf8BufferCapacity);
        }

        /// <summary>Current byte length of the written data.</summary>
        public int Length => _length;

        /// <summary>Reset the writer for reuse.</summary>
        public void Reset()
        {
            if (_disposed) return;
            _length = 0;
        }

        /// <summary>Return the written bytes as a new array.</summary>
        public byte[] ToArray()
        {
            if (_disposed) return Array.Empty<byte>();
            if (_length == 0) return Array.Empty<byte>();

            var result = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        /// <summary>Return pooled buffers held by this writer.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(_utf8Buffer, clearArray: true);
            _buffer = null;
            _utf8Buffer = null;
            _length = 0;
            _disposed = true;
        }

        // ───── Low-level primitives ─────

        /// <summary>Write a field tag (field number + wire type).</summary>
        public void WriteTag(int fieldNumber, int wireType)
        {
            WriteVarint((ulong)((fieldNumber << 3) | wireType));
        }

        /// <summary>Write an unsigned varint (base-128).</summary>
        public void WriteVarint(ulong value)
        {
            if (_disposed) return;
            EnsureCapacity(10);
            while (value > 0x7F)
            {
                _buffer[_length++] = (byte)(value | 0x80);
                value >>= 7;
            }
            _buffer[_length++] = (byte)value;
        }

        /// <summary>Write 4 bytes in little-endian order.</summary>
        public void WriteFixed32(uint value)
        {
            if (_disposed) return;
            EnsureCapacity(4);
            _buffer[_length++] = (byte)value;
            _buffer[_length++] = (byte)(value >> 8);
            _buffer[_length++] = (byte)(value >> 16);
            _buffer[_length++] = (byte)(value >> 24);
        }

        /// <summary>Write 8 bytes in little-endian order.</summary>
        public void WriteFixed64(ulong value)
        {
            if (_disposed) return;
            EnsureCapacity(8);
            _buffer[_length++] = (byte)value;
            _buffer[_length++] = (byte)(value >> 8);
            _buffer[_length++] = (byte)(value >> 16);
            _buffer[_length++] = (byte)(value >> 24);
            _buffer[_length++] = (byte)(value >> 32);
            _buffer[_length++] = (byte)(value >> 40);
            _buffer[_length++] = (byte)(value >> 48);
            _buffer[_length++] = (byte)(value >> 56);
        }

        /// <summary>Write raw bytes. Invalid ranges are ignored to keep SDK calls fail-safe.</summary>
        public void WriteRawBytes(byte[] data, int offset, int count)
        {
            if (_disposed || data == null) return;
            if ((uint)offset > (uint)data.Length)
            {
                return;
            }
            if ((uint)count > (uint)(data.Length - offset))
            {
                return;
            }

            WriteRawBytes(new ReadOnlySpan<byte>(data, offset, count));
        }

        /// <summary>Write raw bytes from a non-allocating span.</summary>
        public void WriteRawBytes(ReadOnlySpan<byte> data)
        {
            if (_disposed) return;
            EnsureCapacity(data.Length);
            data.CopyTo(_buffer.AsSpan(_length, data.Length));
            _length += data.Length;
        }

        // ───── Typed field writers (proto3 default-skipping) ─────

        /// <summary>Write a string field. Skipped if null or empty (proto3 default).</summary>
        public void WriteString(int fieldNumber, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (_disposed) return;
            int byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > _utf8Buffer.Length)
            {
                var nextBuffer = ArrayPool<byte>.Shared.Rent(byteCount);
                ArrayPool<byte>.Shared.Return(_utf8Buffer, clearArray: true);
                _utf8Buffer = nextBuffer;
            }
            Encoding.UTF8.GetBytes(value.AsSpan(), _utf8Buffer.AsSpan(0, byteCount));
            WriteTag(fieldNumber, WireLengthDelimited);
            WriteVarint((ulong)byteCount);
            WriteRawBytes(_utf8Buffer.AsSpan(0, byteCount));
        }

        /// <summary>Write an int64 field as varint. Skipped if zero (proto3 default).</summary>
        public void WriteInt64(int fieldNumber, long value)
        {
            if (value == 0L) return;
            WriteTag(fieldNumber, WireVarint);
            WriteVarint((ulong)value);
        }

        /// <summary>Write a float field (32-bit fixed). Skipped if zero (proto3 default).</summary>
        public void WriteFloat(int fieldNumber, float value)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (value == 0f) return;
            WriteTag(fieldNumber, Wire32Bit);
            WriteFixed32(FloatToUInt32(value));
        }

        /// <summary>
        /// Write a float field unconditionally, even when the value is 0.
        /// For proto3 `optional` (presence-tracked) fields, where 0 is a real value.
        /// </summary>
        public void WriteFloatPresent(int fieldNumber, float value)
        {
            if (_disposed) return;
            WriteTag(fieldNumber, Wire32Bit);
            WriteFixed32(FloatToUInt32(value));
        }

        /// <summary>Write a double field (64-bit fixed). Skipped if zero (proto3 default).</summary>
        public void WriteDouble(int fieldNumber, double value)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (value == 0.0) return;
            WriteTag(fieldNumber, Wire64Bit);
            WriteFixed64(DoubleToUInt64(value));
        }

        /// <summary>Write an enum field as varint. Skipped if zero (proto3 default).</summary>
        public void WriteEnum(int fieldNumber, int value)
        {
            if (value == 0) return;
            WriteTag(fieldNumber, WireVarint);
            WriteVarint((ulong)value);
        }

        /// <summary>Write an embedded message field. Skipped if sub-writer is empty.</summary>
        public void WriteSubMessage(int fieldNumber, ProtobufWriter sub)
        {
            if (_disposed || sub == null || sub._disposed) return;
            if (sub.Length == 0) return;
            WriteTag(fieldNumber, WireLengthDelimited);
            WriteVarint((ulong)sub.Length);
            WriteRawBytes(sub._buffer.AsSpan(0, sub.Length));
        }

        private void EnsureCapacity(int additionalBytes)
        {
            int required = _length + additionalBytes;
            if (required <= _buffer.Length) return;

            int nextCapacity = _buffer.Length * 2;
            if (nextCapacity < required)
            {
                nextCapacity = required;
            }

            var nextBuffer = ArrayPool<byte>.Shared.Rent(nextCapacity);
            Buffer.BlockCopy(_buffer, 0, nextBuffer, 0, _length);
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = nextBuffer;
        }

        // ───── IEEE 754 bit conversion ─────

        private static uint FloatToUInt32(float value)
        {
            return (uint)BitConverter.SingleToInt32Bits(value);
        }

        private static ulong DoubleToUInt64(double value)
        {
            return (ulong)BitConverter.DoubleToInt64Bits(value);
        }
    }
}
