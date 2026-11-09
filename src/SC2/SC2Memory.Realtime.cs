using System.Numerics;
using VmmSharpEx.Options;

namespace SC2Radar.SC2
{
    /// <summary>
    /// The realtime worker's read: every actor's frame in one batched read per tick, the EFT radar's realtime pattern.
    /// Config.PositionReads picks how the batch is laid out - the cost differs per DMA device:
    ///   0 pages  - the pages the frames sit on (a PageSet: one scatter of whole pages)
    ///   1 ranges - one exact range per actor, vtable to cloak flags (a VmmScatter entry each)
    ///   2 fields - five typed entries per actor: vtable, position, model, flags, unit object (only the bytes used)
    /// </summary>
    public sealed partial class SC2Memory
    {
        /// <summary>What the radar needs of an actor each frame.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ActorFrame
        {
            public ulong Vtable;
            public float X, Y;
            public ulong Model;
            public uint  Flags;
            public ulong UnitObj;   // actor + RecordPath[0]: the game-side unit object (the vitals pass starts from it)
        }

        public const int PositionReadPages = 0, PositionReadRanges = 1, PositionReadFields = 2;

        // The read is bandwidth-bound (the device fetches whole pages), so not every actor is read every frame:
        //   moving actors           - every frame
        //   ones that can't move    - all of them every FixedReadMs (ActorInfo.CantMove: resources, most buildings)
        //   decoration              - a slice of DecorPerFrame per frame, round-robin (ActorInfo.IsDecor: scenery, other;
        //                             an arcade map can have thousands - 12,000 "talkiconring" on one)
        // The ones not read this frame keep their last frame.
        const int FixedReadMs = 250, DecorPerFrame = 128;
        readonly Dictionary<ulong, ActorFrame> _slowFrames = [];   // realtime worker: the last frame of each fixed / decor actor
        long _nextFixedRead;
        int _decorCursor;

        /// <summary>Every actor's frame: this frame's read for the ones read now, the last one for the rest. False when
        /// the batch failed as a whole.</summary>
        bool ReadFrames(ActorInfo[] actors, out ActorFrame[] frames, out bool[] ok)
        {
            long now = Environment.TickCount64;
            bool fixedDue = now >= _nextFixedRead;
            var readIdx = new List<int>(actors.Length);
            var decor = new List<int>();
            for (int i = 0; i < actors.Length; i++)
            {
                if (actors[i].IsDecor) decor.Add(i);
                else if (!actors[i].CantMove || fixedDue) readIdx.Add(i);
            }
            if (decor.Count > 0)
            {
                int take = Math.Min(DecorPerFrame, decor.Count);
                _decorCursor %= decor.Count;
                for (int k = 0; k < take; k++) readIdx.Add(decor[(_decorCursor + k) % decor.Count]);
                _decorCursor = (_decorCursor + take) % decor.Count;
            }

            var read = new ActorInfo[readIdx.Count];
            for (int k = 0; k < read.Length; k++) read[k] = actors[readIdx[k]];
            frames = new ActorFrame[actors.Length];
            ok = new bool[actors.Length];
            if (!ReadActorFrames(read, out var rf, out var rok)) return false;
            if (fixedDue) _nextFixedRead = now + FixedReadMs;

            var fresh = new bool[actors.Length];
            for (int k = 0; k < read.Length; k++)
            {
                int i = readIdx[k];
                fresh[i] = true;
                frames[i] = rf[k]; ok[i] = rok[k];
                if (read[k].CantMove || read[k].IsDecor)
                {
                    if (rok[k]) _slowFrames[read[k].Addr] = rf[k];
                    else _slowFrames.Remove(read[k].Addr);
                }
            }
            for (int i = 0; i < actors.Length; i++)
                if (!fresh[i] && _slowFrames.TryGetValue(actors[i].Addr, out var f)) { frames[i] = f; ok[i] = true; }
            // actors gone from the list: forget their frames now and then
            if (_slowFrames.Count > 2 * actors.Length + 1024)
            {
                var live = actors.Select(a => a.Addr).ToHashSet();
                foreach (ulong addr in _slowFrames.Keys.Where(a => !live.Contains(a)).ToList()) _slowFrames.Remove(addr);
            }
            return true;
        }

        /// <summary>One batched read of every actor's frame (Config.PositionReads). False when the batch failed as a whole.</summary>
        bool ReadActorFrames(ActorInfo[] actors, out ActorFrame[] frames, out bool[] ok) =>
            ReadActorFrames(actors, _cfg.PositionReads, out frames, out ok);

