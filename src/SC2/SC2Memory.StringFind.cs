namespace SC2Radar.SC2
{
    public sealed partial class SC2Memory
    {
        /// <summary>Every readable writable region in 1 MB chunks: (address, bytes, page ok flags).</summary>
        void ForEachWritableChunk(Action<ulong, byte[], int, bool[]> visit, ulong maxRegion = 512ul << 20)
        {
            var pte = _vmm.Map_GetPTE(_pid, fIdentifyModules: true) ?? [];
            var buf = new byte[256 * PAGE]; var ok = new bool[256];
            foreach (var e in pte.Where(e => e.cbSize > 0 && e.fW && e.cbSize <= maxRegion))
                for (ulong off = 0; off < e.cbSize; off += (ulong)buf.Length)
                {
                    int len = (int)Math.Min((ulong)buf.Length, e.cbSize - off);
                    int pages = (len + PAGE - 1) / PAGE;
                    ReadRange(e.vaBase + off, buf.AsSpan(0, pages * PAGE), ok.AsSpan(0, pages));
                    visit(e.vaBase + off, buf, pages * PAGE, ok);
                }
        }
    }
}
