using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace SC2Radar.SC2
{
    /// <summary>A map-script struct member: byte offset in the struct and Galaxy type string.</summary>
    public sealed record ScriptMember(string Name, uint Offset, string Type);

    /// <summary>A map-script struct type ("lib1_gs_Units"): size in bytes and members in declaration order.</summary>
    public sealed record ScriptStruct(string Name, int Size, ScriptMember[] Members);

    /// <summary>A map-script global: Galaxy address and type string.</summary>
    public sealed record ScriptGlobal(string Name, uint Addr, string Type);

    /// <summary>A variable's value as last read: display text, and for unit / unitgroup variables the unit tags and the
    /// records of the live units holding them.</summary>
    public sealed record ScriptValue(string Text, uint[] Tags, ulong[] Records);

    /// <summary>
    /// The current map's script symbols. Galaxy type strings: one character for a simple type ('4' int, '3' fixed,
    /// '?' bool, 'U' unit, 'u' unitgroup, 'T' trigger, 'p' point, 's' string, ...), "&lt;name&gt;" for a struct, "[n]" in
    /// front for an array of n.
    /// </summary>
    public sealed class ScriptCatalog
    {
        public required ScriptGlobal[] Globals { get; init; }
        public required Dictionary<string, ScriptGlobal> ByName { get; init; }
        public required Dictionary<string, ScriptStruct> Structs { get; init; }

        /// <summary>Bytes a value of the type takes (bool 1, struct its size, array n x element, everything else 4).</summary>
        public int SizeOf(string type)
        {
            if (type.Length == 0) return 4;
            if (type[0] == '[' && type.IndexOf(']') is var e and > 1 && int.TryParse(type.AsSpan(1, e - 1), out int n))
                return n * SizeOf(type[(e + 1)..]);
            if (type[0] == '<' && Structs.TryGetValue(type.Trim('<', '>'), out var s)) return s.Size;
            return type == "?" ? 1 : 4;
        }

        /// <summary>"lib1_gv_units.lv_luckyLizard", "lib1_gv_waves[3].lv_count": Galaxy address and type, or null.</summary>
        public (uint Addr, string Type)? Resolve(string path)
        {
            path = path.Trim();
            int i = 0;
            while (i < path.Length && path[i] is not ('.' or '[')) i++;
            if (!ByName.TryGetValue(path[..i], out var g)) return null;
            uint addr = g.Addr; string type = g.Type;
            while (i < path.Length)
            {
                if (path[i] == '.')
                {
                    int j = ++i;
                    while (i < path.Length && path[i] is not ('.' or '[')) i++;
                    if (type.Length < 2 || type[0] != '<' || !Structs.TryGetValue(type.Trim('<', '>'), out var st)) return null;
                    var m = Array.Find(st.Members, x => x.Name.Equals(path[j..i], StringComparison.OrdinalIgnoreCase));
                    if (m == null) return null;
                    addr += m.Offset; type = m.Type;
                }
                else
                {
                    int close = path.IndexOf(']', i);
                    if (close < 0 || !int.TryParse(path.AsSpan(i + 1, close - i - 1), out int idx)) return null;
                    int te = type.IndexOf(']');
                    if (type.Length == 0 || type[0] != '[' || te < 0 || !int.TryParse(type.AsSpan(1, te - 1), out int n) || idx < 0 || idx >= n) return null;
                    type = type[(te + 1)..];
                    addr += (uint)(idx * SizeOf(type));
                    i = close + 1;
                }
            }
            return (addr, type);
        }

        /// <summary>A readable type name: "int", "fixed", "unit", "int[17]", "lib1_gs_Units".</summary>
        public static string TypeName(string type)
        {
            if (type.Length == 0) return "?";
            if (type[0] == '[' && type.IndexOf(']') is var e and > 1) return TypeName(type[(e + 1)..]) + type[..(e + 1)];
            if (type[0] == '<') return type.Trim('<', '>');
            return type switch
            {
                "4" => "int", "3" => "fixed", "?" => "bool", "U" => "unit", "u" => "unitgroup", "T" => "trigger", "p" => "point",
                "s" => "string", "t" => "timer", "x" => "text", "l" => "playergroup", "b" => "bank", "r" => "region", "2" => "color",
                _ => $"type '{type}'",
            };
        }
    }

    /// <summary>
    /// Map-script (Galaxy) variables by name, for any arcade / co-op / campaign map: the Special Forces lucky lizard
    /// (lib1_gv_units.lv_luckyLizard), its mystery boxes (lib1_gv_events.lv_mysteryBox), or anything else a map keeps.
    ///   VM state   [exe+0x3901B60] (5.0.x): +0x08 struct count, +0x18 struct records (0x68: name string +0, size +0x48,
    ///              member count +0x50, members +0x60); +0x20 global count, +0x30 global records (0x90: name entry +8 whose
    ///              string is at entry+0x18, u32 address +0x10 with u32 kind 2 = global +0x14, type string +0x20).
    ///              Member records 0xA8: name +0, type +0x48, u32 offset +0xA0.
    ///   strings    u32 (length &lt;&lt; 2 | 1), u32 flags (bit 1: characters on the heap, pointer at +8), else inline at +8.
    ///   memory     static array in the exe [exe+0x6295F10], size limit u32 [exe+0x7795F14] (the read routine bounds-checks).
    ///   values     unit = the unit's tag (record + 0); other handles are 16-bit indices into the table [exe+0x77A6D40]
    ///              (16-byte entries, object +8). A unitgroup object holds the count at +0x18 and the tag array at +0x28.
    /// The catalog is built once per match on the scans worker (a few bulk reads); variables are read only while someone
    /// asks for them (GetScript), twice a second.
    /// </summary>
    public sealed partial class SC2Memory
    {
        // the read routine: "cmp ecx,4; jl; lea eax,[rcx+r8]; cmp eax,[limit]; jg; movsxd rdx,ecx; lea rax,[memory]"
        static readonly CodeSig ScriptMemSig   = new("galaxy memory", "83 F9 04 7C ? 42 8D 04 01 3B 05 ? ? ? ? 7F ? 48 63 D1 48 8D 05 ? ? ? ?", 23);
        static readonly CodeSig ScriptLimitSig = new("galaxy memory limit", "83 F9 04 7C ? 42 8D 04 01 3B 05 ? ? ? ? 7F ? 48 63 D1 48 8D 05 ? ? ? ?", 11);
        // UnitRefToUnit: "movzx ecx,cx; shl rcx,4; add rcx,[handles]; cmp byte ptr [rcx+6],18h"
        static readonly CodeSig ScriptHandlesSig = new("galaxy handles", "0F B7 C9 48 C1 E1 04 48 03 0D ? ? ? ? 80 79 06 18", 10);
        // the variable-ref parser: "mov dword ptr [rcx+14h],-1; mov rdi,rcx; call GetState" and GetState = "lea rax,[state]; ret"
        const string ScriptStateCallPattern = "C7 41 14 FF FF FF FF 48 8B F9 E8 ? ? ? ? 4C 8D 44 24";

        ulong _scriptMemRva, _scriptLimitRva, _scriptHandlesRva, _scriptStateRva;
        volatile ScriptCatalog? _script;
        DateTime _nextScriptBuild, _nextScriptRead;
        readonly ConcurrentDictionary<string, DateTime> _scriptWanted = new(StringComparer.Ordinal);
        volatile Dictionary<string, ScriptValue> _scriptValues = new(StringComparer.Ordinal);

        /// <summary>The current map's script globals and structs, null until read (or when the signatures missed).</summary>
        public ScriptCatalog? Script => _script;

        /// <summary>Whether the script reader's signatures resolved.</summary>
        public bool ScriptAvailable => _scriptMemRva != 0 && _scriptStateRva != 0;

        /// <summary>The variable's last value (null until read, or when the path doesn't resolve); asking keeps it being
        /// read for a few seconds.</summary>
        public ScriptValue? GetScript(string path)
        {
            _scriptWanted[path] = DateTime.UtcNow;
            return _scriptValues.TryGetValue(path, out var v) ? v : null;
        }

        string ResolveScript(List<(uint Rva, byte[] Data)> code)
        {
            _scriptMemRva     = code.Count > 0 && ResolveSig(code, ScriptMemSig, out _) is { } m ? m : 0;
            _scriptLimitRva   = code.Count > 0 && ResolveSig(code, ScriptLimitSig, out _) is { } l ? l : 0;
            _scriptHandlesRva = code.Count > 0 && ResolveSig(code, ScriptHandlesSig, out _) is { } h ? h : 0;
            _scriptStateRva = 0;
            if (code.Count > 0 && FindPattern(code, ScriptStateCallPattern, 2) is [var at] && RelTarget(code, at + 11) is { } fn)
                foreach (var (rva, data) in code)
                {
                    if (fn < rva || fn + 8 > rva + (ulong)data.Length) continue;
                    var s = data.AsSpan((int)(fn - rva), 8);
                    if (s[0] == 0x48 && s[1] == 0x8D && s[2] == 0x05 && s[7] == 0xC3)
                        _scriptStateRva = (ulong)((long)fn + 7 + BinaryPrimitives.ReadInt32LittleEndian(s[3..]));
                }
            bool ok = ScriptAvailable && _scriptLimitRva != 0;
            if (!ok) XMLogging.WriteLine($"[Script] signatures: memory {_scriptMemRva:X} limit {_scriptLimitRva:X} handles {_scriptHandlesRva:X} state {_scriptStateRva:X}");
            return $"script {(ok ? "sig" : "missed")}";
        }

        void ResetScript()
        {
            _script = null;
            _scriptValues = new(StringComparer.Ordinal);
            _nextScriptBuild = DateTime.UtcNow.AddSeconds(2);   // let the map script initialise
        }

        /// <summary>Worker tick: build the catalog once per match, then read the wanted variables twice a second.</summary>
        void ScanScript()
        {
            if (!ScriptAvailable || !InGame) return;
            var now = DateTime.UtcNow;
            if (_script == null)
            {
                if (now < _nextScriptBuild) return;
                _nextScriptBuild = now.AddSeconds(5);
                _script = BuildScriptCatalog();
                if (_script != null) XMLogging.WriteLine($"[Script] {_script.Globals.Length} globals, {_script.Structs.Count} structs");
                return;
            }
            if (now < _nextScriptRead) return;
            _nextScriptRead = now.AddMilliseconds(500);
            foreach (var kv in _scriptWanted)
                if (now - kv.Value > TimeSpan.FromSeconds(5)) _scriptWanted.TryRemove(kv.Key, out _);
            if (_scriptWanted.IsEmpty) { if (_scriptValues.Count > 0) _scriptValues = new(StringComparer.Ordinal); return; }
            _scriptValues = ReadScriptValues(_script, [.. _scriptWanted.Keys]);
        }

        // ── catalog ──────────────────────────────────────────────────────────

        /// <summary>A Galaxy string from its 0x10-byte header in <paramref name="b"/>: the inline text, or the heap pointer
        /// and length to read it from.</summary>
        static (string? Inline, ulong Ptr, int Len) GalaxyStrAt(ReadOnlySpan<byte> b, int maxInline)
        {
            uint lenf = BinaryPrimitives.ReadUInt32LittleEndian(b), fl = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
            int len = (int)(lenf >> 2);
            if ((lenf & 1) == 0 || len is <= 0 or > 256) return (null, 0, 0);
            if (((fl >> 1) & 1) != 0) return (null, BinaryPrimitives.ReadUInt64LittleEndian(b[8..]), len);
            if (len > maxInline || 8 + len > b.Length) return (null, 0, 0);
            var s = b.Slice(8, len);
            foreach (byte c in s) if (c is < 32 or >= 127) return (null, 0, 0);
            return (System.Text.Encoding.ASCII.GetString(s), 0, len);
        }

        /// <summary>Reads heap strings in one batch.</summary>
        string?[] ReadGalaxyStrings(IReadOnlyList<(ulong Ptr, int Len)> refs)
        {
            var res = new string?[refs.Count];
            if (refs.Count == 0) return res;
            const int Max = 128;
            var b = new byte[refs.Count * Max]; var ok = new bool[refs.Count];
            if (!ReadValues([.. refs.Select(r => r.Ptr)], Max, b, ok)) return res;
            for (int i = 0; i < refs.Count; i++)
            {
                if (!ok[i]) continue;
                var s = b.AsSpan(i * Max, Math.Min(refs[i].Len, Max));
                bool printable = true;
                foreach (byte c in s) if (c is < 32 or >= 127) { printable = false; break; }
                if (printable) res[i] = System.Text.Encoding.ASCII.GetString(s);
            }
            return res;
        }

        byte[]? ReadBlock(ulong addr, int len)
        {
            if (len <= 0 || len > 64 << 20) return null;
            int pages = (int)(((addr & (PAGE - 1)) + (ulong)len + PAGE - 1) / PAGE);
            ulong start = addr & ~(ulong)(PAGE - 1);
            var buf = new byte[pages * PAGE]; var ok = new bool[pages];
            ReadRange(start, buf, ok);
            if (ok.Any(o => !o)) return null;
            return buf.AsSpan((int)(addr - start), len).ToArray();
        }

        /// <summary>Strings for a set of 0x10-byte headers at known offsets of buffers: inline ones directly, heap ones in
        /// one batch.</summary>
        string?[] DecodeStrings(IReadOnlyList<(byte[] Buf, int Off, int MaxInline)> heads)
        {
            var res = new string?[heads.Count];
            var heap = new List<(ulong, int)>(); var heapIdx = new List<int>();
            for (int i = 0; i < heads.Count; i++)
            {
                var (buf, off, max) = heads[i];
                if (off + 0x10 > buf.Length) continue;
                var (inl, ptr, len) = GalaxyStrAt(buf.AsSpan(off), max);
                if (inl != null) res[i] = inl;
                else if (ptr > 0x10000 && ptr < 0x7FFFFFFFFFFF) { heap.Add((ptr, len)); heapIdx.Add(i); }
            }
            var hs = ReadGalaxyStrings(heap);
            for (int k = 0; k < heapIdx.Count; k++) res[heapIdx[k]] = hs[k];
            return res;
        }

        ScriptCatalog? BuildScriptCatalog()
        {
            if (ReadBytes(_exeBase + _scriptStateRva, 0x40) is not { } st) return null;
            int nStruct = BinaryPrimitives.ReadInt32LittleEndian(st.AsSpan(8)), nGlobal = BinaryPrimitives.ReadInt32LittleEndian(st.AsSpan(0x20));
            ulong structArr = BinaryPrimitives.ReadUInt64LittleEndian(st.AsSpan(0x18)), globalArr = BinaryPrimitives.ReadUInt64LittleEndian(st.AsSpan(0x30));
            if (nGlobal is <= 0 or > 500000 || nStruct is < 0 or > 50000 || !IsUserPtr(globalArr)) return null;

            // structs and their members
            var structs = new Dictionary<string, ScriptStruct>(StringComparer.OrdinalIgnoreCase);
            if (nStruct > 0 && IsUserPtr(structArr) && ReadBlock(structArr, nStruct * 0x68) is { } sb)
            {
                var names = DecodeStrings([.. Enumerable.Range(0, nStruct).Select(i => (sb, i * 0x68, 0x40))]);
                for (int i = 0; i < nStruct; i++)
                {
                    int size = BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(i * 0x68 + 0x48));
                    int n = BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(i * 0x68 + 0x50));
                    ulong arr = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(i * 0x68 + 0x60));
                    if (names[i] is not { } sname || n is < 0 or > 20000 || n > 0 && !IsUserPtr(arr)) continue;
                    var members = new List<ScriptMember>();
                    if (n > 0 && ReadBlock(arr, n * 0xA8) is { } mb)
                    {
                        var heads = new List<(byte[], int, int)>();
                        for (int k = 0; k < n; k++) { heads.Add((mb, k * 0xA8, 0x40)); heads.Add((mb, k * 0xA8 + 0x48, 0x40)); }
                        var strs = DecodeStrings(heads);
                        for (int k = 0; k < n; k++)
                            if (strs[k * 2] is { } mn)
                                members.Add(new ScriptMember(mn, BinaryPrimitives.ReadUInt32LittleEndian(mb.AsSpan(k * 0xA8 + 0xA0)), strs[k * 2 + 1] ?? ""));
                    }
                    structs[sname] = new ScriptStruct(sname, size, [.. members]);
                }
            }

            // globals: kind 2 records; the name lives in the interned-name entry the record points at
            if (ReadBlock(globalArr, nGlobal * 0x90) is not { } gb) return null;
            var idx = new List<int>(); var entries = new List<ulong>();
            for (int i = 0; i < nGlobal; i++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(gb.AsSpan(i * 0x90 + 0x14)) != 2) continue;
                ulong e = BinaryPrimitives.ReadUInt64LittleEndian(gb.AsSpan(i * 0x90 + 8));
                if (!IsUserPtr(e)) continue;
                idx.Add(i); entries.Add(e + 0x18);
            }
            const int EntLen = 0x38;
            var eb = new byte[entries.Count * EntLen]; var eok = new bool[entries.Count];
            if (!ReadValues(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries), EntLen, eb, eok)) return null;
            var heads2 = new List<(byte[], int, int)>();
            for (int k = 0; k < idx.Count; k++) { heads2.Add((eb, k * EntLen, 0x28)); heads2.Add((gb, idx[k] * 0x90 + 0x20, 0x10)); }
            var gstr = DecodeStrings(heads2);
            var globals = new List<ScriptGlobal>();
            for (int k = 0; k < idx.Count; k++)
                if (eok[k] && gstr[k * 2] is { } gn)
                    globals.Add(new ScriptGlobal(gn, BinaryPrimitives.ReadUInt32LittleEndian(gb.AsSpan(idx[k] * 0x90 + 0x10)), gstr[k * 2 + 1] ?? ""));
            if (globals.Count == 0) return null;
            var byName = new Dictionary<string, ScriptGlobal>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in globals) byName.TryAdd(g.Name, g);
            return new ScriptCatalog { Globals = [.. globals.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)], ByName = byName, Structs = structs };
        }

        // ── values ───────────────────────────────────────────────────────────

        uint? ScriptLimit() => _scriptLimitRva != 0 && GlobalAt(_scriptLimitRva) is { } v ? (uint)v : null;

        Dictionary<string, ScriptValue> ReadScriptValues(ScriptCatalog cat, string[] paths)
        {
            var res = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
            uint limit = ScriptLimit() ?? 0;
            var resolved = new List<(string Path, uint Addr, string Type, int Size)>();
            foreach (var p in paths)
                if (cat.Resolve(p) is { } r)
                {
                    int size = Math.Min(cat.SizeOf(r.Type), 64);   // arrays / structs: their first bytes, as hex
                    if (r.Addr >= 4 && r.Addr + size <= limit) resolved.Add((p, r.Addr, r.Type, size));
                }
            if (resolved.Count == 0) return res;
            const int Len = 64;
            var vb = new byte[resolved.Count * Len]; var vok = new bool[resolved.Count];
            if (!ReadValues([.. resolved.Select(r => _exeBase + _scriptMemRva + r.Addr)], Len, vb, vok)) return res;

            // unit tags -> live records (tag = u32 at record + 0), only when a unit / unitgroup variable is read
            Dictionary<uint, ulong>? recByTag = null;
            Dictionary<uint, ulong> RecByTag()
            {
                if (recByTag != null) return recByTag;
                recByTag = [];
                var recs = Snapshot.Units.Where(u => u.Record != 0).Select(u => u.Record).Distinct().ToArray();
                var tb = new byte[recs.Length * 4]; var tok = new bool[recs.Length];
                if (recs.Length > 0 && ReadValues(recs, 4, tb, tok))
                    for (int i = 0; i < recs.Length; i++)
                        if (tok[i]) recByTag.TryAdd(BinaryPrimitives.ReadUInt32LittleEndian(tb.AsSpan(i * 4)), recs[i]);
                return recByTag;
            }

            for (int i = 0; i < resolved.Count; i++)
            {
                if (!vok[i]) continue;
                var (path, _, type, size) = resolved[i];
                var v = vb.AsSpan(i * Len, Len);
                uint u32 = BinaryPrimitives.ReadUInt32LittleEndian(v);
                uint[] tags = [];
                string text;
                switch (type)
                {
                    case "4": text = ((int)u32).ToString(); break;
                    case "3": text = ((int)u32 / 4096.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); break;
                    case "?": text = v[0] != 0 ? "true" : "false"; break;
                    case "U": tags = u32 != 0 ? [u32] : []; text = u32 != 0 ? $"unit 0x{u32:X8}" : "no unit"; break;
                    case "u": tags = ReadUnitGroup(u32); text = $"{tags.Length} unit{(tags.Length == 1 ? "" : "s")}"; break;
                    default: text = size <= 4 ? $"0x{u32:X8}" : Convert.ToHexString(v[..Math.Min(size, 16)]) + (size > 16 ? "..." : ""); break;
                }
                ulong[] records = [];
                if (tags.Length > 0)
                {
                    var map = RecByTag();
                    records = [.. tags.Select(t => map.TryGetValue(t, out var r) ? r : 0).Where(r => r != 0)];
                }
                res[path] = new ScriptValue(text, tags, records);
            }
            return res;
        }

        /// <summary>A unitgroup handle's unit tags (empty when the handle or object doesn't check out).</summary>
        uint[] ReadUnitGroup(uint handle)
        {
            if (handle == 0 || _scriptHandlesRva == 0) return [];
            ulong table = U64At(_scriptHandlesRva);
            if (table == 0) return [];
            ulong obj = ReadU64(table + (handle & 0xFFFF) * 16 + 8);
            if (!IsUserPtr(obj) || ReadBytes(obj + 0x18, 0x18) is not { } ob) return [];
            int n = BinaryPrimitives.ReadInt32LittleEndian(ob);
            ulong arr = BinaryPrimitives.ReadUInt64LittleEndian(ob.AsSpan(0x10));
            if (n <= 0 || n > 4096 || !IsUserPtr(arr) || ReadBytes(arr, n * 4) is not { } tb) return [];
            return [.. Enumerable.Range(0, n).Select(k => BinaryPrimitives.ReadUInt32LittleEndian(tb.AsSpan(k * 4))).Where(t => t != 0)];
        }
    }
}
