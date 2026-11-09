using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Code-signature resolution for SC2_x64.exe globals/vtables, so a game patch doesn't need new RVAs.
    /// A signature is hex bytes with "?" wildcards; DispOffset is where the rel32 of a RIP-relative
    /// operand starts, and the referenced address is (disp end + InstrTail) + disp.
    /// </summary>
    public sealed partial class SC2Memory
    {
        sealed record CodeSig(string Name, string Pattern, int DispOffset, int InstrTail = 0);

        /// <summary>Executable sections of the exe image: (RVA, bytes).</summary>
        List<(uint Rva, byte[] Data)> ReadExeCode()
        {
            var res = new List<(uint, byte[])>();
            var hdr = ReadBytes(_exeBase, PAGE);
            if (hdr is not { Length: PAGE }) return res;
            int nt = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(0x3C));
            if (nt <= 0 || nt > PAGE - 0x108) return res;
            int nSec = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(nt + 6));
            int optSize = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(nt + 0x14));
            int sec = nt + 0x18 + optSize;
            for (int i = 0; i < nSec && sec + (i + 1) * 40 <= PAGE; i++)
            {
                var s = hdr.AsSpan(sec + i * 40, 40);
                uint vsize = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
                uint rva   = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
                uint ch    = BinaryPrimitives.ReadUInt32LittleEndian(s[0x24..]);
                if ((ch & 0x20000000) == 0 || vsize == 0 || vsize > 512u * 1024 * 1024) continue;   // IMAGE_SCN_MEM_EXECUTE
                int pages = (int)((vsize + PAGE - 1) / PAGE);
                var buf = new byte[pages * PAGE];
                var ok  = new bool[pages];
                ReadRange(_exeBase + rva, buf, ok);
                res.Add((rva, buf));
            }
            return res;
        }

        static (byte[] Bytes, bool[] Mask) ParsePattern(string p)
        {
            var toks = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var b = new byte[toks.Length];
            var m = new bool[toks.Length];
            for (int i = 0; i < toks.Length; i++)
                if (!toks[i].StartsWith('?')) { b[i] = Convert.ToByte(toks[i], 16); m[i] = true; }
            return (b, m);
        }

        /// <summary>All match offsets (section RVA + index) of a pattern in the code sections, up to <paramref name="max"/>.</summary>
        static List<uint> FindPattern(List<(uint Rva, byte[] Data)> code, string pattern, int max = 8)
        {
            var (pb, pm) = ParsePattern(pattern);
            var hits = new List<uint>();
            // anchor on the longest run of fixed bytes for a fast IndexOf
            int bestStart = 0, bestLen = 0;
            for (int i = 0; i < pb.Length;)
            {
                if (!pm[i]) { i++; continue; }
                int j = i; while (j < pb.Length && pm[j]) j++;
                if (j - i > bestLen) { bestStart = i; bestLen = j - i; }
                i = j;
            }
            var anchor = pb.AsSpan(bestStart, bestLen).ToArray();
            foreach (var (rva, data) in code)
            {
                int from = 0;
                while (hits.Count < max)
                {
                    int k = data.AsSpan(from).IndexOf(anchor);
                    if (k < 0) break;
                    int start = from + k - bestStart;
                    from += k + 1;
                    if (start < 0 || start + pb.Length > data.Length) continue;
                    bool ok = true;
                    for (int i = 0; i < pb.Length && ok; i++) ok = !pm[i] || data[start + i] == pb[i];
                    if (ok) hits.Add(rva + (uint)start);
                }
            }
            return hits;
        }

        /// <summary>Resolve a signature to the RVA its RIP-relative operand references; null unless exactly one match.</summary>
        static ulong? ResolveSig(List<(uint Rva, byte[] Data)> code, CodeSig sig, out int matches)
        {
            var hits = FindPattern(code, sig.Pattern, 2);
            matches = hits.Count;
            if (hits.Count != 1) return null;
            uint at = hits[0] + (uint)sig.DispOffset;
            foreach (var (rva, data) in code)
            {
                if (at < rva || at + 4 > rva + (uint)data.Length) continue;
                int disp = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)(at - rva), 4));
                return (ulong)((long)at + 4 + sig.InstrTail + disp);
            }
            return null;
        }
    }
}
