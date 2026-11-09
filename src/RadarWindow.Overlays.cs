using System.Numerics;
using ImGuiNET;
using SC2Radar.DMA;
using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Map frame (the playable area outlined like the minimap, the rest faded, coordinate ticks), unit clusters when zoomed
    /// out ("×1 Command Center, ×40 Marine"), fading what your own vision doesn't cover, and buildings at their real
    /// footprint when zoomed in.
    /// </summary>
    public sealed partial class RadarWindow
    {
        // ── map frame ────────────────────────────────────────────────────────

        /// <summary>
        /// The playable area (from the pathing mesh) as the minimap frames it: outside darkened with a soft edge, a thin
        /// border with corner ticks, and map coordinates along the top and left once there is room for them.
        /// </summary>
        private void DrawMapFrame(in RadarFrameCtx ctx)
        {
            if (!Config.MapFrame || Memory.Instance?.Pathing?.Playable is not { } pb || pb.Max.X <= pb.Min.X) return;
            var (min, max) = pb;
            var dl = ctx.DrawList;
            var tl = ctx.WorldToScreen(min.X, max.Y);   // world Y grows up, screen Y down
            var br = ctx.WorldToScreen(max.X, min.Y);
            var c0 = ctx.CanvasPos; var c1 = ctx.CanvasPos + ctx.CanvasSize;

            // outside: dark, with a short fade into the map
            const uint Shade = 0x9A000000;
            dl.AddRectFilled(c0, new Vector2(c1.X, MathF.Max(c0.Y, tl.Y)), Shade);
            dl.AddRectFilled(new Vector2(c0.X, MathF.Min(c1.Y, br.Y)), c1, Shade);
            dl.AddRectFilled(new Vector2(c0.X, tl.Y), new Vector2(MathF.Max(c0.X, tl.X), br.Y), Shade);
            dl.AddRectFilled(new Vector2(MathF.Min(c1.X, br.X), tl.Y), new Vector2(c1.X, br.Y), Shade);
            const float Fade = 14f;
            const uint In = 0x00000000, Out = 0x50000000;
            dl.AddRectFilledMultiColor(tl, new Vector2(br.X, tl.Y + Fade), Out, Out, In, In);
            dl.AddRectFilledMultiColor(new Vector2(tl.X, br.Y - Fade), br, In, In, Out, Out);
            dl.AddRectFilledMultiColor(tl, new Vector2(tl.X + Fade, br.Y), Out, In, In, Out);
            dl.AddRectFilledMultiColor(new Vector2(br.X - Fade, tl.Y), br, In, Out, Out, In);

            // border and corner ticks
            const uint Line = 0x70FFFFFF, Tick = 0xC8FFFFFF;
            dl.AddRect(tl, br, Line, 0f, ImDrawFlags.None, 1f);
            float t = MathF.Min(16f, MathF.Min(br.X - tl.X, br.Y - tl.Y) * 0.25f);
            void Corner(Vector2 p, float sx, float sy)
            {
                dl.AddLine(p, p + new Vector2(sx * t, 0f), Tick, 2f);
                dl.AddLine(p, p + new Vector2(0f, sy * t), Tick, 2f);
            }
            Corner(tl, 1, 1); Corner(new Vector2(br.X, tl.Y), -1, 1); Corner(new Vector2(tl.X, br.Y), 1, -1); Corner(br, -1, -1);

            // coordinates: every 16, 32 or 64 map units, whichever leaves at least 48 px between ticks
            float step = ctx.Scale * 16f >= 48f ? 16f : ctx.Scale * 32f >= 48f ? 32f : 64f;
            if (ctx.Scale * step < 40f) return;
            const uint Num = 0x90FFFFFF;
            for (float x = MathF.Ceiling(min.X / step) * step; x <= max.X; x += step)
            {
                var p = ctx.WorldToScreen(x, max.Y);
                if (p.X < c0.X || p.X > c1.X) continue;
                dl.AddLine(p, p + new Vector2(0f, 5f), Tick, 1f);
                if (p.Y - 15f >= c0.Y) dl.AddText(p + new Vector2(2f, -15f), Num, ((int)x).ToString());
            }
            for (float y = MathF.Ceiling(min.Y / step) * step; y <= max.Y; y += step)
            {
                var p = ctx.WorldToScreen(min.X, y);
                if (p.Y < c0.Y || p.Y > c1.Y) continue;
                dl.AddLine(p, p + new Vector2(5f, 0f), Tick, 1f);
                string s = ((int)y).ToString();
                float w = ImGuiNET.ImGui.CalcTextSize(s).X;
                if (p.X - w - 4f >= c0.X) dl.AddText(p + new Vector2(-w - 4f, -7f), Num, s);
            }
        }

        // ── outside your vision ──────────────────────────────────────────────

        private VisionMap? _myVision;
        private ushort _myVisionSources;

        /// <summary>Once per frame: your own vision (the game's bitmap), when Dim Outside My Vision is on and it can be read.</summary>
        private void PrepareVisionDim()
        {
            var mem = Memory.Instance;
            int local = mem?.LocalPlayer ?? -1;
            _myVision = Config.DimOutsideVision && local is >= 0 and < 16 ? mem!.Visibility(local) : null;
            _myVisionSources = _myVision != null ? VisionOverlay.VisionSources(local) : (ushort)0;
        }

        /// <summary>A player unit you (and those sharing vision with you) don't see in the game right now.</summary>
        private bool OutsideMyVision(in UnitEntry u) =>
            _myVision != null && u.Owner is > 0 and < 16 && ((_myVisionSources >> u.Owner) & 1) == 0 &&
            !_myVision.At((int)MathF.Floor(u.X), (int)MathF.Floor(u.Y));

        // ── building footprints ──────────────────────────────────────────────

        // footprint edge in map cells of the melee buildings (custom maps' buildings keep their marker)
        static readonly Dictionary<string, int> Footprints = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CommandCenter"] = 5, ["OrbitalCommand"] = 5, ["PlanetaryFortress"] = 5, ["Nexus"] = 5,
            ["Hatchery"] = 5, ["Lair"] = 5, ["Hive"] = 5,
            ["Barracks"] = 3, ["Factory"] = 3, ["Starport"] = 3, ["EngineeringBay"] = 3, ["Armory"] = 3, ["Bunker"] = 3,
            ["GhostAcademy"] = 3, ["FusionCore"] = 3, ["Refinery"] = 3, ["RefineryRich"] = 3,
            ["Gateway"] = 3, ["WarpGate"] = 3, ["Forge"] = 3, ["CyberneticsCore"] = 3, ["TwilightCouncil"] = 3,
            ["RoboticsFacility"] = 3, ["RoboticsBay"] = 3, ["Stargate"] = 3, ["TemplarArchives"] = 3, ["TemplarArchive"] = 3, ["SuperWarpGate"] = 3, ["FleetBeacon"] = 3,
            ["Assimilator"] = 3, ["AssimilatorRich"] = 3, ["Extractor"] = 3, ["ExtractorRich"] = 3,
            ["SpawningPool"] = 3, ["EvolutionChamber"] = 3, ["RoachWarren"] = 3, ["HydraliskDen"] = 3, ["InfestationPit"] = 3,
            ["Spire"] = 3, ["GreaterSpire"] = 3, ["UltraliskCavern"] = 3, ["LurkerDenMP"] = 3, ["LurkerDen"] = 3,
            ["BanelingNest"] = 3, ["NydusNetwork"] = 3, ["NydusCanal"] = 3,
            ["SupplyDepot"] = 2, ["SupplyDepotLowered"] = 2, ["MissileTurret"] = 2, ["Pylon"] = 2, ["PhotonCannon"] = 2,
            ["ShieldBattery"] = 2, ["DarkShrine"] = 2, ["SpineCrawler"] = 2, ["SporeCrawler"] = 2,
            ["TechLab"] = 2, ["Reactor"] = 2, ["BarracksTechLab"] = 2, ["BarracksReactor"] = 2, ["FactoryTechLab"] = 2,
            ["FactoryReactor"] = 2, ["StarportTechLab"] = 2, ["StarportReactor"] = 2,
            ["SensorTower"] = 1,
        };

        /// <summary>
        /// A building's real footprint under its marker once zoomed in far enough for it to be bigger than the marker:
        /// a faint square in the marker's colour. Returns false when it isn't drawn (unknown type, too far out).
        /// </summary>
        private bool DrawFootprint(ImDrawListPtr dl, Vector2 pos, in UnitEntry u, float scale, float markerR, uint col)
        {
            if (!Config.BuildingFootprints || !u.IsBuilding || !Footprints.TryGetValue(u.TypeName, out int cells)) return false;
            float half = cells * 0.5f * scale;
            if (half < markerR * 1.4f) return false;
            Vector2 a = pos - new Vector2(half), b = pos + new Vector2(half);
            uint rgb = col & 0x00FFFFFF, alpha = col >> 24;
            dl.AddRectFilled(a, b, rgb | ((alpha * 0x30 / 0xFF) << 24), 2f);
            dl.AddRect(a, b, rgb | ((alpha * 0xA0 / 0xFF) << 24), 2f, ImDrawFlags.None, 1.2f);
            return true;
        }

        // ── larva, inject, chrono (D2) ───────────────────────────────────────

        static readonly HashSet<string> HatcheryTypes = new(StringComparer.OrdinalIgnoreCase) { "Hatchery", "Lair", "Hive" };

        /// <summary>
        /// On each Hatchery / Lair / Hive: how many larva it has (larva of its owner within a few cells), and a green ring
        /// while a Queen's inject is in progress; a cyan ring on buildings under Chrono Boost (SC2Memory.Injected /
        /// Chronoed). Once zoomed in a little.
        /// </summary>
        private void DrawLarvaAndBuffs(in RadarFrameCtx ctx, UnitEntry[] units)
        {
            // the building itself is drawn: larva are still counted when the filter hides them
            static bool Shown(in UnitEntry u) => UnitFilter.IsVisible(in u) && (RadarProfiles.StyleFor(in u).Show || RadarProfiles.MarkerFor(in u) != null);
            var mem = Memory.Instance;
            if (!Config.ShowLarvaInject || mem == null || ctx.Scale < 2.5f) return;
            var dl = ctx.DrawList;
            var injected = mem.Injected; var chronoed = mem.Chronoed;
            var hatcheries = new List<(ulong Rec, byte Owner, Vector2 Pos, bool Shown)>();
            var larva = new List<(byte Owner, Vector2 Pos)>();
            var done = new HashSet<ulong>();
            foreach (ref readonly var u in units.AsSpan())
            {
                if (u.Record == 0 || !done.Add(u.Record)) continue;
                if (u.TypeName.Equals("Larva", StringComparison.OrdinalIgnoreCase)) larva.Add((u.Owner, new Vector2(u.X, u.Y)));
                else if (HatcheryTypes.Contains(u.TypeName)) hatcheries.Add((u.Record, u.Owner, new Vector2(u.X, u.Y), Shown(in u)));
                if (u.IsBuilding && chronoed.Contains(u.Record) && Shown(in u))
                {
                    var p = ctx.WorldToScreen(u.X, u.Y);
                    float r = MathF.Max(8f, 1.6f * ctx.Scale);
                    dl.AddCircle(p, r, 0xD0FFD040, 32, 2f);   // ABGR: cyan
                    if (_labelsOn && ctx.Scale >= 5f) RadarDraw.DrawOutlinedText(dl, p + new Vector2(r + 2f, -r), "chrono", 0xFFFFD040);
                }
            }
            // each larva counts once, for the nearest hatchery of its owner (two close hatcheries mustn't both claim it)
            var counts = new int[hatcheries.Count];
            foreach (var (lo, lp) in larva)
            {
                int best = -1; float bestSq = 5f * 5f;
                for (int h = 0; h < hatcheries.Count; h++)
                    if (hatcheries[h].Owner == lo && Vector2.DistanceSquared(lp, hatcheries[h].Pos) is var d && d < bestSq) { bestSq = d; best = h; }
                if (best >= 0) counts[best]++;
            }
            for (int hi = 0; hi < hatcheries.Count; hi++)
            {
                var (rec, owner, pos, shown) = hatcheries[hi];
                if (!shown) continue;   // filtered out (a search, a hidden type): no label for a building that isn't drawn
                int n = counts[hi];
                var p = ctx.WorldToScreen(pos.X, pos.Y);
                float r = MathF.Max(9f, 2.6f * ctx.Scale);
                if (injected.Contains(rec)) dl.AddCircle(p, r, 0xD040E040, 40, 2.2f);   // green: inject in progress
                string text = injected.Contains(rec) ? $"{n} larva · inject" : $"{n} larva";
                var ts = ImGuiNET.ImGui.CalcTextSize(text);
                // above the building: the larva's own labels sit right under it
                RadarDraw.DrawPill(dl, p + new Vector2(-ts.X * 0.5f - 4f, -r - ts.Y - 6f), text, n >= 3 ? 0xFF60E0FFu : 0xF0F0F0F0u);
            }
        }

        // ── clusters ─────────────────────────────────────────────────────────

        private readonly record struct ClusterPoint(Vector2 Pos, byte Owner, string Name, bool Building, uint Color);
        private readonly List<ClusterPoint> _clusterPts = new(1024);
        private readonly Dictionary<(byte, int, int), List<int>> _cells = [];
        private readonly HashSet<ulong> _clusterSeen = [];   // unit records already counted this frame (several actors per unit)
        private const float ClusterCellPx = 72f, ClusterBelowScale = 6f;
        private const int   ClusterMin = 5;

        /// <summary>Whether this frame groups units into cluster labels (zoomed out past where unit names show).</summary>
        private bool Clustering(float scale) => Config.ClusterUnits && Config.ShowLabels && scale < ClusterBelowScale;

        /// <summary>
        /// One label per crowd of a player's units: the units in each screen cell (fixed to the map, so they don't jump
        /// while panning) of at least <see cref="ClusterMin"/>, as one line - the biggest type and how many others,
        /// "40 Marine +5" (buildings first when bases are labelled) - with the full list on hover. Config.ClusterBases
        /// off: only armies (no buildings or workers). The markers stay; zooming in brings back each unit's own label.
        /// </summary>
        private void DrawClusters(in RadarFrameCtx ctx)
        {
            if (_clusterPts.Count == 0) return;
            var dl = ctx.DrawList;
            if (_cells.Count > 4096) _cells.Clear();   // cells from other zoom levels
            foreach (var l in _cells.Values) l.Clear();
            for (int i = 0; i < _clusterPts.Count; i++)
            {
                var p = _clusterPts[i];
                // cell from the map position, so a cell's units stay together as the view pans
                var w = ScreenToWorld(p.Pos) * ctx.Scale / ClusterCellPx;
                var key = (p.Owner, (int)MathF.Floor(w.X), (int)MathF.Floor(w.Y));
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = [];
                list.Add(i);
            }

            // biggest crowds first; each label goes where it doesn't cover one already drawn (nudged up or down), else is left out
            var placed = new List<(Vector2 A, Vector2 B)>();
            foreach (var members in _cells.Values.Where(m => m.Count >= ClusterMin).OrderByDescending(m => m.Count))
            {
                Vector2 sum = Vector2.Zero; float top = float.MaxValue;
                var counts = new Dictionary<string, (int N, bool Building)>();
                foreach (int i in members)
                {
                    var p = _clusterPts[i];
                    sum += p.Pos; top = MathF.Min(top, p.Pos.Y);
                    counts[p.Name] = (counts.GetValueOrDefault(p.Name).N + 1, p.Building);
                }
                var order = counts.OrderByDescending(kv => kv.Value.Building).ThenByDescending(kv => kv.Value.N).ThenBy(kv => kv.Key).ToList();
                // one line: the biggest type and how many others; the full list on hover
                var (topName, top0) = (order[0].Key, order[0].Value.N);
                int others = members.Count - top0;
                string text = others > 0 ? $"{top0} {topName} +{others}" : $"{top0} {topName}";
                string full = string.Join("\n", order.Select(kv => $"×{kv.Value.N} {kv.Key}"));
                var ts = ImGuiNET.ImGui.CalcTextSize(text);
                var box = ts + new Vector2(8f, 2f);   // DrawPill's size
                var tl0 = new Vector2(sum.X / members.Count - ts.X * 0.5f - 4f, top - ts.Y - 10f);
                Vector2? spot = null;
                foreach (float dy in (ReadOnlySpan<float>)[0f, -1f, 1f, -2f, 2f, -3f])
                {
                    var c = tl0 + new Vector2(0f, dy * (box.Y + 3f));
                    if (!placed.Any(r => c.X < r.B.X && c.X + box.X > r.A.X && c.Y < r.B.Y && c.Y + box.Y > r.A.Y)) { spot = c; break; }
                }
                if (spot is not { } tl) continue;
                placed.Add((tl, tl + box));
                var size = RadarDraw.DrawPill(dl, tl, text, 0xF0F0F0F0u);
                dl.AddRect(tl, tl + size, (_clusterPts[members[0]].Color & 0x00FFFFFF) | 0xB0000000, 3f, ImDrawFlags.None, 1f);
                if (ImGuiNET.ImGui.IsMouseHoveringRect(tl, tl + size)) SC2Radar.UI.ImGui.UI.TooltipForBounds(tl, tl + size, full);
            }
            _clusterPts.Clear();
        }
    }
}
