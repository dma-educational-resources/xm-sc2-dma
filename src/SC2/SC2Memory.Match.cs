using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Match-wide state: game clock, map name and terrain (5.0.x addresses in brackets, all found by code signature).
    ///   timing block   sc2rtwp's TimingData, an encoded heap pointer decoded like the game's global-time-scale setter
    ///                  [exe+0x680300]: lo = u32[g1] ^ u32[k1], hi = u32[g2] ^ u32[k2] ^ imm32.
    ///                  +0x64 game time (seconds, fixed 4096), +0x7C game speed (fixed 4096, 1.4 = Faster),
    ///                  +0x84 speed index 0..4 (Slower .. Faster)
    ///   game info      plain pointer read by the GameMapName native [exe+0x26BE7E0] through its getter [exe+0x673770]:
    ///                  +0x2C0 map name, +0x318 map description (string objects: u32 len &lt;&lt; 2 @+8, flags @+0xC with
    ///                  bit 1 = chars on the heap at *(+0x10), else inline at +0x10)
    ///   height maps    read by the WorldHeight native [exe+0x26BC260]: a static table [exe+0x3B67EB0] or the pointer
    ///                  [exe+0x3B67EA0] when set; one map per height type, 0x20414 apart (type 0 = ground).
    ///                  int minX, minY, maxX, maxY (vertex bounds, cells = max - min - 1), then int16 heights [257 * 257]
    ///                  row-major from the bottom-left vertex, height = value / 256 map units.
    /// </summary>
    public sealed partial class SC2Memory
    {
        const string TimingPattern = "8B 05 ? ? ? ? 33 05 ? ? ? ? 89 44 24 10 8B 05 ? ? ? ? 33 05 ? ? ? ? 35 ? ? ? ? 89 44 24 14 48 8B 44 24 10 89 88 A0 00 00 00 C3";
        static readonly CodeSig[] TimingSigs =
        [
            new("timing g1", TimingPattern, 2), new("timing k1", TimingPattern, 8),
            new("timing g2", TimingPattern, 18), new("timing k2", TimingPattern, 24),
        ];
        const int TimingKeyOff = 29;   // imm32 of the "xor eax, imm32"
        const int TimingGameTimeOff = 0x64, TimingSpeedOff = 0x7C, TimingSpeedIndexOff = 0x84;

        // GameMapName native: "call getter; test rax,rax; jne; mov eax,1; add rsp,38h; ret; mov [rsp+40h],rbx; lea rbx,[rax+2C0h]"
        static readonly CodeSig GameInfoGetterSig =
            new("game info getter", "48 83 EC 38 E8 ? ? ? ? 48 85 C0 75 ? B8 01 00 00 00 48 83 C4 38 C3 48 89 5C 24 40 48 8D 98 C0 02 00 00", 5);
        const int GameInfoMapNameOff = 0x2C0, GameInfoMapDescOff = 0x318, GameInfoMapPathOff = 0x10;

        // WorldHeight native: "mov rax,[rip+ptr]; lea r9,[rip+table]; ... cmovne r9,rax; sar r8d,0Ch; imul rcx,rsi,stride"
        const string HeightPattern = "48 8B 05 ? ? ? ? 4C 8D 0D ? ? ? ? 44 8B 85 ? ? ? ? 48 85 C0 8B BD ? ? ? ? 4C 0F 45 C8 41 C1 F8 0C 48 69 CE ? ? ? ?";
        static readonly CodeSig HeightPtrSig   = new("height map pointer", HeightPattern, 3);
        static readonly CodeSig HeightTableSig = new("height map table", HeightPattern, 10);
        const int HeightRowStride = 257, HeightDataOff = 0x10;

        // Pathing: the PathingType native [exe+0x26BA700] reads the pathing object from a global ("mov rsi,[rip+X]"), and
        // its helper [exe+0x8C73F0] shows the layout: mesh = *(obj + 0x4A8), triangle class byte at mesh + i*16 + 0x2001C,
        // pathing type = byte obj[0x47A + class]. The mesh (navmesh): u32 vertexCount @+8, u32 triangleCount @+0xC,
        // vertices u32 @+0x10 (x = bits 0..11, y = bits 12..23, 1/16 map unit), triangles 16 bytes @+0x20010
        // (three u32 whose low 15 bits are vertex indices, class byte @+0xC; triangle 0 unused).
        // Pathing types: 0 none (outside the playable area, holes), 1 ground, 2 blocked (structures, minerals, doodads), 3 cliff.
        static readonly CodeSig PathObjSig = new("pathing object", "48 89 70 E0 48 8B 35 ? ? ? ? 4C 89 70 D8 48 8B CE 4C 89 78 D0 E8", 7);
        const string PathFieldsPattern = "48 89 5C 24 08 57 48 83 EC 20 48 8B F9 39 91 ? ? ? ? 75 ? 48 8B 99 ? ? ? ? 49 8B D0 48 8B CB E8 ? ? ? ? 8B C8 48 03 C9 0F B6 84 CB ? ? ? ? 0F B6 84 38 ? ? ? ?";
        ulong _pathObjRva;
        uint  _meshOff, _triClassOff, _classMapOff;
        int   _pathVersion;
        /// <summary>The ground navmesh of the current map, null until read.</summary>
        public PathingMesh? Pathing { get; private set; }

        ulong[] _timingRvas = [];
        uint    _timingKey;
        ulong   _gameInfoRva, _heightPtrRva, _heightTableRva;

        /// <summary>Game time in seconds, -1 when unknown.</summary>
        public float  GameTime       { get; private set; } = -1f;
        /// <summary>0 Slower .. 4 Faster, -1 when unknown.</summary>
        public int    GameSpeedIndex { get; private set; } = -1;
        /// <summary>The clock hasn't moved for a while (paused, or the match ended).</summary>
        public bool   GamePaused     { get; private set; }
        public string MapName        { get; private set; } = "";
        public string MapDescription { get; private set; } = "";
        /// <summary>The map file the game loaded (a Battle.net cache file for online maps).</summary>
        public string MapPath        { get; private set; } = "";

        /// <summary>Where the map came from, from its path: "Battle.net" (ladder / arcade / custom online), "Campaign",
        /// "Local file", or "" when unknown.</summary>
        public string MapSource =>
            MapPath.Length == 0 ? ""
            : MapPath.Contains(@"\Battle.net\Cache\", StringComparison.OrdinalIgnoreCase) || MapPath.Contains("/Battle.net/Cache/", StringComparison.OrdinalIgnoreCase) ? "Battle.net"
            : MapPath.Contains("Campaign", StringComparison.OrdinalIgnoreCase) ? "Campaign"
            : "Local file";
        /// <summary>Ground height map of the current map, null until read.</summary>
        public TerrainMap? Terrain   { get; private set; }

        public static readonly string[] SpeedNames = ["Slower", "Slow", "Normal", "Fast", "Faster"];

        /// <summary>The in-game timer, as "m:ss" or "h:mm:ss". The stored game time already counts the seconds the game's
        /// timer shows (22.4 game loops each), so it is shown as is.</summary>
        public static string FormatClock(float gameTime)
        {
            if (gameTime < 0f) return "-";
            var t = TimeSpan.FromSeconds(Math.Floor(gameTime));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
        public string GameSpeedName => GameSpeedIndex is >= 0 and < 5 ? SpeedNames[GameSpeedIndex] : "";

        float    _lastClock = -1f;
        DateTime _clockMoved = DateTime.MinValue, _nextMapInfo = DateTime.MinValue, _nextTerrain = DateTime.MinValue;
        int      _terrainVersion;

        /// <summary>The unique match's bytes at <paramref name="offset"/> as a u32 (an immediate), or null.</summary>
        static uint? SigImm32(List<(uint Rva, byte[] Data)> code, string pattern, int offset)
        {
            var hits = FindPattern(code, pattern, 2);
            if (hits.Count != 1) return null;
            uint at = hits[0] + (uint)offset;
            foreach (var (rva, data) in code)
                if (at >= rva && at + 4 <= rva + (uint)data.Length)
                    return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)(at - rva), 4));
            return null;
        }

        /// <summary>The target of the rel32 at a code RVA (ending its instruction), or null.</summary>
        static ulong? RelTarget(List<(uint Rva, byte[] Data)> code, uint at)
        {
            foreach (var (rva, data) in code)
                if (at >= rva && at + 4 <= rva + (uint)data.Length)
                    return (ulong)((long)at + 4 + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)(at - rva), 4)));
            return null;
        }

        /// <summary>"mov rax, [rip+X]" at a code RVA -> X.</summary>
        static ulong? RipLoadTarget(List<(uint Rva, byte[] Data)> code, ulong fn)
        {
            foreach (var (rva, data) in code)
            {
                if (fn < rva || fn + 7 > rva + (ulong)data.Length) continue;
                var s = data.AsSpan((int)(fn - rva), 7);
                if (s[0] != 0x48 || s[1] != 0x8B || s[2] != 0x05) return null;
                return (ulong)((long)fn + 7 + BinaryPrimitives.ReadInt32LittleEndian(s[3..]));
            }
            return null;
        }

        string ResolveMatch(List<(uint Rva, byte[] Data)> code)
        {
            var parts = new List<string>();
            var trvas = TimingSigs.Select(s => code.Count > 0 ? ResolveSig(code, s, out _) : null).ToArray();
            uint? key = code.Count > 0 ? SigImm32(code, TimingPattern, TimingKeyOff) : null;
            _timingRvas = trvas.All(r => r is not null) && key is not null ? trvas.Select(r => r!.Value).ToArray() : [];
            _timingKey  = key ?? 0;
            parts.Add($"clock {(_timingRvas.Length == 4 ? "sig" : "missed")}");

            // the getter is shared by several natives with this shape: every match must agree on it
            var getters = code.Count > 0 ? FindPattern(code, GameInfoGetterSig.Pattern).Select(h => RelTarget(code, h + (uint)GameInfoGetterSig.DispOffset)).Distinct().ToList() : [];
            _gameInfoRva = getters is [{ } getter] && RipLoadTarget(code, getter) is { } gi ? gi : 0;
            parts.Add($"map info {(_gameInfoRva != 0 ? "sig" : "missed")}");

            _heightPtrRva   = code.Count > 0 && ResolveSig(code, HeightPtrSig, out _) is { } hp ? hp : 0;
            _heightTableRva = code.Count > 0 && ResolveSig(code, HeightTableSig, out _) is { } ht ? ht : 0;
            parts.Add($"terrain {(_heightTableRva != 0 ? "sig" : "missed")}");

            _pathObjRva  = code.Count > 0 && ResolveSig(code, PathObjSig, out _) is { } pr ? pr : 0;
            _meshOff     = code.Count > 0 ? SigImm32(code, PathFieldsPattern, 24) ?? 0 : 0;
            _triClassOff = code.Count > 0 ? SigImm32(code, PathFieldsPattern, 48) ?? 0 : 0;
            _classMapOff = code.Count > 0 ? SigImm32(code, PathFieldsPattern, 56) ?? 0 : 0;
            bool pathOk = _pathObjRva != 0 && _meshOff is > 0 and < 0x10000 && _triClassOff is > 0xC and < 0x100000 && _classMapOff is > 0 and < 0x10000;
            if (!pathOk) _pathObjRva = 0;
            parts.Add($"pathing {(pathOk ? "sig" : "missed")}");
            foreach (var p in parts.Where(p => p.EndsWith("missed"))) XMLogging.WriteLine($"[Match] signature '{p}'");
            return string.Join(", ", parts);
        }

        /// <summary>A new mission: forget the previous map.</summary>
        void ResetMatch()
        {
            GameTime = -1f; GameSpeedIndex = -1; GamePaused = false; _lastClock = -1f;
            MapName = ""; MapDescription = ""; MapPath = ""; Terrain = null; Pathing = null;
            Creep = null; _creepRaw = null;
            _vision = new VisionMap?[16];
            // the cameras too, or the last match's boxes stay on the radar after it ends
            LocalCameraView = null; _camTargets = new System.Numerics.Vector2?[16]; _camDistances = new float[16];
            _camController = null;
            _nextMapInfo = _nextTerrain = DateTime.MinValue;
            ResetScript();
            ResetEventFeed();
        }

        /// <summary>Clock every call (vitals cadence), map info every few seconds, terrain on a slow cycle.</summary>
        void ReadMatch()
        {
            ReadClock();
            var now = DateTime.UtcNow;
            if (now >= _nextMapInfo) { _nextMapInfo = now.AddSeconds(MapName.Length > 0 ? 10 : 2); ReadMapInfo(); }
            if (now >= _nextTerrain) { _nextTerrain = now.AddSeconds(Terrain != null ? 5 : 3); ReadTerrain(); ReadPathing(); }
        }

        /// <summary>The navmesh (re-read on the terrain cycle: placing or losing a structure changes it).</summary>
        void ReadPathing()
        {
            if (_pathObjRva == 0) return;
            ulong obj = U64At(_pathObjRva);
            if (!IsUserPtr(obj)) return;
            ulong mesh = ReadU64(obj + _meshOff);
            var hdr = IsUserPtr(mesh) ? ReadBytes(mesh, 0x10) : null;
            var map = ReadBytes(obj + _classMapOff, 256);
            if (hdr == null || map == null) return;
            int nv = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(8)), nt = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(12));
            if (nv is < 3 or > 0x8000 || nt is < 2 or > 0x10000) return;
            ulong triBase = mesh + _triClassOff - 0xC;
            var vb = ReadBytes(mesh + 0x10, nv * 4);
            var tb = ReadBytes(triBase, nt * 16);
            if (vb == null || tb == null) return;
            var verts = new Vector2[nv];
            for (int i = 0; i < nv; i++)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(vb.AsSpan(i * 4));
                verts[i] = new Vector2((v & 0xFFF) / 16f, ((v >> 12) & 0xFFF) / 16f);
            }
            var tris = new PathTriangle[nt - 1];
            for (int t = 1; t < nt; t++)
            {
                var s = tb.AsSpan(t * 16, 16);
                int a = BinaryPrimitives.ReadUInt16LittleEndian(s) & 0x7FFF, b = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]) & 0x7FFF,
                    c = BinaryPrimitives.ReadUInt16LittleEndian(s[8..]) & 0x7FFF;
                if (a >= nv || b >= nv || c >= nv) return;   // not a mesh after all (stale pointer, mid-rebuild)
                tris[t - 1] = new PathTriangle(a, b, c, map[s[12]]);
            }
            var old = Pathing;
            if (old != null && old.Vertices.AsSpan().SequenceEqual(verts) && old.Triangles.AsSpan().SequenceEqual(tris)) return;
            Pathing = new PathingMesh(verts, tris, ++_pathVersion);
        }

        void ReadClock()
        {
            if (_timingRvas.Length != 4) return;
            ulong p = ((ulong)(U32At(_timingRvas[2]) ^ U32At(_timingRvas[3]) ^ _timingKey) << 32) | (U32At(_timingRvas[0]) ^ U32At(_timingRvas[1]));
            var b = IsUserPtr(p) ? ReadBytes(p, 0x90) : null;
            if (b == null) { GameTime = -1f; GameSpeedIndex = -1; return; }
            uint speed = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(TimingSpeedOff));
            uint idx   = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(TimingSpeedIndexOff));
            if (speed is < 1000 or > 20000 || idx > 4) { GameTime = -1f; GameSpeedIndex = -1; return; }   // not the timing block
            float t = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(TimingGameTimeOff)) / 4096f;
            var now = DateTime.UtcNow;
            if (t != _lastClock) { _lastClock = t; _clockMoved = now; }
            GamePaused     = (now - _clockMoved).TotalSeconds > 1.0;
            GameTime       = t;
            GameSpeedIndex = (int)idx;
        }

        string ReadGameString(ulong obj)
        {
            var b = ReadBytes(obj, 0x18);
            if (b == null) return "";
            int len = (int)(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)) >> 2);
            if (len is <= 0 or > 1024) return "";
            bool heap = (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0xC)) & 2) != 0;
            ulong at = heap ? BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(0x10)) : obj + 0x10;
            if (heap && !IsUserPtr(at) && at < 0x10000) return "";
            var s = ReadBytes(at, len);
            return s == null ? "" : Encoding.UTF8.GetString(s).TrimEnd('\0');
        }

        void ReadMapInfo()
        {
            if (_gameInfoRva == 0) return;
            ulong gi = U64At(_gameInfoRva);
            if (!IsUserPtr(gi)) { MapName = ""; MapDescription = ""; MapPath = ""; return; }
            MapName        = ReadGameString(gi + GameInfoMapNameOff);
            MapDescription = ReadGameString(gi + GameInfoMapDescOff);
            MapPath        = ReadGameString(gi + GameInfoMapPathOff);
        }

        void ReadTerrain()
        {
            if (_heightTableRva == 0) return;
            ulong ptr = _heightPtrRva != 0 ? U64At(_heightPtrRva) : 0;
            ulong tbl = IsUserPtr(ptr) ? ptr : _exeBase + _heightTableRva;
            const int len = HeightDataOff + HeightRowStride * HeightRowStride * 2;
            var b = ReadBytes(tbl, len);   // type 0: ground
            if (b is not { Length: len }) return;
            int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));
            int w = I(8) - I(0) - 1, h = I(0xC) - I(4) - 1;
            if (w is < 8 or > 256 || h is < 8 or > 256) return;
            var raw = MemoryMarshal.Cast<byte, short>(b.AsSpan(HeightDataOff));
            var heights = new float[(w + 1) * (h + 1)];
            for (int y = 0; y <= h; y++)
                for (int x = 0; x <= w; x++)
                    heights[y * (w + 1) + x] = raw[y * HeightRowStride + x] / 256f;
            // only publish a new version when something changed (the texture is rebuilt per version)
            var old = Terrain;
            if (old != null && old.W == w && old.H == h && old.Heights.AsSpan().SequenceEqual(heights)) return;
            Terrain = new TerrainMap(w, h, heights, ++_terrainVersion);
        }
    }

    /// <summary>One navmesh triangle: vertex indices and its pathing type (0 none, 1 ground, 2 blocked, 3 cliff).</summary>
    public readonly record struct PathTriangle(int A, int B, int C, byte Type);

    /// <summary>The ground navmesh in map units.</summary>
    public sealed record PathingMesh(Vector2[] Vertices, PathTriangle[] Triangles, int Version)
    {
        (Vector2 Min, Vector2 Max)? _playable;

        /// <summary>The playable area: the bounding box of every triangle that isn't type 0 (outside the playable area) -
        /// the part of the map the minimap shows.</summary>
        public (Vector2 Min, Vector2 Max) Playable => _playable ??= Bounds();

        (Vector2, Vector2) Bounds()
        {
            var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
            foreach (var t in Triangles)
            {
                if (t.Type == 0) continue;
                foreach (int i in (ReadOnlySpan<int>)[t.A, t.B, t.C])
                    if (i < Vertices.Length) { min = Vector2.Min(min, Vertices[i]); max = Vector2.Max(max, Vertices[i]); }
            }
            return min.X <= max.X ? (min, max) : (Vector2.Zero, Vector2.Zero);
        }
    }

    /// <summary>Ground height per vertex: (W + 1) x (H + 1) values, row-major from the bottom-left (map 0,0).</summary>
    public sealed record TerrainMap(int W, int H, float[] Heights, int Version)
    {
        public float At(int x, int y) => Heights[Math.Clamp(y, 0, H) * (W + 1) + Math.Clamp(x, 0, W)];

        /// <summary>Bilinear ground height at a map position.</summary>
        public float Sample(float x, float y)
        {
            x = Math.Clamp(x, 0f, W); y = Math.Clamp(y, 0f, H);
            int x0 = Math.Min((int)x, W - 1), y0 = Math.Min((int)y, H - 1);
            float fx = x - x0, fy = y - y0;
            float a = At(x0, y0), b = At(x0 + 1, y0), c = At(x0, y0 + 1), d = At(x0 + 1, y0 + 1);
            return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
        }

        const int Block = 8;
        float[]? _blockMax;

        /// <summary>Highest ground in the square [x0,x1]x[y0,y1] (conservative: whole 8x8 blocks).</summary>
        public float MaxIn(float x0, float y0, float x1, float y1)
        {
            int bw = W / Block + 1, bh = H / Block + 1;
            if (_blockMax == null)
            {
                var m = new float[bw * bh];
                Array.Fill(m, float.MinValue);
                for (int y = 0; y <= H; y++)
                    for (int x = 0; x <= W; x++)
                    {
                        int i = Math.Min(y / Block, bh - 1) * bw + Math.Min(x / Block, bw - 1);
                        m[i] = MathF.Max(m[i], Heights[y * (W + 1) + x]);
                    }
                _blockMax = m;
            }
            int bx0 = Math.Clamp((int)x0 / Block, 0, bw - 1), bx1 = Math.Clamp((int)x1 / Block, 0, bw - 1);
            int by0 = Math.Clamp((int)y0 / Block, 0, bh - 1), by1 = Math.Clamp((int)y1 / Block, 0, bh - 1);
            float max = float.MinValue;
            for (int by = by0; by <= by1; by++)
                for (int bx = bx0; bx <= bx1; bx++)
                    max = MathF.Max(max, _blockMax[by * bw + bx]);
            return max;
        }
    }
}
