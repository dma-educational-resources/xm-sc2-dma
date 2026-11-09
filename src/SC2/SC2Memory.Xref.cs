namespace SC2Radar.SC2
{
    public sealed partial class SC2Memory
    {
        /// <summary>Read actor float positions and candidate int32 pairs back-to-back.</summary>
        ((float X, float Y)?[] Actors, (int X, int Y)?[] Cands) Snapshot2(ulong[] actorPos, ulong[] cands)
        {
            var ab = new byte[actorPos.Length * 8]; var aok = new bool[actorPos.Length];
            var cb = new byte[cands.Length * 8];    var cok = new bool[cands.Length];
            ReadValues(actorPos, 8, ab, aok);
            ReadValues(cands, 8, cb, cok);
            var a = new (float, float)?[actorPos.Length];
            for (int i = 0; i < a.Length; i++)
                if (aok[i]) a[i] = (BitConverter.ToSingle(ab, i * 8), BitConverter.ToSingle(ab, i * 8 + 4));
            var c = new (int, int)?[cands.Length];
            for (int i = 0; i < c.Length; i++)
                if (cok[i]) c[i] = (BitConverter.ToInt32(cb, i * 8), BitConverter.ToInt32(cb, i * 8 + 4));
            return (a, c);
        }

        static long Cell(int x, int y) => ((long)x << 32) | (uint)y;
    }
}
