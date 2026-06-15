using System;
using System.IO;

namespace WinMaps.Pbf
{
    /// <summary>
    /// Low-level protobuf wire format reader. Reads varints, zigzag-encoded 
    /// signed integers, length-delimited fields, and fixed-width values from a stream.
    /// Designed for zero-allocation streaming of OSM PBF data.
    /// </summary>
    internal class PbfReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buf4 = new byte[4];

        public PbfReader(Stream stream)
        {
            _stream = stream;
        }

        public Stream BaseStream => _stream;

        public bool CanRead => _stream.Position < _stream.Length;

        public int ReadInt32BigEndian()
        {
            if (ReadExact(_buf4, 0, 4) < 4)
                throw new EndOfStreamException();
            return (_buf4[0] << 24) | (_buf4[1] << 16) | (_buf4[2] << 8) | _buf4[3];
        }

        public ulong ReadVarInt()
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                int b = _stream.ReadByte();
                if (b < 0) throw new EndOfStreamException();
                result |= ((ulong)(b & 0x7F)) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift > 63) throw new InvalidDataException("VarInt too long");
            }
        }

        public long ReadSignedVarInt()
        {
            ulong raw = ReadVarInt();
            // zigzag decode
            return (long)((raw >> 1) ^ (~(raw & 1) + 1));
        }

        public byte[] ReadBytes(int length)
        {
            byte[] data = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = _stream.Read(data, offset, length - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
            return data;
        }

        public void Skip(int length)
        {
            if (_stream.CanSeek)
            {
                _stream.Seek(length, SeekOrigin.Current);
            }
            else
            {
                byte[] skip = new byte[Math.Min(length, 8192)];
                int remaining = length;
                while (remaining > 0)
                {
                    int toRead = Math.Min(remaining, skip.Length);
                    int read = _stream.Read(skip, 0, toRead);
                    if (read <= 0) throw new EndOfStreamException();
                    remaining -= read;
                }
            }
        }

        /// <summary>
        /// Reads a field tag: returns (fieldNumber, wireType).
        /// Wire types: 0=varint, 1=64bit, 2=length-delimited, 5=32bit.
        /// </summary>
        public (int fieldNumber, int wireType) ReadTag()
        {
            ulong tag = ReadVarInt();
            return ((int)(tag >> 3), (int)(tag & 0x7));
        }

        /// <summary>
        /// Skips a field based on its wire type.
        /// </summary>
        public void SkipField(int wireType)
        {
            switch (wireType)
            {
                case 0: // varint
                    ReadVarInt();
                    break;
                case 1: // 64-bit
                    Skip(8);
                    break;
                case 2: // length-delimited
                    int len = (int)ReadVarInt();
                    Skip(len);
                    break;
                case 5: // 32-bit
                    Skip(4);
                    break;
                default:
                    throw new InvalidDataException($"Unknown wire type {wireType}");
            }
        }

        /// <summary>
        /// Reads packed varints from a byte array into the provided list.
        /// </summary>
        public static void ReadPackedVarInts(byte[] data, int offset, int length, Action<ulong> handler)
        {
            int end = offset + length;
            while (offset < end)
            {
                ulong result = 0;
                int shift = 0;
                while (true)
                {
                    if (offset >= end) throw new InvalidDataException("Truncated packed varint");
                    byte b = data[offset++];
                    result |= ((ulong)(b & 0x7F)) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }
                handler(result);
            }
        }

        /// <summary>
        /// Reads packed signed (zigzag) varints from a byte array.
        /// </summary>
        public static void ReadPackedSignedVarInts(byte[] data, int offset, int length, Action<long> handler)
        {
            ReadPackedVarInts(data, offset, length, raw =>
            {
                long value = (long)((raw >> 1) ^ (~(raw & 1) + 1));
                handler(value);
            });
        }

        private int ReadExact(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = _stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0) break;
                totalRead += read;
            }
            return totalRead;
        }
    }
}