        bool ReadActorFrames(ActorInfo[] actors, int mode, out ActorFrame[] frames, out bool[] ok)
        {
            frames = new ActorFrame[actors.Length];
            ok = new bool[actors.Length];
            int posOff = _cfg.ActorPosOffset, modOff = _cfg.ActorModelOffset;
            int len = Math.Max(Math.Max(Math.Max(posOff + 8, CloakFlagsOff + 4), modOff + 8), RecordPath[0] + 8);   // vtable .. the last field used
            var extra = _rideAlong;   // the cameras, read in the same batch (QueueCameras)
            var extraRead = new byte[]?[extra.Length];

            if (mode == PositionReadPages)
            {
                try
                {
                    using var set = PageSet.Rent();
                    foreach (var a in actors) set.Add(a.Addr, len);
                    foreach (var (a, size) in extra) set.Add(a, size);
                    set.Fetch(this);
                    Span<byte> buf = stackalloc byte[len];
                    for (int i = 0; i < actors.Length; i++)
                        if (ok[i] = set.Copy(actors[i].Addr, buf)) frames[i] = FrameFrom(buf, posOff, modOff);
                    for (int i = 0; i < extra.Length; i++)
                    {
                        var dst = new byte[extra[i].Size];
                        extraRead[i] = set.Copy(extra[i].Addr, dst) ? dst : null;
                    }
                }
                catch { return false; }
                _rideAlongRead = extraRead;
                return true;
            }

            try
            {
                using var scatter = CreateScatter(VmmFlags.NOCACHE);
                foreach (var a in actors)
                {
                    if (mode == PositionReadRanges) scatter.PrepareRead(a.Addr, (uint)len);
                    else
                    {
                        scatter.PrepareReadValue<ulong>(a.Addr);
                        scatter.PrepareReadValue<Vector2>(a.Addr + (ulong)posOff);
                        scatter.PrepareReadValue<ulong>(a.Addr + (ulong)modOff);
                        scatter.PrepareReadValue<uint>(a.Addr + CloakFlagsOff);
                        scatter.PrepareReadValue<ulong>(a.Addr + (ulong)RecordPath[0]);
                    }
                }
                foreach (var (a, size) in extra) scatter.PrepareRead(a, (uint)size);
                Execute(scatter);
                var range = mode == PositionReadRanges ? new byte[len] : null;
                for (int i = 0; i < actors.Length; i++)
                {
                    ulong a = actors[i].Addr;
                    if (range != null)
                    {
                        if (!scatter.ReadSpan<byte>(a, range)) continue;
                        frames[i] = FrameFrom(range, posOff, modOff);
                        ok[i] = true;
                    }
                    else if (scatter.ReadValue(a, out ulong vt) && scatter.ReadValue(a + (ulong)posOff, out Vector2 pos))
                    {
                        scatter.ReadValue(a + (ulong)modOff, out ulong model);
                        scatter.ReadValue(a + CloakFlagsOff, out uint flags);
                        scatter.ReadValue(a + (ulong)RecordPath[0], out ulong unitObj);
                        frames[i] = new ActorFrame { Vtable = vt, X = pos.X, Y = pos.Y, Model = model, Flags = flags, UnitObj = unitObj };
                        ok[i] = true;
                    }
                }
                for (int i = 0; i < extra.Length; i++)
                {
                    var dst = new byte[extra[i].Size];
                    extraRead[i] = scatter.ReadSpan<byte>(extra[i].Addr, dst) ? dst : null;
                }
                _rideAlongRead = extraRead;
                return true;
            }
            catch { return false; }
        }

        static ActorFrame FrameFrom(ReadOnlySpan<byte> s, int posOff, int modOff) => new()
        {
            Vtable = BinaryPrimitives.ReadUInt64LittleEndian(s),
            X      = BinaryPrimitives.ReadSingleLittleEndian(s[posOff..]),
            Y      = BinaryPrimitives.ReadSingleLittleEndian(s[(posOff + 4)..]),
            Model  = BinaryPrimitives.ReadUInt64LittleEndian(s[modOff..]),
            Flags  = BinaryPrimitives.ReadUInt32LittleEndian(s[CloakFlagsOff..]),
            UnitObj = BinaryPrimitives.ReadUInt64LittleEndian(s[RecordPath[0]..]),
        };
    }
}
