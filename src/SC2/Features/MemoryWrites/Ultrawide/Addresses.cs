using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace SC2Radar.SC2.Features.MemoryWrites.Ultrawide;

/// <summary>A code pattern did not match exactly once, so the tool refuses to write.</summary>
sealed class LookupFailed(string what) : Exception(what);

/// <summary>Build-specific addresses found in the running game by <see cref="AddressFinder.Find"/>.
/// Values ending in Rva are offsets from the module base; the others are struct field offsets.</summary>
sealed class Addresses
{
    public required long ListenValue { get; init; }
    public required long RenderValue { get; init; }
    public required long StoredWidth { get; init; }
    public required long StoredHeight { get; init; }
    public required long DisplayMode { get; init; }

    public bool Console { get; set; }              // set once the console addresses below are found
    public string? ConsoleError { get; set; }
    public long GameUiPointer { get; set; }
    public uint GameUiConsolePanel { get; set; }
    public uint PanelCenterModel { get; set; }
    public long ModelEntryVtable { get; set; }
}

/// <summary>
/// Finds the addresses the tool needs. Port of memory.py: none of them are hard-coded, because they
/// move with every game patch; each is located by recognizing the code that uses it, and each
/// setting is read back by name before anything is written, so a build where a pattern no longer
/// matches fails safe. This is the only file that holds game-code patterns.
///
/// The patterns are the Python ones unchanged. They run as .NET regexes over the section's Latin-1
/// string (one char per byte), where \xNN and "." (Singleline) mean the same as in Python bytes regexes.
/// </summary>
static class AddressFinder
{
    // Read-only aspect-ratio cap, found by signature (build independent). Used for `status` only;
    // it lives in copy-protected memory and the game never writes it.
    static readonly (string Name, string Pattern, int DispOff, int NextOff)[] CapSignatures =
    [
        ("window size clamp", "F3 0F 10 0D ?? ?? ?? ?? B8 00 04 00 00 F3 0F 10 15", 4, 8),
        ("fullscreen clamp", "8B 41 24 0F 57 C9 F3 0F 10 15 ?? ?? ?? ?? 0F 57 C0 F3 48 0F 2A C0", 10, 14),
        ("resolution list filter",
         "0F 29 B4 24 D0 00 00 00 F3 0F 10 35 ?? ?? ?? ?? 0F 29 BC 24 C0 00 00 00 F3 0F 10 3D", 12, 16),
    ];

