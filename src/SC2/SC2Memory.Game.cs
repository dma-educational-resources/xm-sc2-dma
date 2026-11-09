using VmmSharpEx.Options;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Raw process access for the Ultrawide feature's display monitor (Features/MemoryWrites/Ultrawide).
    /// Exact-length reads; nothing here is cached.
    /// </summary>
    public sealed partial class SC2Memory
    {
        public uint  Pid     => _pid;
        public ulong ExeBase => _exeBase;

        /// <summary>Read exactly <paramref name="size"/> bytes straight from the device, or null.</summary>
        public unsafe byte[]? TryReadExact(ulong addr, int size)
        {
            var buf = new byte[size];
            fixed (byte* p = buf)
            {
                try
                {
                    _roundTrips++;
                    if (!_vmm.MemRead(_pid, addr, p, (uint)size, out uint got, VmmFlags.NOCACHE) || got != size)
                        return null;
                }
                catch { return null; }
            }
            return buf;
        }

        /// <summary>Read a large range; pages that fail stay zero.</summary>
        public unsafe byte[] ReadZeroPad(ulong addr, int size)
        {
            var buf = new byte[size];
            const int chunk = 0x100000;
            fixed (byte* p = buf)
            {
                for (int o = 0; o < size; o += chunk)
                {
                    try { _vmm.MemRead(_pid, addr + (ulong)o, p + o, (uint)Math.Min(chunk, size - o), out _, VmmFlags.ZEROPAD_ON_FAIL); }
                    catch { }
                }
            }
            return buf;
        }

    }
}
