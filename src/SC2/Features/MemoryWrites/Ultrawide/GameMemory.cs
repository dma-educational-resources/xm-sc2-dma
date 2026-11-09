using System.Buffers.Binary;
using System.Text;

namespace SC2Radar.SC2.Features.MemoryWrites.Ultrawide;

/// <summary>A read of the game's memory failed (unmapped page, or the game closed).</summary>
sealed class MemoryReadException(ulong address) : Exception($"could not read 0x{address:X}");

/// <summary>A PE section of the game module, read once: its RVA, its bytes, and the same bytes as a
/// Latin-1 string (one char per byte) so the code patterns can run as ordinary regexes.</summary>
sealed record Section(long Rva, byte[] Data, string Text);

/// <summary>
/// The sc2-dma GameMemory API (used unchanged by AddressFinder, WideMode and BottomConsole),
/// backed by the radar's own <see cref="SC2Memory"/> connection instead of a separate DmaConnection.
/// One instance per game process; the cached sections and resolved addresses die with it.
/// </summary>
sealed class GameMemory
{
    readonly SC2Memory _mem;
    readonly Dictionary<string, Section> _sections = new();

    public uint  Pid  { get; }
    public ulong Base { get; }
    public Addresses? Addr { get; set; }

    public GameMemory(SC2Memory mem)
    {
        _mem = mem;
        Pid  = mem.Pid;
        Base = mem.ExeBase;
    }

    // ── reads ────────────────────────────────────────────────────────────

    public byte[]? TryReadAbs(ulong addr, int size) => _mem.TryReadExact(addr, size);
    public byte[] ReadAbs(ulong addr, int size) => TryReadAbs(addr, size) ?? throw new MemoryReadException(addr);
    public byte[] Read(long rva, int size) => ReadAbs(Base + (ulong)rva, size);
    public uint ReadU32(long rva) => BinaryPrimitives.ReadUInt32LittleEndian(Read(rva, 4));
    public ulong ReadU64(long rva) => BinaryPrimitives.ReadUInt64LittleEndian(Read(rva, 8));
    public uint ReadAbsU32(ulong addr) => BinaryPrimitives.ReadUInt32LittleEndian(ReadAbs(addr, 4));
    public ulong ReadAbsU64(ulong addr) => BinaryPrimitives.ReadUInt64LittleEndian(ReadAbs(addr, 8));
    public float ReadAbsF32(ulong addr) => BinaryPrimitives.ReadSingleLittleEndian(ReadAbs(addr, 4));

    // ── writes ───────────────────────────────────────────────────────────

    /// <summary>Queue a write at an absolute address into the memory-write features' batch.</summary>
    public void QueueAbs(ScatterWriteHandle writes, ulong addr, byte[] data) => writes.AddBufferEntry<byte>(addr, data);

    /// <summary>Queue a write at a module offset.</summary>
    public void Queue(ScatterWriteHandle writes, long rva, byte[] data) => QueueAbs(writes, Base + (ulong)rva, data);

    // ── module sections ──────────────────────────────────────────────────

    /// <summary>A whole PE section, read once and cached. Pages that fail to read stay zero; the
    /// patterns that run over it must match exactly once, so a hole cannot produce a wrong address.</summary>
    public Section Section(string name)
    {
        lock (_sections)
        {
            if (_sections.TryGetValue(name, out var cached))
                return cached;
            byte[] headers = Read(0, 0x1000);
            int lfanew = BinaryPrimitives.ReadInt32LittleEndian(headers.AsSpan(0x3C));
            int count = BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(lfanew + 6));
            int optSize = BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(lfanew + 20));
            int table = lfanew + 24 + optSize;
            for (int i = 0; i < count; i++)
            {
                int off = table + 40 * i;
                if (Encoding.ASCII.GetString(headers, off, 8).TrimEnd('\0') != name)
                    continue;
                int vsize = BinaryPrimitives.ReadInt32LittleEndian(headers.AsSpan(off + 8));
                int rva = BinaryPrimitives.ReadInt32LittleEndian(headers.AsSpan(off + 12));
                var data = _mem.ReadZeroPad(Base + (ulong)rva, vsize);
                return _sections[name] = new Section(rva, data, Encoding.Latin1.GetString(data));
            }
            throw new LookupFailed($"no {name} section found");
        }
    }

    /// <summary>Drop the cached sections once addresses are resolved (they are tens of MB).</summary>
    public void ClearCache()
    {
        lock (_sections)
            _sections.Clear();
    }

    /// <summary>An engine option object's name (inline for short names, else via a heap pointer).</summary>
    public string OptionName(long rva)
    {
        byte[]? obj = TryReadAbs(Base + (ulong)rva, 0x20);
        if (obj is null)
            return "";
        int length = (int)(BinaryPrimitives.ReadUInt32LittleEndian(obj.AsSpan(8)) >> 2);
        if (length is > 0 and <= 8 && obj.AsSpan(0x10, length).IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) < 0)
            return Encoding.ASCII.GetString(obj, 0x10, length);
        ulong ptr = BinaryPrimitives.ReadUInt64LittleEndian(obj.AsSpan(0x10));
        if (ptr == 0 || length is <= 0 or >= 64)
            return "";
        byte[]? text = TryReadAbs(ptr, length);
        return text is null ? "" : Encoding.ASCII.GetString(text);
    }
}
