using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    public sealed partial class SC2Memory
    {
        /// <summary>Find every 16-byte-aligned heap qword equal to <paramref name="vt"/>.</summary>
        List<ulong> FindInstances(ulong vt)
        {
            var res = new List<ulong>();
            var pte = _vmm.Map_GetPTE(_pid, fIdentifyModules: true) ?? [];
            var buf = new byte[256 * PAGE];
            var ok  = new bool[256];
            foreach (var e in pte.Where(e => !e.fS && e.fW && e.cbSize > 0 && e.cbSize <= 2048ul * 1024 * 1024))
            {
                for (ulong roff = 0; roff < e.cbSize; roff += (ulong)buf.Length)
                {
                    int len = (int)Math.Min((ulong)buf.Length, e.cbSize - roff);
                    int pages = (len + PAGE - 1) / PAGE;
                    ulong va = e.vaBase + roff;
                    ReadRange(va, buf.AsSpan(0, pages * PAGE), ok.AsSpan(0, pages));
                    for (int pi = 0; pi < pages; pi++)
                    {
                        if (!ok[pi]) continue;
                        for (int i = 0; i < PAGE; i += 16)
                            if (BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(pi * PAGE + i, 8)) == vt)
                                res.Add(va + (ulong)(pi * PAGE + i));
                    }
                }
            }
            res.Sort();
            return res;
        }
    }
}
