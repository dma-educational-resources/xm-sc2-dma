using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    public sealed partial class SC2Memory
    {
        public ushort? ReadU16(ulong addr) => ReadBytes(addr, 2) is { } b ? BinaryPrimitives.ReadUInt16LittleEndian(b) : null;
        public bool WriteU16(ulong addr, ushort v, ScatterWriteHandle? writes = null) => Write(addr, v, writes);

        /// <summary>
        /// Your detectors' behaviours (a buff whose catalog Modification has Detect, entry + 0x58 + 0x7F0): detection is
        /// max(Detect x count) over the instance's per-player stack counts (u16 at inst + 0xB0 + 2 x slot, the case for
        /// query 0x11 in [exe+0x6D15E0]), so a count of 3 on your own instance gives 3 x 11 without touching the catalog
        /// entry the AI's detectors share. Returns (instance, count address, original count, Detect).
        /// </summary>
        public List<(ulong Inst, ulong CountAddr, ushort Count, float Detect)> DetectorStacks(ulong rec)
        {
            var list = new List<(ulong, ulong, ushort, float)>();
            foreach (ulong inst in UnitBehaviors(rec))
            {
                ulong q = ReadU64(inst + 0x60), be = CatalogEntry(q);
                if (!IsUserPtr(be) || ReadBytes(be + 0x58 + 0x7F0, 4) is not { } db) continue;
                int det = BinaryPrimitives.ReadInt32LittleEndian(db);
                if (det <= 0 || det > 64 * 4096) continue;
                ulong ca = inst + 0xB0 + ((q >> 59) & 31) * 2;
                if (ReadBytes(ca, 2) is not { } cb) continue;
                ushort c = BinaryPrimitives.ReadUInt16LittleEndian(cb);
                if (c is > 0 and < 16) list.Add((inst, ca, c, det / 4096f));
            }
            return list;
        }
    }
}