    static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static string Escape(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 4);
        foreach (byte b in bytes)
            sb.Append($"\\x{b:x2}");
        return sb.ToString();
    }

    static int I32(byte[] blob, int at) => BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(at));
    static uint U32(byte[] blob, int at) => BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at));

    /// <summary>Offset (within blob) that a rel32 / rip-relative operand stored at `at` points to.</summary>
    static long Rel32(byte[] blob, int at) => at + 4 + I32(blob, at);

    static T Only<T>(string what, IEnumerable<T> hits)
    {
        var list = hits.Take(2).ToList();
        if (list.Count != 1)
            throw new LookupFailed($"{what} ({(list.Count == 0 ? 0 : hits.Count())} matches, expected 1)");
        return list[0];
    }

    /// <summary>`size` bytes of a section starting at `rva`, clamped to the section like a Python slice.</summary>
    static byte[] At(Section s, long rva, int size)
    {
        long start = Math.Clamp(rva - s.Rva, 0, s.Data.Length);
        long end = Math.Clamp(rva - s.Rva + size, start, s.Data.Length);
        return s.Data[(int)start..(int)end];
    }

    /// <summary>(rva, sites) for the read-only aspect cap, found via its code references.</summary>
    public static (long? Rva, Dictionary<string, long> Sites) LocateCap(GameMemory game)
    {
        var text = game.Section(".text");
        var sites = new Dictionary<string, long>();
        var targets = new HashSet<long>();
        foreach (var (name, pattern, dispOff, nextOff) in CapSignatures)
        {
            string rx = string.Concat(pattern.Split(' ').Select(t => t == "??" ? "." : $"\\x{t.ToLowerInvariant()}"));
            var matches = Rx(rx).Matches(text.Text);
            if (matches.Count != 1)
                continue;
            int m = matches[0].Index;
            sites[name] = text.Rva + m;
            targets.Add(text.Rva + m + nextOff + I32(text.Data, m + dispOff));
        }
        return (targets.Count == 1 ? targets.Single() : null, sites);
    }

    /// <summary>The engine option object named `name` that owns the value at valueRva (it sits just before).</summary>
    static long OptionObject(GameMemory game, long valueRva, string name)
    {
        for (long obj = valueRva & ~7L; obj > ((valueRva - 0x100) & ~7L) - 8; obj -= 8)
            if (game.OptionName(obj) == name)
                return obj;
        throw new LookupFailed($"the '{name}' option (no object with that name before its value)");
    }

    /// <summary>Addresses used to widen the window. Anchored on the window-size check, which reads them.</summary>
    static Addresses ResolveWindow(GameMemory game)
    {
        var text = game.Section(".text");
        // The window-size check: calls the client-API check, then reads the stored width and height
        // and compares the display mode with 1 before it applies the aspect cap.
        var m = Only("the window-size check", Rx(
            @"\xe8(.{4})\x84\xc0\x74.\xb0\x01\x48\x83\xc4.\x5f\x5e\xc3" +
            @"\x8b\x05(.{4}).{0,48}?\x8b\x05(.{4}).{0,64}?\x83\x3d(.{4})\x01").Matches(text.Text));
        long clientApi = text.Rva + Rel32(text.Data, m.Groups[1].Index);
        long storedWidth = text.Rva + Rel32(text.Data, m.Groups[2].Index);
        long storedHeight = text.Rva + Rel32(text.Data, m.Groups[3].Index);
        // cmp dword [rip+disp], imm8: the displacement is relative to the end, after the imm8
        long displayMode = text.Rva + m.Groups[4].Index + 5 + I32(text.Data, m.Groups[4].Index);

        // The client-API check calls two one-line getters: `listen` (dword) and `gameStateRender` (byte).
        byte[] api = At(text, clientApi, 32);
        var body = Rx(@"\A\x48\x83\xec.\xe8(.{4})\x84\xc0\x74.\xe8(.{4})\x84\xc0").Match(Encoding.Latin1.GetString(api));
        if (!body.Success)
            throw new LookupFailed("the client-API check");
        long listenFn = clientApi + Rel32(api, body.Groups[1].Index);
        long renderFn = clientApi + Rel32(api, body.Groups[2].Index);
        byte[] listenCode = At(text, listenFn, 16), renderCode = At(text, renderFn, 8);
        var lg = Rx(@"\A\xf7\x05(.{4})\xfc\xff\xff\xff\x0f\x95\xc0\xc3").Match(Encoding.Latin1.GetString(listenCode));
        var rg = Rx(@"\A\x0f\xb6\x05(.{4})\xc3").Match(Encoding.Latin1.GetString(renderCode));
        if (!lg.Success || !rg.Success)
            throw new LookupFailed("the listen / gameStateRender getters");
        long listenValue = listenFn + 10 + I32(listenCode, lg.Groups[1].Index);
        long renderValue = renderFn + 7 + I32(renderCode, rg.Groups[1].Index);
        foreach (var (value, name) in new[] { (listenValue, "listen"), (renderValue, "gameStateRender"), (displayMode, "displaymode") })
            OptionObject(game, value, name);
        return new Addresses
        {
            ListenValue = listenValue, RenderValue = renderValue,
            StoredWidth = storedWidth, StoredHeight = storedHeight, DisplayMode = displayMode,
        };
    }

    /// <summary>Addresses used to fit the bottom console, or LookupFailed.</summary>
    static (long GameUi, uint PanelField, uint Middle, long EntryVtable) ResolveConsole(GameMemory game)
    {
        var text = game.Section(".text");
        var rdata = game.Section(".rdata");
        int k = rdata.Data.AsSpan().IndexOf("\0UIContainer/ConsolePanel\0"u8);
        if (k < 0)
            throw new LookupFailed("the ConsolePanel layout path");
        long path = rdata.Rva + k + 1;

        // The game UI stores the ConsolePanel frame in a field right after looking up its layout path.
        var code = text.Data;
        int reference = Only("the ConsolePanel path reference",
            Enumerable.Range(0, code.Length - 7).Where(i =>
                code[i] == 0x48 && code[i + 1] == 0x8d && (code[i + 2] == 0x05 || code[i + 2] == 0x0d)
                && text.Rva + Rel32(code, i + 3) == path));
        int from = Math.Min(reference + 7, code.Length);
        var store = Rx(@"\x48\x89\x83(.{4})").Match(text.Text, from, Math.Min(0x60, code.Length - from));
        if (!store.Success)
            throw new LookupFailed("the ConsolePanel field");
        uint panelField = U32(code, store.Groups[1].Index);

        // The game UI global: the rip-relative pointer loaded most often right before that field is read.
        var votes = new Dictionary<long, int>();
        foreach (Match v in Rx(@"\x48\x8b[\x05\x0d](.{4}).{0,16}?\x48\x8b[\x88\x89]"
                               + Escape(BitConverter.GetBytes(panelField))).Matches(text.Text))
        {
            long g = text.Rva + Rel32(code, v.Groups[1].Index);
            votes[g] = votes.GetValueOrDefault(g) + 1;
        }
        var ranked = votes.OrderByDescending(kv => kv.Value).ToList();
        if (ranked.Count == 0 || ranked[0].Value < 3 || (ranked.Count > 1 && ranked[1].Value * 2 > ranked[0].Value))
            throw new LookupFailed($"the game UI pointer (votes {string.Join(", ", ranked.Take(3).Select(r => $"0x{r.Key:X}={r.Value}"))})");
        long gameUi = ranked[0].Key;

        // The ConsolePanel's three model frames are read in a row, 8 bytes apart; the middle one is ours.
        var trip = Rx(@"\x8b\x93(.{4})\x48\x8b\x8b(.{4})\xe8(.{4})").Matches(text.Text)
            .Select(x => (A: text.Rva + x.Index, M: x)).ToList();
        var middles = new HashSet<uint>();
        for (int i = 0; i + 2 < trip.Count; i++)
        {
            var t = new[] { trip[i], trip[i + 1], trip[i + 2] };
            if (t[2].A - t[0].A > 0x80)
                continue;
            long[] f = t.Select(x => (long)U32(code, x.M.Groups[2].Index)).ToArray();
            long[] e = t.Select(x => (long)U32(code, x.M.Groups[1].Index)).ToArray();
            int calls = t.Select(x => x.A + 18 + I32(code, x.M.Groups[3].Index)).Distinct().Count();
            if (f[1] - f[0] == 8 && f[2] - f[1] == 8 && e[1] - e[0] == 4 && e[2] - e[1] == 4 && calls == 1)
                middles.Add((uint)f[1]);
        }
        uint middle = Only("the console model fields", middles);

        // A model entry's vtable: stored just before the entry's scale is set to its default of 1.0.
        var vtables = new HashSet<long>();
        var lea = Rx(@"\x48\x8d\x05(.{4})\x48\x89\x03");
        foreach (Match s in Rx(@"\xc7\x43\x34\x00\x00\x80\x3f\xc7\x43\x38\x00\x00\x80\x3f").Matches(text.Text))
        {
            int window = s.Index - 0x40;
            if (window < 0)
                continue;
            var leas = lea.Matches(text.Text.Substring(window, 0x40));
            if (leas.Count > 0)
                vtables.Add(text.Rva + Rel32(code, window + leas[^1].Groups[1].Index));
        }
        long entryVtable = Only("the model entry vtable", vtables);
        return (gameUi, panelField, middle, entryVtable);
    }

    /// <summary>
    /// Locate every build-specific address in the running game and store them on game.Addr.
    /// The window addresses are required (LookupFailed if missing). The console addresses are
    /// optional: if they cannot be found, Addr.Console stays false and the console is left alone.
    /// </summary>
    public static Addresses Find(GameMemory game)
    {
        var a = game.Addr = ResolveWindow(game);
        try
        {
            (a.GameUiPointer, a.GameUiConsolePanel, a.PanelCenterModel, a.ModelEntryVtable) = ResolveConsole(game);
            a.Console = true;
        }
        catch (LookupFailed e)
        {
            a.ConsoleError = e.Message;
        }
        return a;
    }
}
