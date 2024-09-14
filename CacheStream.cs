using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Hi3Helper.EncTool
{
    public class CacheStream : Stream
    {
        private const int _seedBoxLen = 0x270;
        private const int _keyLen = 0x200;
        private const int _dataLen = 0x80;
        private const int _bufLen = 0x1000;

        private int _keyOffset;
        private readonly long _streamOffset;

        private static int[] seedBox = new int[_seedBoxLen];
        private static byte[] key = new byte[_keyLen];

        private protected readonly Stream _stream;
        private bool _allowDispose;
        public CacheStream(string path, FileMode fileMode, FileAccess fileAccess, FileShare fileShare = FileShare.Read, FileOptions fileOptions = FileOptions.None, bool keepOpen = false, int preSeed = 0)
            : base()
        {
            _stream = new FileStream(path, fileMode, fileAccess, fileShare, 4 << 10, fileOptions);
            _keyOffset = GenerateSeed(_stream, preSeed);
            _streamOffset = _stream.Position;
            _allowDispose = !keepOpen;
        }

        public CacheStream(Stream stream, bool keepOpen = false, int preSeed = 0)
            : base()
        {
            _stream = stream;
            _keyOffset = GenerateSeed(_stream, preSeed);
            _streamOffset = _stream.Position;
            _allowDispose = !keepOpen;
        }

        ~CacheStream() => Dispose();

        private int GenerateSeed(Stream stream, int preSeed)
        {
            // Get the seed and read the data for the key generation
            byte[] data = new byte[_dataLen];
            using BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true);

            int seedAdd = 0;

            // Get the flag byte and boxes
            byte flag = reader.ReadByte();
            if (flag != 1 && preSeed != 0) seedAdd = preSeed;

            // Pass 1: Get the first seedBox
            seedBox[0] = reader.ReadInt32();
            // Pass the add seed + first seedBox to RNG and get the next value
            seedBox[0] = new Random(seedBox[0] + seedAdd).Next();

            _ = stream.Read(data, 0, _dataLen);

            // Phase 2: Generate the seeds
            for (int i = 0, j = 1; j < _seedBoxLen; i++, j++)
            {
                seedBox[j] = (int)(j + 0x6C078965 * (seedBox[i] ^ (uint)(seedBox[i] >> 30)));
            }

            // Phase 3: Recalculate the seeds
            for (int i = 0, j = 1; i < _seedBoxLen; i++, j++)
            {
                int box = (seedBox[i]) ^ ((seedBox[i]) ^ (seedBox[j % _seedBoxLen])) & 0x7FFFFFFF;
                seedBox[i] = (box >> 1) ^ seedBox[(i + 397) % _seedBoxLen];

                if ((box & 1) != 0) unchecked { seedBox[i] ^= (int)0x9908B0DF; }
            }

            // Phase 4: Generate the keys
            for (int i = 0, step = 0; i < _dataLen; i++, step += 4)
            {
                unsafe
                {
                    int postSeed = GetSeed(i);
                    byte* bytePointer = (byte*)&postSeed;

                    bytePointer[0] ^= data[(i * 4) % _dataLen];
                    bytePointer[1] ^= data[(i * 4 + 7) % _dataLen];
                    bytePointer[2] ^= data[(i * 4 + 13) % _dataLen];
                    bytePointer[3] ^= data[(i * 4 + 11) % _dataLen];

                    Marshal.Copy((nint)bytePointer, key, step, 4);
                }
            }

            // Phase 5: Return the offset
            return GetSeed(_dataLen);
        }

        private int GetSeed(int i)
        {
            int lastSeed = seedBox[i];
            int shiftSeed = lastSeed >> 11;

            long seedA = ((((shiftSeed) ^ lastSeed) & 0xFF3A58AD) << 7) ^ (shiftSeed) ^ lastSeed;
            int seedB = (int)(((seedA & 0xFFFFDF8C) << 15) ^ seedA);
            int seedResult = seedB ^ (seedB >> 18);

            return seedResult;
        }

        private int ReadBytes(Span<byte> buffer)
        {
            long lastPos = Position;
            int i = _stream.Read(buffer);
            ReadXOR(buffer, i, lastPos);
            return i;
        }

        private int ReadBytes(byte[] buffer, int offset, int count)
        {
            long lastPos = Position;
            int i = _stream.Read(buffer, offset, count);
            ReadXOR(buffer, i, lastPos);
            return i;
        }

        public override int Read(Span<byte> buffer) => ReadBytes(buffer);
        public override int Read(byte[] buffer, int offset, int count) => ReadBytes(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void CopyTo(Stream destination, int bufferSize)
        {
            int read;
            byte[] buf = new byte[bufferSize];
            while ((read = Read(buf, 0, bufferSize)) > 0)
            {
                destination.Write(buf, 0, read);
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override void Flush() => _stream.Flush();

        public override long Length => _stream.Length - _streamOffset;

        public override long Position
        {
            get => _stream.Position - _streamOffset;
            set => _stream.Position = value + _streamOffset;
        }

        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset + _streamOffset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadXOR(Span<byte> buffer, int i, long realLastPos)
        {
            long realKeyOffset = _keyOffset + realLastPos;
            for (int j = 0; j < i; j++)
            {
                buffer[j] ^= key[realKeyOffset++ % _keyLen];
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _stream.Dispose();
            }
        }
    }
}
