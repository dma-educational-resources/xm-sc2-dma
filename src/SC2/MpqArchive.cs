using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace SC2Radar.SC2
{
    /// <summary>
    /// A minimal reader for an MPQ archive held in a byte array (SC2 keeps a map's .SC2Map archive in memory): the
    /// encrypted hash and block tables, and files stored whole or in sectors, plain or zlib-compressed. Enough for the
    /// small files of a map (Minimap.tga, MapInfo, ...); bzip2 / LZMA / encrypted files are reported as unreadable.
    /// </summary>
    public sealed class MpqArchive
    {
        static readonly uint[] Crypt = BuildCrypt();
        readonly byte[] _data;
        readonly int _sectorSize;
        readonly (uint A, uint B, uint Block)[] _hash;
        readonly (uint Pos, uint CSize, uint Size, uint Flags)[] _blocks;

        public MpqArchive(byte[] data)
        {
            _data = data;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x1A51504D) throw new InvalidDataException("not an MPQ");
            _sectorSize = 512 << BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
            uint hashPos = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)), blockPos = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
            int hashCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24)), blockCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(28));
            var ht = Table(hashPos, hashCount * 4, Hash("(hash table)", 3));
            var bt = Table(blockPos, blockCount * 4, Hash("(block table)", 3));
            _hash = Enumerable.Range(0, hashCount).Select(i => (ht[i * 4], ht[i * 4 + 1], ht[i * 4 + 3])).ToArray();
            _blocks = Enumerable.Range(0, blockCount).Select(i => (bt[i * 4], bt[i * 4 + 1], bt[i * 4 + 2], bt[i * 4 + 3])).ToArray();
        }

        uint[] Table(uint pos, int words, uint key)
        {
            var t = new uint[words];
            for (int i = 0; i < words; i++) t[i] = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)pos + i * 4));
            Decrypt(t, key);
            return t;
        }

        /// <summary>Table sanity for research: used hash entries, existing blocks, the first few blocks.</summary>
        public string Describe() =>
            $"sector {_sectorSize}, hash {_hash.Length} ({_hash.Count(h => h.Block < _blocks.Length)} used), blocks {_blocks.Length} " +
            $"({_blocks.Count(b => (b.Flags & 0x80000000) != 0)} exist): " +
            string.Join(" ", _blocks.Take(6).Select(b => $"[{b.Pos:X} {b.CSize}/{b.Size} {b.Flags:X8}]"));

        /// <summary>A file's bytes, null when it isn't there or can't be read (encrypted, bzip2, ...).</summary>
        public byte[]? Read(string name)
        {
            uint start = Hash(name, 0) % (uint)_hash.Length, a = Hash(name, 1), b = Hash(name, 2);
            for (uint i = start, n = 0; n < _hash.Length; i = (i + 1) % (uint)_hash.Length, n++)
            {
                var h = _hash[i];
                if (h.Block == 0xFFFFFFFF) return null;   // never used: not in the archive
                if (h.A != a || h.B != b || h.Block >= _blocks.Length) continue;
                return ReadBlock(_blocks[h.Block]);
            }
            return null;
        }

        byte[]? ReadBlock((uint Pos, uint CSize, uint Size, uint Flags) bl)
        {
            const uint Exists = 0x80000000, SingleUnit = 0x01000000, Compressed = 0x200, Encrypted = 0x10000, Imploded = 0x100;
            if ((bl.Flags & Exists) == 0 || (bl.Flags & (Encrypted | Imploded)) != 0 || bl.Pos + bl.CSize > _data.Length) return null;
            var output = new byte[bl.Size];
            if ((bl.Flags & SingleUnit) != 0 || (bl.Flags & Compressed) == 0)
            {
                var raw = _data.AsSpan((int)bl.Pos, (int)bl.CSize);
                return (bl.Flags & Compressed) != 0 && bl.CSize < bl.Size ? Decompress(raw, (int)bl.Size) : raw[..(int)bl.Size].ToArray();
            }
            int sectors = (int)((bl.Size + _sectorSize - 1) / _sectorSize);
            for (int s = 0; s < sectors; s++)
            {
                uint from = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)bl.Pos + s * 4));
                uint to = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)bl.Pos + s * 4 + 4));
                int want = (int)Math.Min(_sectorSize, bl.Size - s * _sectorSize);
                var raw = _data.AsSpan((int)(bl.Pos + from), (int)(to - from));
                var part = raw.Length < want ? Decompress(raw, want) : raw[..want].ToArray();
                if (part == null) return null;
                part.CopyTo(output, s * _sectorSize);
            }
            return output;
        }

        /// <summary>A compressed sector: a mask byte, then the data. zlib only.</summary>
        static byte[]? Decompress(ReadOnlySpan<byte> raw, int size)
        {
            if (raw.Length < 1 || raw[0] != 0x02) return null;
            using var z = new ZLibStream(new MemoryStream(raw[1..].ToArray()), CompressionMode.Decompress);
            var o = new byte[size];
            int got = 0;
            while (got < size && z.Read(o, got, size - got) is > 0 and var n) got += n;
            return got == size ? o : null;
        }

        static uint[] BuildCrypt()
        {
            var t = new uint[0x500];
            uint seed = 0x00100001;
            for (int i = 0; i < 0x100; i++)
                for (int j = i, k = 0; k < 5; k++, j += 0x100)
                {
                    seed = (seed * 125 + 3) % 0x2AAAAB; uint hi = (seed & 0xFFFF) << 16;
                    seed = (seed * 125 + 3) % 0x2AAAAB; t[j] = hi | (seed & 0xFFFF);
                }
            return t;
        }

        static uint Hash(string s, int type)
        {
            uint seed1 = 0x7FED7FED, seed2 = 0xEEEEEEEE;
            foreach (char c0 in s.ToUpperInvariant().Replace('/', '\\'))
            {
                uint c = c0;
                seed1 = Crypt[type * 0x100 + c] ^ (seed1 + seed2);
                seed2 = c + seed1 + seed2 + (seed2 << 5) + 3;
            }
            return seed1;
        }

        static void Decrypt(uint[] data, uint key)
        {
            uint seed = 0xEEEEEEEE;
            for (int i = 0; i < data.Length; i++)
            {
                seed += Crypt[0x400 + (key & 0xFF)];
                uint v = data[i] ^ (key + seed);
                key = ((~key << 21) + 0x11111111) | (key >> 11);
                seed = v + seed + (seed << 5) + 3;
                data[i] = v;
            }
        }
    }
}
