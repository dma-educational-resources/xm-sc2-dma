using System.Numerics;
using ImGuiNET;
using SC2Radar.DMA;
using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// What each player the user turned on can see: the union of the sight circles (catalog Sight) of its own units and
    /// of every player sharing vision with it (the game's alliance table, SC2Memory.SharesVision), drawn as one shaded
    /// area with an outline in the player's colour. Rasterised on a grid of CellPx screen pixels over the visible
    /// canvas, so overlapping circles don't stack and the cost is bounded by the canvas size, not the zoom. When the
    /// game's own visibility bitmap can be read (SC2Memory.Visibility) it is drawn instead of the estimate.
    /// </summary>
    public static class VisionOverlay
    {
        const float CellPx = 3f;
        static Config Config => Program.Config;

        static bool[] _grid = [];
        static Dictionary<int, uint> _playerColors = [];

        /// <summary>A player's colour in the current colour mode, grey before any of its units is seen.</summary>
        public static uint PlayerColor(int player, bool? alliance = null) =>
            PlayerColors.ForPlayer(player, _playerColors.TryGetValue(player, out uint c) ? c : 0xFFAAAAAA, alliance);

        /// <summary>Players whose units player `viewer` sees: itself plus everyone sharing vision with it.</summary>
        public static ushort VisionSources(int viewer)
        {
            var mem = Memory.Instance;
            ushort mask = (ushort)(1 << viewer);
            if (mem == null) return mask;
            for (int q = 0; q < 16; q++)
                if (mem.SharesVision(q, viewer)) mask |= (ushort)(1 << q);
            return mask;
        }

        /// <param name="coloursOnly">Performance mode: only note each player's colour (PlayerColor), draw nothing.</param>
        public static void Draw(in RadarFrameCtx ctx, UnitEntry[] units, bool coloursOnly = false)
        {
            _playerColors = units.Where(u => u.Owner is not (0 or 16))
                                 .GroupBy(u => (int)u.Owner)
                                 .ToDictionary(g => g.Key, g => g.GroupBy(u => u.Color).MaxBy(c => c.Count())!.Key);
            if (coloursOnly || Config.VisionPlayers.Count == 0) return;

            int w = (int)MathF.Ceiling(ctx.CanvasSize.X / CellPx), h = (int)MathF.Ceiling(ctx.CanvasSize.Y / CellPx);
            if (w <= 0 || h <= 0) return;
            if (_grid.Length < w * h) _grid = new bool[w * h];
            float cellW = CellPx / ctx.Scale;                                     // cell size in map units
            float mapLeft = ctx.MapCenter.X - ctx.CanvasSize.X * 0.5f / ctx.Scale;   // map coordinates of the canvas top-left
            float mapTop  = ctx.MapCenter.Y + ctx.CanvasSize.Y * 0.5f / ctx.Scale;

            var terrain = Config.VisionHighGround ? Memory.Instance?.Terrain : null;
            var drawn = new HashSet<ushort>();   // allies sharing vision see the same area: draw it once
            foreach (int viewer in Config.VisionPlayers.Order())
            {
                ushort sources = VisionSources(viewer);
                if (!drawn.Add(sources)) continue;
                Array.Clear(_grid, 0, w * h);
                bool any = false;
                // the game's own visibility when it can be read (high ground, detection-free sight, shared vision all
                // included); otherwise estimated from the sight circles below
                if (Memory.Instance?.Visibility(viewer) is { } seen)
                {
                    for (int j = 0; j < h; j++)
                    {
                        int my = (int)MathF.Floor(mapTop - (j + 0.5f) * cellW);
                        for (int i = 0; i < w; i++)
                            if (seen.At((int)MathF.Floor(mapLeft + (i + 0.5f) * cellW), my)) { _grid[j * w + i] = true; any = true; }
                    }
                }
                else foreach (ref readonly var u in units.AsSpan())
                    if (u.Sight > 0f && u.Owner < 16 && ((sources >> u.Owner) & 1) != 0)
                    {
                        // high ground: a unit doesn't see ground higher than its eyes (its ground + catalog eye height);
                        // only units with higher ground in range pay for the per-cell check
                        float eye = float.MaxValue;
                        if (terrain != null)
                        {
                            float e = terrain.Sample(u.X, u.Y) + Math.Clamp(u.EyeHeight, 0f, 20f) + HighGroundSlack;
                            if (terrain.MaxIn(u.X - u.Sight, u.Y - u.Sight, u.X + u.Sight, u.Y + u.Sight) > e) eye = e;
                        }
                        any |= Stamp(u.X, u.Y, u.Sight, w, h, cellW, mapLeft, mapTop, eye < float.MaxValue ? terrain : null, eye);
                    }
                if (!any) continue;
                uint col = PlayerColor(viewer);
                uint fill = (col & 0x00FFFFFF) | ((uint)(Math.Clamp(Config.VisionFillAlpha, 0f, 1f) * 255f) << 24);
                uint edge = (col & 0x00FFFFFF) | 0xB0000000;
                DrawGrid(ctx.DrawList, ctx.CanvasPos, w, h, fill, edge);
            }
        }

        /// <summary>The union of some circles (map units) as one shaded area with an outline - overlaps don't stack.</summary>
        internal static void DrawUnion(in RadarFrameCtx ctx, IEnumerable<(float X, float Y, float R)> circles, uint fill, uint edge)
        {
            int w = (int)MathF.Ceiling(ctx.CanvasSize.X / CellPx), h = (int)MathF.Ceiling(ctx.CanvasSize.Y / CellPx);
            if (w <= 0 || h <= 0) return;
            if (_grid.Length < w * h) _grid = new bool[w * h];
            Array.Clear(_grid, 0, w * h);
            float cellW = CellPx / ctx.Scale;
            float mapLeft = ctx.MapCenter.X - ctx.CanvasSize.X * 0.5f / ctx.Scale;
            float mapTop  = ctx.MapCenter.Y + ctx.CanvasSize.Y * 0.5f / ctx.Scale;
            bool any = false;
            foreach (var (x, y, r) in circles) any |= Stamp(x, y, r, w, h, cellW, mapLeft, mapTop, null, float.MaxValue);
            if (any) DrawGrid(ctx.DrawList, ctx.CanvasPos, w, h, fill, edge);
        }

        // a cliff step is about 2 map units; anything under this counts as the same level (ramps, bumps)
        const float HighGroundSlack = 0.6f;

        /// <summary>Mark the cells whose centres lie inside the circle (and, with terrain, aren't above eye level).
        /// Returns true if any cell is on the canvas.</summary>
        static bool Stamp(float x, float y, float r, int w, int h, float cellW, float mapLeft, float mapTop, TerrainMap? terrain, float eye)
        {
            float cx = (x - mapLeft) / cellW, cy = (mapTop - y) / cellW, rc = r / cellW;
            int y0 = Math.Max(0, (int)MathF.Floor(cy - rc)), y1 = Math.Min(h - 1, (int)MathF.Ceiling(cy + rc));
            bool any = false;
            for (int gy = y0; gy <= y1; gy++)
            {
                float dy = gy + 0.5f - cy;
                float d2 = rc * rc - dy * dy;
                if (d2 < 0) continue;
                float dx = MathF.Sqrt(d2);
                int x0 = Math.Max(0, (int)MathF.Ceiling(cx - dx - 0.5f)), x1 = Math.Min(w - 1, (int)MathF.Floor(cx + dx - 0.5f));
                if (x0 > x1) continue;
                if (terrain == null) { Array.Fill(_grid, true, gy * w + x0, x1 - x0 + 1); any = true; continue; }
                float my = mapTop - (gy + 0.5f) * cellW;
                for (int gx = x0; gx <= x1; gx++)
                    if (terrain.Sample(mapLeft + (gx + 0.5f) * cellW, my) <= eye) { _grid[gy * w + gx] = true; any = true; }
            }
            return any;
        }

        static void DrawGrid(ImDrawListPtr dl, Vector2 origin, int w, int h, uint fill, uint edge)
        {
            for (int gy = 0; gy < h; gy++)
            {
                int row = gy * w;
                float top = origin.Y + gy * CellPx, bottom = top + CellPx;
                // fill: one rect per run of visible cells
                for (int gx = 0; gx < w;)
                {
                    if (!_grid[row + gx]) { gx++; continue; }
                    int start = gx;
                    while (gx < w && _grid[row + gx]) gx++;
                    dl.AddRectFilled(new Vector2(origin.X + start * CellPx, top), new Vector2(origin.X + gx * CellPx, bottom), fill);
                }
                // outline: vertical edges where a run starts or ends inside this row (not along the canvas border,
                // so vision running off-screen stays open)
                for (int gx = 1; gx < w; gx++)
                    if (_grid[row + gx - 1] != _grid[row + gx])
                        dl.AddLine(new Vector2(origin.X + gx * CellPx, top), new Vector2(origin.X + gx * CellPx, bottom), edge);
                // outline: horizontal edges against the row above, merged into runs
                if (gy == 0) continue;
                for (int gx = 0; gx < w;)
                {
                    if (_grid[row + gx] == _grid[row - w + gx]) { gx++; continue; }
                    int start = gx;
                    while (gx < w && _grid[row + gx] != _grid[row - w + gx]) gx++;
                    dl.AddLine(new Vector2(origin.X + start * CellPx, top), new Vector2(origin.X + gx * CellPx, top), edge);
                }
            }
        }
    }
}
