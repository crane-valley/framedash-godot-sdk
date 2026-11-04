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

        public int Length => _length;

        public void Reset()
        {
            if (_disposed) return;
            _length = 0;
        }

        public byte[] ToArray()
        {
            if (_disposed) return Array.Empty<byte>();
            if (_length == 0) return Array.Empty<byte>();

            var result = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

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


        public void WriteTag(int fieldNumber, int wireType)
        {
            // Caller-supplied invalid tags must remain fail-safe in checked consumer builds.
            WriteVarint(unchecked(((ulong)(uint)fieldNumber << 3) | (uint)wireType));
        }

        public void WriteVarint(ulong value)
        {
            if (_disposed) return;
            if (!EnsureCapacity(10)) return;
            while (value > 0x7F)
            {
                _buffer[_length++] = unchecked((byte)(value | 0x80));
                value >>= 7;
            }
            _buffer[_length++] = unchecked((byte)value);
        }

        public void WriteFixed32(uint value)
        {
            if (_disposed) return;
            if (!EnsureCapacity(4)) return;
            _buffer[_length++] = unchecked((byte)value);
            _buffer[_length++] = unchecked((byte)(value >> 8));
            _buffer[_length++] = unchecked((byte)(value >> 16));
            _buffer[_length++] = unchecked((byte)(value >> 24));
        }

        public void WriteFixed64(ulong value)
        {
            if (_disposed) return;
            if (!EnsureCapacity(8)) return;
            _buffer[_length++] = unchecked((byte)value);
            _buffer[_length++] = unchecked((byte)(value >> 8));
            _buffer[_length++] = unchecked((byte)(value >> 16));
            _buffer[_length++] = unchecked((byte)(value >> 24));
            _buffer[_length++] = unchecked((byte)(value >> 32));
            _buffer[_length++] = unchecked((byte)(value >> 40));
            _buffer[_length++] = unchecked((byte)(value >> 48));
            _buffer[_length++] = unchecked((byte)(value >> 56));
        }

        /// <summary>Write raw bytes. Invalid ranges are ignored to keep SDK calls fail-safe.</summary>
        public void WriteRawBytes(byte[] data, int offset, int count)
        {
            if (_disposed || data == null) return;
            if (offset < 0 || count < 0 || offset > data.Length - count)
            {
                return;
            }

            WriteRawBytes(new ReadOnlySpan<byte>(data, offset, count));
        }

        public void WriteRawBytes(ReadOnlySpan<byte> data)
        {
            if (_disposed) return;
            if (!EnsureCapacity(data.Length)) return;
            data.CopyTo(_buffer.AsSpan(_length, data.Length));
            _length += data.Length;
        }


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

        public void WriteInt64(int fieldNumber, long value)
        {
            if (value == 0L) return;
            WriteTag(fieldNumber, WireVarint);
            WriteVarint(unchecked((ulong)value));
        }

        public void WriteFloat(int fieldNumber, float value)
        {
            if (value == 0f) return;
            WriteTag(fieldNumber, Wire32Bit);
            WriteFixed32(FloatToUInt32(value));
        }

        public void WriteFloatPresent(int fieldNumber, float value)
        {
            if (_disposed) return;
            WriteTag(fieldNumber, Wire32Bit);
            WriteFixed32(FloatToUInt32(value));
        }

        public void WriteDouble(int fieldNumber, double value)
        {
            if (value == 0.0) return;
            WriteTag(fieldNumber, Wire64Bit);
            WriteFixed64(DoubleToUInt64(value));
        }

        public void WriteEnum(int fieldNumber, int value)
        {
            if (value == 0) return;
            WriteTag(fieldNumber, WireVarint);
            WriteVarint(unchecked((ulong)value));
        }

        public void WriteSubMessage(int fieldNumber, ProtobufWriter sub)
        {
            if (_disposed || sub == null || sub._disposed) return;
            if (sub.Length == 0) return;
            WriteTag(fieldNumber, WireLengthDelimited);
            WriteVarint((ulong)sub.Length);
            WriteRawBytes(sub._buffer.AsSpan(0, sub.Length));
        }

        private bool EnsureCapacity(int additionalBytes)
        {
            if (additionalBytes < 0 || additionalBytes > int.MaxValue - _length) return false;
            int required = _length + additionalBytes;
            if (required <= _buffer.Length) return true;

            int nextCapacity = _buffer.Length <= int.MaxValue / 2
                ? _buffer.Length * 2
                : required;
            if (nextCapacity < required)
            {
                nextCapacity = required;
            }

            var nextBuffer = ArrayPool<byte>.Shared.Rent(nextCapacity);
            Buffer.BlockCopy(_buffer, 0, nextBuffer, 0, _length);
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = nextBuffer;
            return true;
        }


        private static uint FloatToUInt32(float value)
        {
            return unchecked((uint)BitConverter.SingleToInt32Bits(value));
        }

        private static ulong DoubleToUInt64(double value)
        {
            return unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        }
    }
}
