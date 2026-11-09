using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    public sealed partial class SC2Memory
    {
        /// <summary>A unit record's catalog entry (its type's data: Sight, Food, cost, ...), 0 when unknown. The game reads
        /// Sight from it live, so a write there changes what every unit using the entry sees, from the next vision update.</summary>
        public ulong UnitTypeEntry(ulong record) =>
            IsUserPtr(record) && ReadBytes(record, 0x40) is { } rb
                ? CatalogEntry(BinaryPrimitives.ReadUInt64LittleEndian(rb.AsSpan((rb[0x2E] & 0x20) != 0 ? 0x10 : 0x08))) : 0;

        /// <summary>A catalog entry's Sight (fixed 4096), null when unreadable.</summary>
        public int? ReadTypeSightFx(ulong entry) =>
            IsUserPtr(entry) && ReadBytes(entry + CatSightOff, 4) is { } b ? BinaryPrimitives.ReadInt32LittleEndian(b) : null;

        public bool WriteTypeSightFx(ulong entry, int sightFx, ScatterWriteHandle? writes = null) => IsUserPtr(entry) && Write(entry + CatSightOff, sightFx, writes);
        /// <summary>A unit's behaviour instances (UnitBehaviorGet [exe+0x997780] / the iterator [exe+0x898C00]): the container
        /// at unit + 0x110, first node = *(*(container + 0x18)) &amp; ~1, each node holding [node + 0x48] instances at node + 8 +
        /// 8 i (skipped when [inst + 0x68] bit 0 is set), the next node at [node] (none when its bit 0 is set).</summary>
        List<ulong> UnitBehaviors(ulong unit)
        {
            var list = new List<ulong>();
            ulong head = ReadU64(unit + 0x110 + 0x18);
            ulong node = IsUserPtr(head) ? ReadU64(head) & ~1UL : 0;
            for (int guard = 0; IsUserPtr(node) && guard < 64; guard++)
            {
                if (ReadBytes(node, 0x50) is not { } nb) break;
                int n = nb[0x48];
                if (n > 64 || ReadBytes(node + 8, n * 8) is not { } ib) break;
                for (int i = 0; i < n; i++)
                {
                    ulong inst = BinaryPrimitives.ReadUInt64LittleEndian(ib.AsSpan(i * 8));
                    if (IsObjPtr(inst) && ReadBytes(inst + 0x68, 1) is [var f] && (f & 1) == 0) list.Add(inst);
                }
                ulong next = BinaryPrimitives.ReadUInt64LittleEndian(nb);
                node = (next & 1) != 0 ? 0 : next;
            }
            return list;
        }
    }
}
