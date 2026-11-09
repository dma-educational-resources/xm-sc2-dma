using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Local player, from the IsActorLocalPlayer actor term (5.0.x evaluate at exe+0x20017C0; its actor-side owner
    /// byte at +0x379 belongs to another actor class - render actors use the unit record's owner instead):
    ///   info  = ((~u32[g2] + u32[k2]) &lt;&lt; 32) | (u32[g1] ^ u32[k1])     encoded pointer in four exe globals
    ///   local = byte info[0x0C]
    /// </summary>
    public sealed partial class SC2Memory
    {
        public const int LocalInfoPlayerOff = 0x0C;

        // "mov eax,[g1]; xor eax,[k1]; movzx edx,byte [rbx+379h]; mov [rsp+38h],eax; mov eax,[g2]; not eax; add eax,[k2]"
        const string LocalPlayerPattern = "8B 05 ? ? ? ? 33 05 ? ? ? ? 0F B6 93 79 03 00 00 89 44 24 38 8B 05 ? ? ? ? F7 D0 03 05 ? ? ? ?";
        static readonly CodeSig[] LocalPlayerSigs =
        [
            new("local player g1", LocalPlayerPattern, 2),
            new("local player k1", LocalPlayerPattern, 8),
            new("local player g2", LocalPlayerPattern, 25),
            new("local player k2", LocalPlayerPattern, 33),
        ];
        ulong[] _localPlayerRvas = [];
        ulong _localInfo;

        /// <summary>The local player's id, or -1 when unknown (signature missed, not in a game).</summary>
        public int LocalPlayer { get; private set; } = -1;

        void ResolveLocalPlayer(List<(uint Rva, byte[] Data)> code)
        {
            var rvas = LocalPlayerSigs.Select(s => code.Count > 0 ? ResolveSig(code, s, out _) : null).ToArray();
            _localPlayerRvas = rvas.All(r => r is not null) ? rvas.Select(r => r!.Value).ToArray() : [];
            if (_localPlayerRvas.Length == 0) XMLogging.WriteLine("[Actors] signature 'local player' missed");
        }

        void ReadLocalPlayer()
        {
            if (_localPlayerRvas.Length != 4) { LocalPlayer = -1; return; }
            uint lo = U32At(_localPlayerRvas[0]) ^ U32At(_localPlayerRvas[1]);
            uint hi = ~U32At(_localPlayerRvas[2]) + U32At(_localPlayerRvas[3]);
            ulong info = ((ulong)hi << 32) | lo;
            _localInfo = info;
            int raw = IsUserPtr(info) && ReadBytes(info + LocalInfoPlayerOff, 1) is { Length: 1 } p ? p[0] : -1;
            // Observer View holds the client at 16: the radar keeps treating the real player as local
            LocalPlayer = raw is >= 0 and < 16 ? raw : raw == ObserverId && ObserverRealPlayer > 0 ? ObserverRealPlayer : -1;
        }

        // ── Observer View (Features/MemoryWrites/ObserverView) ───────────────────

        /// <summary>The local id an observer client has: full vision, no units of its own.</summary>
        public const byte ObserverId = 16;

        /// <summary>The player the client really is while Observer View holds it at 16, else -1.</summary>
        public int ObserverRealPlayer { get; set; } = -1;

        /// <summary>The client's local player id byte as it is right now (16 = observer), null when unknown.</summary>
        public int? RawLocalId() => IsUserPtr(_localInfo) && ReadBytes(_localInfo + LocalInfoPlayerOff, 1) is [var b] ? b : null;

        /// <summary>Write the client's local player id byte (client-side state: which player this PC is).</summary>
        public bool WriteLocalId(byte id, ScatterWriteHandle? writes = null) => IsUserPtr(_localInfo) && Write(_localInfo + LocalInfoPlayerOff, id, writes);
    }
}
