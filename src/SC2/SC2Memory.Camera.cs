using System.Numerics;
using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    /// <summary>
    /// The local client camera (5.0.x addresses in brackets, all by code signature). CameraSetValue -> [exe+0xAF8E40]:
    ///   controller = *(*(exe+0x401F728) + 0xC18), encoded @+0xB480: lo = u32 - u32[exe+0x3A7DDFC] - 0x55AE9885,
    ///   hi = u32 @+0xB484 ^ u32[exe+0x3A7F4F8]. [exe+0xAFCD40] then moves value i (c_cameraValue*: 0 field of view,
    ///   4 distance, 5 pitch, 6 yaw, ...) in its 36-byte slot at controller + 0xAC + 36 i: target @+0, current @+4,
    ///   start @+8, each a float split in two encoded u16 halves: hi = ~(e_hi - K[e_lo &amp; 0xFFF]),
    ///   lo = ~(e_lo - K[hi &amp; 0xFFF]), K = the low u16 of each u32 in the key table [exe+0x3A7D2E0].
    /// Client-side only: nothing here is synced to the other players. Every player's camera target is synced, though
    /// (see CameraTarget below), which is how the radar shows where the others are looking.
    /// </summary>
    public sealed partial class SC2Memory
    {
        // Every player's camera target (CameraGetTarget [exe+0x96E720], synced, so other players' too): encoded at
        // player object + 0x1000 (two fixed 4096 ints), decoded by one of 16 routines picked by the player-slot selector
        // (case table [exe+0x96F160], each case "lea rcx,[rbp+210h]; call decoder"). The offset sits in obfuscated code.
        const string CamTargetSwitchPattern = "48 8D 3D ? ? ? ? 83 F8 0F 0F 87 ? ? ? ? 8B 8C 87 ? ? ? ? 48 03 CF";
        const uint CamTargetOff = 0x1000;
        uint _camTargetCasesRva;
        ulong _camTargetDecoder;
        Vector2?[] _camTargets = new Vector2?[16];
        float[] _camDistances = new float[16];
        DateTime _nextCamTargets;

        /// <summary>Where a player's camera is looking (map units), null when unknown.</summary>
        public Vector2? CameraTarget(int player) => player is >= 0 and < 16 ? _camTargets[player] : null;

        /// <summary>A player's camera zoom (distance from target, map units), synced as a plain fixed 4096 int at player
        /// object + 0x1008 (next to the encoded target); 34 when unknown.</summary>
        public float CameraDistance(int player) => player is >= 0 and < 16 && _camDistances[player] > 0f ? _camDistances[player] : 34f;
        const uint CamDistanceOff = 0x1008;

        /// <summary>The local camera as the client has it, about 30 times a second: position (controller + 0x7A4, floats - it
        /// follows every move; the target at +0x48 only changes on big jumps), distance, pitch and field of view (camera
        /// values 4, 5, 0). Null when there's no game camera.</summary>
        public (Vector2 Target, float Distance, float Pitch, float Fov)? LocalCameraView { get; private set; }
        const ulong LocalCameraPosOff = 0x7A4;
        DateTime _nextLocalView;

        // The realtime worker reads the cameras in the same batch as the unit positions, as the EFT radar reads its view
        // matrix with the players: QueueCameras asks for the ranges (the local camera's, then every player's camera
        // target), ReadActorFrames reads them along (_rideAlong / _rideAlongRead), ReadCameras decodes them.
        (ulong Addr, int Size)[] _rideAlong = [];
        byte[]?[]? _rideAlongRead;
        int _rideLocal;                 // ranges of _rideAlong that are the local camera's
        CamTargetPlan? _rideTargets;    // the camera targets' ranges, after those

        /// <summary>Realtime worker, before the positions read: the cameras' ranges, when they're due (about 30 times a
        /// second) - the local controller position and value slots 0..5, and every player's camera target.</summary>
        void QueueCameras()
        {
            _rideAlong = []; _rideAlongRead = null; _rideLocal = 0; _rideTargets = null;
            var ranges = new List<(ulong Addr, int Size)>();
            if (HasCamera && DateTime.UtcNow >= _nextLocalView)
            {
                _nextLocalView = DateTime.UtcNow.AddMilliseconds(33);
                ulong c = CameraController();
                if (c == 0 || CameraKeys() is null) LocalCameraView = null;
                else ranges.AddRange([(c + LocalCameraPosOff, 8), (c + _camSlotsOff, CameraSlotSize * 6)]);
            }
            _rideLocal = ranges.Count;
            _rideTargets = QueueCameraTargets(ranges);
            _rideAlong = [.. ranges];
        }

        /// <summary>Realtime worker, after the positions read: decode the camera ranges it read along (or read them now,
        /// when there were no positions to read).</summary>
        void ReadCameras()
        {
            if (_rideAlong.Length == 0) return;
            var r = _rideAlongRead ?? ReadRanges(_rideAlong);
            if (_rideLocal > 0) LocalCameraView = DecodeLocalView(r[.._rideLocal]);
            if (_rideTargets is { } plan) DecodeCameraTargets(plan, _rideAlong, r);
            _rideAlong = []; _rideAlongRead = null; _rideLocal = 0; _rideTargets = null;
        }

        (Vector2 Target, float Distance, float Pitch, float Fov)? DecodeLocalView(byte[]?[] r)
        {
            if (CameraKeys() is not { } k || r is not [{ } tb, { } sb]) return null;
            float Current(int i) => DecodeCam(sb.AsSpan(CameraSlotSize * i + 4), k);
            return (new Vector2(BitConverter.ToSingle(tb, 0), BitConverter.ToSingle(tb, 4)), Current(CameraValueDistance), Current(5), Current(0));
        }

        // "mov rax,[rip+cam]; mov rcx,[rax+C18h]; mov eax,[rcx+B480h]; sub eax,[rip+k1]; sub eax,imm;
        //  mov [rsp+40h],eax; mov eax,[rcx+B484h]; xor eax,[rip+k2]" - inlined wherever the controller is used
        const string CameraControllerPattern = "48 8B 05 ? ? ? ? 48 8B 88 ? ? ? ? 8B 81 ? ? ? ? 2B 05 ? ? ? ? 2D ? ? ? ? 89 44 24 ? 8B 81 ? ? ? ? 33 05 ? ? ? ?";
        // the value setter: "lea r10,[rip+keys]; ...; lea r9,[rdi+rdi*8]; movzx edx,word [rcx+r9*4+cur_hi]; movzx r8d,word [rcx+r9*4+cur_lo]"
        const string CameraSetterPattern = "4C 8D 15 ? ? ? ? 48 8B F1 0F 57 C0 0F 28 CB F3 0F 10 5C 24 ? 4C 8D 0C FF 42 0F B7 94 89 ? ? ? ? 46 0F B7 84 89 ? ? ? ?";
        const int CameraSlotSize = 36;
        public const int CameraValueDistance = 4;

        ulong _camGlobalRva, _camKey1Rva, _camKey2Rva, _camKeyTableRva;
        uint  _camOuterOff, _camLoOff, _camHiOff, _camConst, _camSlotsOff;
        ushort[]? _camKeys;
        string _camDiag = "";

        public bool HasCamera => _camGlobalRva != 0 && _camSlotsOff != 0;

        string ResolveCamera(List<(uint Rva, byte[] Data)> code)
        {
            _camGlobalRva = _camKey1Rva = _camKey2Rva = _camKeyTableRva = 0;
            _camSlotsOff = 0;
            _camKeys = null;
            if (code.Count > 0)
            {
                ulong g  = ResolveSigAgree(code, new CodeSig("camera global", CameraControllerPattern, 3)) ?? 0;
                ulong k1 = ResolveSigAgree(code, new CodeSig("camera key 1", CameraControllerPattern, 22)) ?? 0;
                ulong k2 = ResolveSigAgree(code, new CodeSig("camera key 2", CameraControllerPattern, 43)) ?? 0;
                uint? outer = SigImm32Agree(code, CameraControllerPattern, 10), lo = SigImm32Agree(code, CameraControllerPattern, 16);
                uint? hi = SigImm32Agree(code, CameraControllerPattern, 37), c = SigImm32Agree(code, CameraControllerPattern, 27);
                ulong keys = ResolveSigAgree(code, new CodeSig("camera key table", CameraSetterPattern, 3)) ?? 0;
                uint? cur = SigImm32Agree(code, CameraSetterPattern, 40);
                _camDiag = $"hits {FindPattern(code, CameraControllerPattern, 64).Count}/{FindPattern(code, CameraSetterPattern, 8).Count}, g {g:X} k1 {k1:X} k2 {k2:X} keys {keys:X} outer {outer:X} lo {lo:X} hi {hi:X} c {c:X} cur {cur:X}";
                if (g != 0 && k1 != 0 && k2 != 0 && keys != 0 && outer is { } o && lo is { } l && hi == l + 4 && c is { } cc && cur is > 4 and < 0x1000)
                {
                    (_camGlobalRva, _camKey1Rva, _camKey2Rva, _camKeyTableRva) = (g, k1, k2, keys);
                    (_camOuterOff, _camLoOff, _camHiOff, _camConst) = (o, l, l + 4, cc);
                    _camSlotsOff = cur.Value - 4;   // the setter reads "current", one float into the slot
                }
            }
            if (!HasCamera) XMLogging.WriteLine($"[Camera] signature 'camera controller' missed ({_camDiag}) - zoom unavailable");

            // camera targets: of the 16-way switches on the selector, the one whose cases decode into [rbp+210h]
            _camTargetCasesRva = 0; _camTargetDecoder = 0;
            _camTargets = new Vector2?[16];
            if (code.Count > 0)
            {
                var tables = FindPattern(code, CamTargetSwitchPattern, 64)
                    .Select(h => CodeU32(code, h + 19) ?? 0)
                    .Where(t => t != 0 && CodeU32(code, t) is { } c0 && CodeBytes(code, c0, 8) is [0x48, 0x8D, 0x8D, 0x10, 0x02, 0x00, 0x00, 0xE8])
                    .Distinct().ToList();
                if (tables is [var t]) _camTargetCasesRva = t;
            }
            if (_camTargetCasesRva == 0) XMLogging.WriteLine("[Camera] signature 'camera targets' missed - no player cameras");
            return $"camera {(HasCamera ? "sig" : "missed")}, cameras {(_camTargetCasesRva != 0 ? "sig" : "missed")}";
        }

        /// <summary>The camera targets' share of a batched read: per player, where its ranges start and how many.</summary>
        sealed record CamTargetPlan((int Player, ulong Obj, int First, int Count)[] Players);

        // Per player, the memory the decoder read over DMA itself last time (outside the encoded field - its keys): read
        // in the batch from then on, so a decode needs no read of its own. Refreshed by every decode.
        readonly (ulong Obj, (ulong Addr, int Size)[] Reads)[] _camTargetReads = new (ulong, (ulong, int)[])[16];
        const int CamFieldLen = (int)(CamDistanceOff - CamTargetOff) + 0x18;

        /// <summary>Add every player's camera target ranges to a batched read (realtime worker, about 30 times a second -
        /// the targets are synced at the game's tick rate); null when not due or not resolved.</summary>
        CamTargetPlan? QueueCameraTargets(List<(ulong Addr, int Size)> ranges)
        {
            if (_camTargetCasesRva == 0 || DateTime.UtcNow < _nextCamTargets) return null;
            _nextCamTargets = DateTime.UtcNow.AddMilliseconds(33);
            if (_camTargetDecoder == 0)
            {
                // the selector is fixed at start-up: pick its case once
                uint sel = _slotSelectorRva != 0 ? U32At(_slotSelectorRva) : 99;
                uint caseRva = sel <= 15 ? U32At(_camTargetCasesRva + sel * 4) : 0;
                if (caseRva == 0 || ReadBytes(_exeBase + caseRva, 12) is not [0x48, 0x8D, 0x8D, _, _, _, _, 0xE8, _, _, _, _] cb) return null;
                _camTargetDecoder = (ulong)((long)caseRva + 12 + BinaryPrimitives.ReadInt32LittleEndian(cb.AsSpan(8)));
            }
            var players = new List<(int, ulong, int, int)>();
            var objs = _playerObjs;
            for (int p = 1; p < 16; p++)
            {
                ulong obj = objs[p];
                if (obj == 0) continue;
                int first = ranges.Count;
                ranges.Add((obj + CamTargetOff, CamFieldLen));
                if (_camTargetReads[p] is { Reads: { } learned } l && l.Obj == obj) ranges.AddRange(learned);
                players.Add((p, obj, first, ranges.Count - first));
            }
            return new CamTargetPlan([.. players]);
        }

        /// <summary>Decode every player's camera target from the ranges read (the decoder, run in MiniEmu, takes its input
        /// and keys from there).</summary>
        void DecodeCameraTargets(CamTargetPlan plan, ReadOnlySpan<(ulong Addr, int Size)> ranges, byte[]?[] read)
        {
            var targets = new Vector2?[16];
            var distances = new float[16];
            float w = Terrain?.W ?? 512f, h = Terrain?.H ?? 512f;
            foreach (var (p, obj, first, count) in plan.Players)
            {
                if (read[first] is not { } field) continue;
                try
                {
                    var emu = new MiniEmu(this, null) { LiveReads = [] };
                    emu.Preload(obj + CamTargetOff, field);
                    for (int i = first + 1; i < first + count; i++)
                        if (read[i] is { } d) emu.Preload(ranges[i].Addr, d);
                    bool ran = emu.Call(_camTargetDecoder, MiniEmu.OutBuf, obj + CamTargetOff);
                    // what it read itself goes in the next batch, with what it was given (at most 32 ranges: should the
                    // addresses keep changing, start over from this decode's)
                    if (emu.LiveReads.Count > 0)
                    {
                        var learned = ranges.Slice(first + 1, count - 1).ToArray().Concat(emu.LiveReads).Distinct().ToArray();
                        _camTargetReads[p] = (obj, learned.Length <= 32 ? learned : [.. emu.LiveReads.Distinct()]);
                    }
                    if (!ran) continue;
                    ulong v = emu.ReadOut(0, 8);
                    float x = (int)(uint)v / 4096f, y = (int)(v >> 32) / 4096f;
                    if (x > 0f && y > 0f && x <= w && y <= h) targets[p] = new Vector2(x, y);
                    if (BinaryPrimitives.ReadInt32LittleEndian(field.AsSpan((int)(CamDistanceOff - CamTargetOff))) / 4096f is > 5f and < 300f and var dist)
                        distances[p] = dist;
                }
                catch { }
            }
            _camTargets = targets;
            _camDistances = distances;
        }

        /// <summary>Every player's camera target in a read of its own (the research tools; the radar reads them along
        /// with the positions).</summary>
        void ReadCameraTargets()
        {
            var ranges = new List<(ulong Addr, int Size)>();
            if (QueueCameraTargets(ranges) is not { } plan) return;
            DecodeCameraTargets(plan, CollectionsMarshal.AsSpan(ranges), ReadRanges(CollectionsMarshal.AsSpan(ranges)));
        }

        // the decoded controller, kept for a second: it only changes with a new mission (the realtime worker reads the camera
        // about 30 times a second, Camera Zoom 20); a pair so both threads see a consistent value and time
        volatile Tuple<ulong, long>? _camController;

        ulong CameraController()
        {
            if (!HasCamera) return 0;
            if (_camController is { } cc && Environment.TickCount64 < cc.Item2) return cc.Item1;
            ulong c = DecodeCameraController();
            _camController = c != 0 ? Tuple.Create(c, Environment.TickCount64 + 1000) : null;
            return c;
        }

        ulong DecodeCameraController()
        {
            ulong g = U64At(_camGlobalRva);
            ulong p = IsUserPtr(g) ? ReadU64(g + _camOuterOff) : 0;
            if (!IsUserPtr(p) || ReadBytes(p + _camLoOff, 8) is not { Length: 8 } b) return 0;
            uint lo = BinaryPrimitives.ReadUInt32LittleEndian(b) - U32At(_camKey1Rva) - _camConst;
            uint hi = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)) ^ U32At(_camKey2Rva);
            ulong c = ((ulong)hi << 32) | lo;
            return IsUserPtr(c) ? c : 0;
        }

        ushort[]? CameraKeys()
        {
            if (_camKeys != null) return _camKeys;
            if (ReadBytes(_exeBase + _camKeyTableRva, 0x4000) is not { Length: 0x4000 } kb) return null;
            var k = new ushort[4096];
            for (int i = 0; i < k.Length; i++) k[i] = BinaryPrimitives.ReadUInt16LittleEndian(kb.AsSpan(i * 4));
            return k.Any(v => v != 0) ? _camKeys = k : null;   // all zero: not initialised yet
        }

        static float DecodeCam(ReadOnlySpan<byte> s, ushort[] k)
        {
            ushort elo = BinaryPrimitives.ReadUInt16LittleEndian(s), ehi = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
            ushort hi = (ushort)~(ushort)(ehi - k[elo & 0xFFF]);
            ushort lo = (ushort)~(ushort)(elo - k[hi & 0xFFF]);
            return BitConverter.Int32BitsToSingle(lo | (hi << 16));
        }

        static byte[] EncodeCam(float v, ushort[] k)
        {
            int bits = BitConverter.SingleToInt32Bits(v);
            ushort lo = (ushort)bits, hi = (ushort)(bits >> 16);
            ushort elo = (ushort)((ushort)~lo + k[hi & 0xFFF]);
            ushort ehi = (ushort)((ushort)~hi + k[elo & 0xFFF]);
            return [(byte)elo, (byte)(elo >> 8), (byte)ehi, (byte)(ehi >> 8)];
        }

        /// <summary>A camera value's (target, current), null when the camera can't be read (menus, loading).</summary>
        public (float Target, float Current)? ReadCameraValue(int index)
        {
            ulong c = CameraController();
            if (c == 0 || CameraKeys() is not { } k || ReadBytes(c + _camSlotsOff + (ulong)(CameraSlotSize * index), 12) is not { Length: 12 } s) return null;
            return (DecodeCam(s, k), DecodeCam(s.AsSpan(4), k));
        }

        /// <summary>Set a camera value outright (target, current and start). Returns false when the camera can't be read.</summary>
        public bool WriteCameraValue(int index, float value, ScatterWriteHandle? writes = null)
        {
            ulong c = CameraController();
            if (c == 0 || CameraKeys() is not { } k) return false;
            var e = EncodeCam(value, k);
            return Write(c + _camSlotsOff + (ulong)(CameraSlotSize * index), [.. e, .. e, .. e], writes);
        }
    }
}
