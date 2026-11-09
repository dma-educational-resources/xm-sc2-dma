// SC2 RadarWindow - draws units from UnitSnapshot on an ImGui canvas.

using System;
using System.Numerics;
using ImGuiNET;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI;
using SC2Radar.UI.ImGui;
using SC2Radar.UI.Misc;

namespace SC2Radar.UI.ImGui.Radar
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed partial class RadarWindow : IDisposable
    {
        private readonly DX11Window _host;
        private static Config Config => Program.Config;

        // Radar state
        private int     _zoom      = 100;
        private Vector2 _panOffset = new(128f, 128f); // default center; user pans freely
        private Vector2 _lastMouse = Vector2.Zero;
        private bool    _isPanning = false;
        private bool    _centeredOnUnits = false;
        private bool    _awaitingMap     = false;   // no terrain yet: re-centre when the next map's terrain arrives

        // FPS counter
        private int _fps;
        private readonly System.Diagnostics.Stopwatch _fpsSw = System.Diagnostics.Stopwatch.StartNew();
        private int _fpsCounter;

        // Per-frame canvas coords (set in Draw)
        private Vector2 _canvasPos;
        private Vector2 _canvasSize;

        public Vector2 WindowSize { get; private set; }

        public RadarWindow(DX11Window host)
        {
            _host = host;
            _zoom = Config.Zoom;
        }

        public void AdjustZoom(int delta)
        {
            _zoom = Math.Clamp(_zoom + delta, 5, 400);
            Config.Zoom = _zoom;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Main draw call (called every frame from ImGuiApp.BuildRadarFrame)
        // ─────────────────────────────────────────────────────────────────────

        public void Draw()
        {
            // Keep _zoom in sync with config (e.g. Settings slider changed it)
            _zoom = Math.Clamp(Config.Zoom, 5, 400);

            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.05f, 0.05f, 0.05f, 1f));

            bool open = true;
            const ImGuiWindowFlags NoBringToDisplayFront = (ImGuiWindowFlags)(1 << 13);
            ImGuiNET.ImGui.Begin("##RadarMain", ref open,
                ImGuiWindowFlags.NoTitleBar        |
                ImGuiWindowFlags.NoMove            |
                ImGuiWindowFlags.NoResize          |
                ImGuiWindowFlags.NoCollapse        |
                ImGuiWindowFlags.NoDocking         |
                ImGuiWindowFlags.NoNavFocus        |
                ImGuiWindowFlags.NoScrollbar       |
                ImGuiWindowFlags.NoScrollWithMouse |
                NoBringToDisplayFront);

            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopStyleVar(2);

            _canvasPos  = ImGuiNET.ImGui.GetCursorScreenPos();
            _canvasSize = ImGuiNET.ImGui.GetContentRegionAvail();

            // Persist window state to config
            var displaySize = ImGuiNET.ImGui.GetIO().DisplaySize;
            WindowSize = displaySize;
            Config.WindowWidth    = (int)displaySize.X;
            Config.WindowHeight   = (int)displaySize.Y;
            Config.WindowMaximized = _host.IsMaximized;
            if (_host.WindowX >= 0) Config.WindowX = _host.WindowX;
            if (_host.WindowY >= 0) Config.WindowY = _host.WindowY;

            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            dl.AddRectFilled(_canvasPos, _canvasPos + _canvasSize, ImGuiStyle.RadarBg);

            long radarT0 = Stopwatch.GetTimestamp();
            DrawRadarContent(dl);
            PerfStats.RecordFrame(ImGuiNET.ImGui.GetIO().DeltaTime * 1000f, (float)Stopwatch.GetElapsedTime(radarT0).TotalMilliseconds,
                                  PlayerUnitCount(Memory.Instance?.Snapshot.Units ?? []));
            ScoutAlerts.Update();
            MatchStats.Update();
            MarkerAlerts.Update();
            BaseAlerts.Update();
            DrawClockOverlay(dl);
            DrawUnitContextMenu();
            DrawBackgroundMenu();
            DrawPoiContextMenu();
            HandleInput();
            DrawFPS(dl);

            ImGuiNET.ImGui.End();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Radar content
        // ─────────────────────────────────────────────────────────────────────

        private DateTime? _lostAt;   // when a connection / game we had went away (banner + timer), null while fine
        private bool _hadGame;
        private bool _labelsOn = true;   // Labels, unless performance mode turns them off this frame

        private void DrawRadarContent(ImDrawListPtr dl)
        {
            // the misc worker re-attaches by itself: say what happened and for how long
            if (Memory.IsConnected && Memory.InGame) { _hadGame = true; _lostAt = null; }
            else if (_hadGame) _lostAt ??= DateTime.UtcNow;
            string since = _lostAt is { } t ? $" ({(int)(DateTime.UtcNow - t).TotalSeconds}s)" : "";
            _poiHits.Clear();   // nothing to hover or drag until this frame draws them

            if (!Memory.IsConnected)
            {
                if (_lostAt != null) DrawBanner(dl, $"Lost connection to the game PC{since} - reconnecting...", ImGuiStyle.Danger);
                else DrawStatusString(dl, "Connecting to the FPGA...");
                return;
            }

            if (!Memory.InGame)
            {
                if (_lostAt != null) DrawBanner(dl, $"StarCraft II closed or left the match{since} - waiting for it...", ImGuiStyle.Warning);
                else DrawStatusString(dl, "Waiting for StarCraft II...");
                return;
            }

            var snapshot = Memory.Instance?.Snapshot;
            if (snapshot == null)
            {
                DrawStatusString(dl, "Scanning...");
                return;
            }

            var units = snapshot.Units;

            // performance mode (Settings ▸ Performance): lighter drawing on big maps
            bool perf = PerfStats.PerfModeActive;
            _labelsOn = Config.ShowLabels && !(perf && Config.PerfSkipLabels);
            bool layers = !(perf && Config.PerfSkipLayers);

            // Unit Filter "Centre radar on it"
            if (UnitFilter.CenterRequest is { } req)
            {
                UnitFilter.CenterRequest = null;
                _panOffset = req;
                _centeredOnUnits = true;
            }

            // a new match (the map data went away and came back): centre again
            var terrain = Memory.Instance?.Terrain;
            if (terrain == null) _awaitingMap = true;
            else if (_awaitingMap) { _awaitingMap = false; _centeredOnUnits = false; }

            // first data of a session / match: centre the view on your own units (all units until the owner is known)
            if (!_centeredOnUnits && units.Length > 0)
            {
                _centeredOnUnits = true;
                _panOffset = HomePosition(units);
            }

            // Scale: pixels per world unit based on zoom %
            float scale = _zoom * 0.5f;

            // Map center always uses the pan offset - radar is always in free mode
            Vector2 center = _panOffset;

            var ctx = new RadarFrameCtx(_canvasPos, _canvasSize, center, scale, dl);

            dl.PushClipRect(_canvasPos, _canvasPos + _canvasSize, true);

            TerrainLayer.Draw(in ctx);
            if (layers) CreepLayer.Draw(in ctx);
            PlayerColors.Update(units);
            VisionOverlay.Draw(in ctx, units, coloursOnly: !layers);
            if (layers) PowerFieldLayer.Draw(in ctx, units);
            DrawRangeRings(in ctx, units);
            DrawCameras(in ctx);
            DrawMapFrame(in ctx);
            DrawPOIs(in ctx);
            OrderInfo.DrawLines(in ctx, units);   // under the markers

            // per-type styles of the active profile (Settings ▸ Visuals ▸ Entity Info) and its game-mode markers; the
            // category toggles and the Unit Filter decide what is drawn at all
            {
                float sizeMul = Config.UnitDotRadius / 3.5f;   // Display ▸ Marker Scale
                // Display ▸ Detail By Zoom: 0 far out (small plain dots) .. 1 close in (full markers, outlines, bars)
                float detail = perf && Config.PerfSimpleMarkers ? 0f : Config.ZoomDetail ? Math.Clamp((scale - 3f) / 3f, 0f, 1f) : 1f;
                bool fancy = detail >= 0.5f;
                var fs = UnitFilter.S;
                PrepareVisionDim();
                bool clustering = Clustering(scale);
                var selectedBy = Config.ShowSelections && Config.TrackEvents ? Memory.Instance?.SelectedBy : null;
                for (int i = 0; i < units.Length; i++)
                {
                    ref readonly var u = ref units[i];
                    var pos = ctx.WorldToScreen(u.X, u.Y);

                    // Cull outside canvas
                    if (pos.X < _canvasPos.X - 20 || pos.X > _canvasPos.X + _canvasSize.X + 20 ||
                        pos.Y < _canvasPos.Y - 20 || pos.Y > _canvasPos.Y + _canvasSize.Y + 20)
                        continue;

                    // categories, hidden teams/names and search (UnitFilter / Unit Filter panel)
                    if (!UnitFilter.IsVisible(in u)) continue;
                    if (Config.TrackCargo && CargoInfo.IsCarried(u.Record)) continue;   // inside a transport: its actor still stands where it boarded
                    var kind   = RadarProfiles.KindOf(in u);
                    var style  = RadarProfiles.CustomFor(in u)?.Style ?? RadarProfiles.StyleFor(kind, RadarProfiles.FactionOf(in u));
                    var marker = RadarProfiles.MarkerFor(in u);
                    if (!style.Show && marker == null) continue;
                    bool dim  = UnitFilter.IsDimmed(in u);
                    uint ucol = marker?.Color ?? (style.TeamColor ? PlayerColors.For(in u) : style.Color);
                    uint col  = dim ? (ucol & 0x00FFFFFF) | 0x40000000 : ucol;
                    // Dim Outside My Vision: what the game itself doesn't show you right now, faded
                    bool unseen = !dim && OutsideMyVision(in u);
                    if (unseen) col = (col & 0x00FFFFFF) | (((col >> 24) * 0x60 / 0xFF) << 24);
                    float r   = marker != null ? marker.Size * sizeMul : style.Size * sizeMul * (0.65f + 0.35f * detail);   // markers keep their size
                    var shape = marker?.Shape ?? style.Shape;
                    if (fancy) DrawFootprint(dl, pos, in u, scale, r, col);   // Building Footprints, zoomed in

                    // focus ring for the row selected in the Unit Filter panel; saved highlights in their colour
                    if (UnitFilter.FocusName is { } focus && u.Name == focus)
                        dl.AddCircle(pos, r + 7f, 0xFF00FFFF, 24, 2.5f);
                    // live event feed: whoever has this unit selected right now, a thin ring in their colour
                    if (selectedBy != null && selectedBy.TryGetValue(u.Record, out int selP))
                        dl.AddCircle(pos, r + 3f, (PlayerColors.ForPlayer(selP, 0xFFFFFFFF) & 0x00FFFFFF) | 0xC0000000, 16, 1.2f);
                    if (UnitFilter.HighlightOf(in u) is uint hl)
                        dl.AddCircle(pos, r + 4.5f, dim ? (hl & 0x00FFFFFF) | 0x50000000 : hl, 20, 2f);
                    if (marker is { Glow: true } && !dim) RadarDraw.DrawGlow(dl, pos, r, marker.Color);

                    if (u.Burrowed)
                        RadarDraw.DrawMarker(dl, pos, MarkerShape.Ring, r + 1f, col, style.Outline);   // burrowed: hollow ring
                    else if (u.Cloaked)
                    {
                        // cloaked: the marker faded, with a thin white ring
                        RadarDraw.DrawMarker(dl, pos, shape, r, (col & 0x00FFFFFF) | 0x70000000, style.Outline, shadow: false);
                        dl.AddCircle(pos, r + 2.5f, dim ? 0x40FFFFFFu : 0xCCFFFFFFu, 20, 1f);
                    }
                    else
                        RadarDraw.DrawMarker(dl, pos, shape, r, col, style.Outline && !dim && (fancy || marker != null), shadow: !dim && fancy);

                    // minerals / gas left, once zoomed in far enough to read it per field
                    if (u.Category == UnitCategory.Resource && style.Amount && !dim && u.Resources >= 0 && _labelsOn && scale >= 10f)
                    {
                        string left = ResourceText(u.Resources);
                        var ts = ImGuiNET.ImGui.CalcTextSize(left);
                        RadarDraw.DrawOutlinedText(dl, pos + new Vector2(-ts.X * 0.5f, r + 2f), left,
                                                   u.Resources == 0 ? 0x80FFFFFFu : (ucol & 0x00FFFFFF) | 0xE0000000);
                    }

                    // vitals: shield / HP bars per the type's style (never / when damaged / always); figures once zoomed in
                    bool numbers = _labelsOn && scale >= 6f && fs.ShowVitalText && !dim;
                    float textX = r + 3f;   // vitals figures are laid out left to right after the marker
                    void VitalText(string s, uint c)
                    {
                        RadarDraw.DrawOutlinedText(dl, pos + new Vector2(textX, 6f), s, c);
                        textX += ImGuiNET.ImGui.CalcTextSize(s).X + 4f;
                    }
                    bool Wanted(BarMode m, bool hurt) => m == BarMode.Always || m == BarMode.WhenDamaged && hurt;
                    bool shieldsHurt = u.MaxShields > 0f && u.ShieldDamage > 0.5f;
                    bool showShield = !dim && fancy && fs.ShowShields && u.MaxShields > 0f && Wanted(style.Shields, shieldsHurt);
                    bool showHp     = !dim && fancy && fs.ShowDamage && Wanted(style.Health, u.IsDamaged) && (u.MaxHp > 0f || u.Damage > 0f);
                    if (showShield || showHp)
                    {
                        float bw = MathF.Max(10f, r * 2.6f), bh = MathF.Max(2f, r * 0.42f);
                        var bl = pos + new Vector2(-bw * 0.5f, r + 3f);
                        if (showShield)
                        {
                            RadarDraw.DrawBar(dl, bl, bw, bh, u.ShieldFrac, 0xFFFFA050u);
                            bl.Y += bh + 2f;
                            if (numbers) VitalText($"{u.MaxShields - u.ShieldDamage:0}/{u.MaxShields:0}", 0xFFFFA050);
                        }
                        if (showHp)
                        {
                            float frac = u.HpFrac;
                            if (frac >= 0f)
                                RadarDraw.DrawBar(dl, bl, bw, bh, frac, frac > 0.6f ? 0xFF40D060u : frac > 0.3f ? 0xFF30C8E0 : 0xFF4040E0u);
                            else
                                dl.AddCircleFilled(pos + new Vector2(r * 0.9f, -r * 0.9f), 2.6f, 0xFF3030FF);   // max HP unknown
                            if (numbers) VitalText(frac >= 0f ? $"{u.MaxHp - u.Damage:0}/{u.MaxHp:0}" : $"-{u.Damage:0}", 0xFF50D050);
                        }
                    }
                    if (fs.ShowEnergy && style.Energy && u.Energy >= 0.5f && numbers)
                        VitalText(u.MaxEnergy > 0f ? $"{u.Energy:0}/{u.MaxEnergy:0}" : $"{u.Energy:0}", 0xFFFF70D0);

                    // morph (amber) or production / research (cyan) in progress: a ring filling clockwise from the top,
                    // with the percentage (and queue length) when zoomed in
                    bool morphing = u.Progress >= 0f;
                    if ((morphing || u.QueueProgress >= 0f) && !dim && Config.ShowProgress)
                    {
                        float p = morphing ? u.Progress : u.QueueProgress;
                        uint pc = morphing ? 0xFF40E0FFu : 0xFFFFD040u;   // ABGR: amber / cyan
                        float pr = r * (shape is MarkerShape.Square or MarkerShape.Diamond or MarkerShape.Hexagon ? 1.3f : 1f) + 4f;
                        dl.AddCircle(pos, pr, 0x60000000, 32, 3.5f);
                        dl.PathArcTo(pos, pr, -MathF.PI / 2f, -MathF.PI / 2f + MathF.Tau * Math.Clamp(p, 0f, 1f), 32);
                        dl.PathStroke(pc, ImDrawFlags.None, 2.5f);
                        if (scale >= 4f)
                        {
                            // what it makes and the time left ("+1 Weapons 0:42"), else the percentage
                            string? making = morphing ? ProductionWidget.MorphTarget(in u) : u.QueueProduct is { Length: > 0 } q ? q : null;
                            string more = !morphing && u.QueueCount > 1 ? $" +{u.QueueCount - 1}" : "";
                            string pct = Config.ProgressDetails && u.TimeLeft >= 0f
                                ? $"{(making != null ? making + " " : "")}{SC2Memory.FormatClock(u.TimeLeft)}{more}"
                                : $"{p * 100f:0}%{more}";
                            var ts = ImGuiNET.ImGui.CalcTextSize(pct);
                            RadarDraw.DrawOutlinedText(dl, pos + new Vector2(-ts.X * 0.5f, -pr - ts.Y - 1f), pct, pc);
                        }
                    }

                    _hits.Add((pos, u));
                    // a loaded transport: how many are inside, at the marker's top right (the hover card lists them)
                    if (Config.TrackCargo && !dim && CargoInfo.Count(u.Record) is > 0 and var carrying)
                        RadarDraw.DrawPill(dl, pos + new Vector2(r * 0.7f, -r - ImGuiNET.ImGui.GetTextLineHeight() - 1f), carrying.ToString(), 0xFFFFFFFFu);
                    // crowd labels: armies only (no buildings or workers) unless bases are labelled too
                    if (clustering && u.Owner is > 0 and < 16 &&
                        (Config.ClusterBases ? u.Category is UnitCategory.Unit or UnitCategory.Worker or UnitCategory.Building : u.Category is UnitCategory.Unit) &&
                        (u.Record == 0 || _clusterSeen.Add(u.Record)))
                        _clusterPts.Add(new(pos, u.Owner, u.DisplayName, u.IsBuilding, ucol));

                    // labels once zoomed in enough that they don't pile up (a marker's can show at any zoom); drawn after
                    // all markers, grouped
                    string shown = marker is { Label.Length: > 0 } ? marker.Label : u.DisplayName;
                    bool always = marker is { AlwaysLabel: true };
                    if (_labelsOn && (style.Label || marker != null) && (scale >= 6f || always) && shown.Length > 0)
                        _labels.Add(new PendingLabel(pos, u.Burrowed ? $"{shown} (burrowed)" : u.Cloaked ? $"{shown} (cloaked)" : shown,
                                                     ucol, dim || unseen, dim ? 9 : marker != null ? -1 : kind switch
                                                     {
                                                         EntityKind.Army => 0, EntityKind.Worker => 1, EntityKind.Building => 2,
                                                         EntityKind.Mineral or EntityKind.Geyser => 4, _ => 3,
                                                     }, u.Record != 0 ? u.Record : u.Addr, r));
                }
                _clusterSeen.Clear();
                DrawClusters(in ctx);
                DrawLarvaAndBuffs(in ctx, units);
                BaseAlerts.Draw(in ctx);
                DrawGroupedLabels(dl);
                HandleUnitHover();
            }
            _hits.Clear();

            dl.PopClipRect();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Unit hover tooltip + right-click menu (same pattern as the EFT radar: hit-test the drawn
        // entities, OpenPopup on right-click, draw the popup every frame with the UI context-menu style).
        // SC2 units pile up, so the menu lists every unit type under the cursor.
        // ─────────────────────────────────────────────────────────────────────

        private readonly List<(Vector2 Pos, UnitEntry U)> _hits = new(1024);
        private List<(UnitEntry First, int Count)> _ctxGroups = [];
        private const float HitPx = 10f;
        private const string UnitCtxId = "##radar_unit_ctx";

        private List<(UnitEntry First, int Count)> UnitsUnderMouse()
        {
            var mp = ImGuiNET.ImGui.GetIO().MousePos;
            return _hits.Where(h => Vector2.DistanceSquared(h.Pos, mp) <= HitPx * HitPx)
                        .OrderBy(h => Vector2.DistanceSquared(h.Pos, mp))
                        .GroupBy(h => (h.U.Name, h.U.Color))
                        // units, not markers: a unit drawn with several actors counts once
                        .Select(g => (g.First().U, g.Select(h => h.U.Record != 0 ? h.U.Record : h.U.Addr).Distinct().Count()))
                        .Take(8).ToList();
        }

        private void HandleUnitHover()
        {
            var io = ImGuiNET.ImGui.GetIO();
            bool inCanvas = io.MousePos.X >= _canvasPos.X && io.MousePos.Y >= _canvasPos.Y &&
                            io.MousePos.X <= _canvasPos.X + _canvasSize.X && io.MousePos.Y <= _canvasPos.Y + _canvasSize.Y;
            if (!inCanvas || !ImGuiNET.ImGui.IsWindowHovered() || ImGuiNET.ImGui.IsPopupOpen(UnitCtxId) || ImGuiNET.ImGui.IsPopupOpen(PoiCtxId)) return;

            // points of interest first: they are few, and often sit on top of resources
            if (HandlePoiHover()) return;

            var under = UnitsUnderMouse();
            if (under.Count == 0)
            {
                // empty map: view options
                if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right) && !io.KeyCtrl)
                {
                    _bgUnits = Memory.Instance?.Snapshot.Units ?? [];
                    _bgWorld = ScreenToWorld(io.MousePos);
                    ImGuiNET.ImGui.OpenPopup(BgCtxId);
                }
                return;
            }

            UnitHoverCard.Draw(under, io.MousePos);

            // the weapon range of what's under the cursor
            if (Config.RangeHovered)
                foreach (var (pos, u) in _hits)
                    if (u.Range > 0f && Vector2.DistanceSquared(pos, io.MousePos) <= HitPx * HitPx)
                        RangeRing(ImGuiNET.ImGui.GetWindowDrawList(), pos, u.Range * _zoom * 0.5f, PlayerColors.For(in u), 0xC0);

            if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right) && !io.KeyCtrl)
            {
                _ctxGroups = under;
                ImGuiNET.ImGui.OpenPopup(UnitCtxId);
            }
        }

        /// <summary>
        /// Prefix wildcard for a family of variant names: strip trailing size/material words
        /// ("physicsimpactfleshlight" -> "physicsimpact*", "zerglingdeath_01" -> "zerglingdeath*"). Null if nothing to strip.
        /// </summary>
        private static string? FamilyPattern(string name)
        {
            string[] tails = ["light", "med", "medium", "heavy", "small", "large", "big",
                              "flesh", "creep", "rock", "dust", "sparks", "metal", "water", "dirt", "snow", "sand", "glass", "wood"];
            string s = System.Text.RegularExpressions.Regex.Replace(name, @"[_\d]+$", "");
            bool changed = s != name;
            for (bool again = true; again;)
            {
                again = false;
                foreach (var t in tails)
                    if (s.Length > t.Length + 3 && s.EndsWith(t, StringComparison.OrdinalIgnoreCase))
                    { s = s[..^t.Length].TrimEnd('_'); again = changed = true; }
            }
            return changed && s.Length >= 4 ? s + "*" : null;
        }

        /// <summary>"Player 6 (you)" / "(ally)" / "(enemy)" when the owner is known, else the colour-based team label.</summary>
        internal static string OwnerLabel(in UnitEntry u)
        {
            var mem = Memory.Instance;
            if (u.Record == 0 || u.Owner is 0 or >= 16 || mem == null) return UnitFilter.TeamLabel(u.Color);
            int local = mem.LocalPlayer;
            string rel = local < 0 || !mem.HasAllianceTable ? "" : u.Owner == local ? " (you)" : mem.IsAlly(u.Owner, local) ? " (ally)" : " (enemy)";
            string name = mem.PlayerDisplayName(u.Owner);
            return $"{(name.Length > 0 ? name : $"Player {u.Owner}")}{rel}";
        }

        /// <summary>"1,350", or "∞" for the effectively endless fields some maps use.</summary>
        internal static string ResourceText(int left) => left >= 100_000_000 ? Infinity : left.ToString("N0");

        private static string? _infinity;
        /// <summary>"∞" when the UI font has the glyph, else "inf".</summary>
        private static unsafe string Infinity =>
            _infinity ??= ImGuiNET.ImGui.GetFont().NativePtr != null && ImGuiNET.ImGui.GetFont().FindGlyphNoFallback('∞').NativePtr != null ? "∞" : "inf";

        // detection radius of the melee detectors (LotV values; it comes from a behaviour, not the weapon); which units detect
        // is the game's own flag (UnitEntry.Detector), the names only cover a record that hasn't resolved yet
        static readonly Dictionary<string, float> DetectorRadius = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MissileTurret"] = 11f, ["PhotonCannon"] = 11f, ["SporeCrawler"] = 11f,
            ["Observer"] = 11f, ["ObserverSiegeMode"] = 13.75f, ["Overseer"] = 11f, ["OverseerSiegeMode"] = 11f, ["Raven"] = 11f,
        };
        const float DefaultDetectionRadius = 11f;   // most detectors, custom-map ones (Science Vessel) included

        /// <summary>Whether DetectionRadius is the melee value for the type rather than the default guess.</summary>
        internal static bool DetectionRadiusKnown(in UnitEntry u) => DetectorRadius.ContainsKey(u.TypeName);

        /// <summary>How far a unit detects, null when it isn't a detector.</summary>
        internal static float? DetectionRadius(in UnitEntry u) =>
            DetectorRadius.TryGetValue(u.TypeName, out float r) ? r : u.Detector ? DefaultDetectionRadius : null;

        private static void RangeRing(ImDrawListPtr dl, Vector2 pos, float radius, uint col, uint alpha)
        {
            if (radius < 3f) return;
            dl.AddCircle(pos, radius, (col & 0x00FFFFFF) | (alpha << 24), Math.Clamp((int)(radius / 3f), 24, 96), 1.5f);
        }

        /// <summary>
        /// What a camera looking at <paramref name="t"/> shows of the ground: the corners of the part of the screen above the
        /// bottom console (<paramref name="console"/>, a fraction of the screen height) projected onto the map - the same
        /// area the game's minimap outlines. The camera sits <paramref name="distance"/> back from the target, tilted
        /// <paramref name="pitch"/> degrees down, with a vertical field of view <paramref name="fov"/> - so the far edge comes
        /// out wider than the near one, and everything grows with the zoom. Returns near-left, near-right, far-right,
        /// far-left (map units).
        /// </summary>
        internal static Vector2[] CameraFootprint(Vector2 t, float distance, float pitch, float fov, float aspect, float console)
        {
            float th = Math.Clamp(pitch, 10f, 90f) * MathF.PI / 180f, f = Math.Clamp(fov, 5f, 120f) * MathF.PI / 360f;
            float h = distance * MathF.Sin(th), back = distance * MathF.Cos(th), tanF = MathF.Tan(f), tanW = aspect * tanF;
            // a row of the screen (v: -1 bottom .. +1 top): where its ray meets the ground (y from the target) and how wide
            // the view is there. The ray leaves the view axis at atan(v tan f), so it points that much less steeply down.
            (float Y, float HalfW) Edge(float v)
            {
                float off = MathF.Atan(v * tanF);
                float ray = Math.Clamp(th - off, 5f * MathF.PI / 180f, MathF.PI / 2f);
                float slant = h / MathF.Sin(ray);
                return (h / MathF.Tan(ray) - back, slant * MathF.Cos(off) * tanW);
            }
            var near = Edge(-1f + 2f * Math.Clamp(console, 0f, 0.6f));   // the top of the console
            var far = Edge(1f);
            return [new(t.X - near.HalfW, t.Y + near.Y), new(t.X + near.HalfW, t.Y + near.Y),
                    new(t.X + far.HalfW, t.Y + far.Y), new(t.X - far.HalfW, t.Y + far.Y)];
        }

        /// <summary>Each player's camera view: what their screen shows (see CameraFootprint), scaled with their zoom, in
        /// the player's colour with the name above. Your own comes from the live camera; the others from the synced target
        /// and distance with the game's default tilt and field of view.</summary>
        private void DrawCameras(in RadarFrameCtx ctx)
        {
            var mem = Memory.Instance;
            if (!Config.ShowCameras || mem == null || mem.Snapshot.Units.Length == 0) return;   // no mission: nothing to show
            int local = mem.LocalPlayer;
            // computer and map players have no lobby name (when names are known at all); their camera rarely moves
            bool named = Enumerable.Range(1, 15).Any(q => mem.PlayerName((byte)q).Length > 0);
            var size = SC2.Features.MemoryWrites.Ultrawide.DisplayMonitor.GameSize;
            float aspect = size.W > 0 && size.H > 0 ? (float)size.W / size.H : 16f / 9f;
            (Vector2, Vector2)? playable = mem.Pathing?.Playable is { } pb && pb.Max.X > pb.Min.X ? pb : null;
            float console = Config.CameraConsoleHeight / 100f;
            for (int p = 1; p < 16; p++)
            {
                if (p == local && !Config.ShowOwnCamera) continue;
                if (p != local && Config.CamerasEnemiesOnly && local > 0 && mem.IsAlly(p, local)) continue;   // allies' boxes off
                if (!Config.ShowAiCameras && named && mem.PlayerName((byte)p).Length == 0) continue;
                Vector2[] area;
                if (p == local && mem.LocalCameraView is { } v) area = CameraFootprint(v.Target, v.Distance, v.Pitch, v.Fov, aspect, console);
                else if (mem.CameraTarget(p) is { } t)
                {
                    var (st, sd) = SmoothCamera(p, t, mem.CameraDistance(p));
                    area = CameraFootprint(st, sd, 56f, 27.8f, aspect, console);
                }
                else { _camShown[p] = null; continue; }
                // the minimap cuts the view off at the playable area: near an edge the box squishes the same way
                if (playable is var (min, max)) area = ClipToRect(area, min, max);
                if (area.Length < 3) continue;
                uint col = VisionOverlay.PlayerColor(p);
                uint line = (col & 0x00FFFFFF) | 0xC0000000, fill = (col & 0x00FFFFFF) | 0x14000000;
                var pts = new Vector2[area.Length];
                for (int i = 0; i < area.Length; i++) pts[i] = ctx.WorldToScreen(area[i].X, area[i].Y);
                ctx.DrawList.AddConvexPolyFilled(ref pts[0], pts.Length, fill);
                ctx.DrawList.AddPolyline(ref pts[0], pts.Length, line, ImDrawFlags.Closed, 1.5f);
                string name = mem.PlayerDisplayName((byte)p) is { Length: > 0 } n ? n : $"Player {p}";
                var ts = ImGuiNET.ImGui.CalcTextSize(name);
                float top = pts.Min(q => q.Y), mid = (pts.Min(q => q.X) + pts.Max(q => q.X)) * 0.5f;
                RadarDraw.DrawOutlinedText(ctx.DrawList, new Vector2(mid - ts.X * 0.5f, top - ts.Y - 2f), name, line);
            }
        }

        // The other players' camera targets are synced at the game's pace, about every 90-130 ms while they move, so
        // drawn as read the boxes step. Each box glides to its latest target instead - most of the way within one sync -
        // and jumps when the camera does (a minimap click, a hotkey).
        private readonly (Vector2 Target, float Distance)?[] _camShown = new (Vector2, float)?[16];
        private const float CamGlideSeconds = 0.05f, CamJump = 30f;

        private (Vector2 Target, float Distance) SmoothCamera(int p, Vector2 target, float distance)
        {
            if (_camShown[p] is not { } s || Vector2.Distance(s.Target, target) > CamJump)
                return (_camShown[p] = (target, distance)).Value;
            float k = 1f - MathF.Exp(-ImGuiNET.ImGui.GetIO().DeltaTime / CamGlideSeconds);
            return (_camShown[p] = (Vector2.Lerp(s.Target, target, k), s.Distance + (distance - s.Distance) * k)).Value;
        }

        /// <summary>A convex polygon cut to an axis-aligned rectangle (Sutherland-Hodgman, one side at a time).</summary>
        internal static Vector2[] ClipToRect(Vector2[] poly, Vector2 min, Vector2 max)
        {
            var cur = new List<Vector2>(poly);
            // each side: is a point inside, and where does an edge cross it
            foreach (var (inside, cross) in new (Func<Vector2, bool>, Func<Vector2, Vector2, Vector2>)[]
            {
                (q => q.X >= min.X, (a, b) => Vector2.Lerp(a, b, (min.X - a.X) / (b.X - a.X))),
                (q => q.X <= max.X, (a, b) => Vector2.Lerp(a, b, (max.X - a.X) / (b.X - a.X))),
                (q => q.Y >= min.Y, (a, b) => Vector2.Lerp(a, b, (min.Y - a.Y) / (b.Y - a.Y))),
                (q => q.Y <= max.Y, (a, b) => Vector2.Lerp(a, b, (max.Y - a.Y) / (b.Y - a.Y))),
            })
            {
                var next = new List<Vector2>(cur.Count + 2);
                for (int i = 0; i < cur.Count; i++)
                {
                    Vector2 a = cur[i], b = cur[(i + 1) % cur.Count];
                    bool ina = inside(a), inb = inside(b);
                    if (ina) next.Add(a);
                    if (ina != inb) next.Add(cross(a, b));
                }
                cur = next;
                if (cur.Count == 0) break;
            }
            return [.. cur];
        }

        /// <summary>Weapon range of enemy static defence (or every enemy) and detection radius of enemy detectors.</summary>
        private void DrawRangeRings(in RadarFrameCtx ctx, UnitEntry[] units)
        {
            var mem = Memory.Instance;
            if (mem is not { LocalPlayer: > 0, HasAllianceTable: true } || !(Config.RangeDefences || Config.RangeEnemies || Config.DetectionRings)) return;
            int local = mem.LocalPlayer;
            var drawn = new HashSet<ulong>();
            foreach (ref readonly var u in units.AsSpan())
            {
                if (u.Record == 0 || u.Owner is 0 or >= 16 || u.Owner == local || mem.IsAlly(u.Owner, local) || !drawn.Add(u.Record)) continue;
                if (!UnitFilter.IsVisible(in u) || Config.TrackCargo && CargoInfo.IsCarried(u.Record)) continue;
                var pos = ctx.WorldToScreen(u.X, u.Y);
                uint col = PlayerColors.For(in u);
                if (u.Range > 0f && (Config.RangeEnemies || Config.RangeDefences && u.IsBuilding))
                    RangeRing(ctx.DrawList, pos, u.Range * ctx.Scale, col, 0x70);
                if (Config.DetectionRings && DetectionRadius(in u) is { } det)
                    RangeRing(ctx.DrawList, pos, det * ctx.Scale, 0xFFFFC060, 0x60);   // light blue
            }
        }

        private static string Describe(in UnitEntry u, int count) =>
            $"{u.DisplayName}{(count > 1 ? $" ×{count}" : "")}{(u.TypeName.Length > 0 ? $" ({u.Name})" : "")}  ·  {OwnerLabel(in u)}  ·  {u.Category.ToString().ToLowerInvariant()}" +
            (count == 1 && u.Resources >= 0 ? $"  ·  {ResourceText(u.Resources)} left" : "") +
            (count == 1 && u.Progress >= 0f ? $"  ·  morphing{(ProductionWidget.MorphTarget(in u) is { } to ? $" into {to}" : "")} {u.Progress * 100f:0}%" : "") +
            (count == 1 && u.QueueProgress >= 0f ? $"  ·  producing{(u.QueueProduct.Length > 0 ? $" {u.QueueProduct}" : "")} {u.QueueProgress * 100f:0}%{(u.QueueCount > 1 ? $" ({u.QueueCount} queued)" : "")}" : "") +
            (count == 1 && u.Range > 0f ? $"  ·  range {u.Range:0.##}" : "") +
            (u.Burrowed ? "  ·  burrowed" : "") + (u.Cloaked ? "  ·  cloaked" : "") +
            (count == 1 && u.MaxShields > 0f && u.IsDamaged ? $"  ·  shields {u.MaxShields - u.ShieldDamage:0}/{u.MaxShields:0}" : "") +
            (count == 1 && u.IsDamaged ? (u.HpFrac >= 0f ? $"  ·  HP {u.MaxHp - u.Damage:0}/{u.MaxHp:0}" : $"  ·  damage taken {u.Damage:0}") : "") +
            (count == 1 && u.Energy >= 0.5f ? (u.MaxEnergy > 0f ? $"  ·  energy {u.Energy:0}/{u.MaxEnergy:0}" : $"  ·  energy {u.Energy:0}") : "");

        private const string BgCtxId = "##radar_bg_ctx";
        private UnitEntry[] _bgUnits = [];

        /// <summary>Where "home" is: the middle of your own buildings (else your units, else everything).</summary>
        private static Vector2 HomePosition(UnitEntry[] units)
        {
            int local = Memory.Instance?.LocalPlayer ?? -1;
            static Vector2? Mid(IEnumerable<UnitEntry> us)
            {
                float sx = 0, sy = 0; int n = 0;
                foreach (var u in us) { sx += u.X; sy += u.Y; n++; }
                return n > 0 ? new Vector2(sx / n, sy / n) : null;
            }
            return (local >= 0 ? Mid(units.Where(u => u.Record != 0 && u.Owner == local && u.IsBuilding)) ?? Mid(units.Where(u => u.Record != 0 && u.Owner == local)) : null)
                   ?? Mid(units) ?? MapCentre();
        }

        private static Vector2 MapCentre() => Memory.Instance?.Terrain is { } t ? new Vector2(t.W * 0.5f, t.H * 0.5f) : new Vector2(128f, 128f);

        /// <summary>Hotkey: centre the view on your own base.</summary>
        public void CenterOnBase() => _panOffset = HomePosition(Memory.Instance?.Snapshot.Units ?? []);

        /// <summary>Hotkey: zoom so the whole map fits.</summary>
        public void FitMapToWindow() => FitMap();

        /// <summary>Zoom so the whole map fits the canvas, centred.</summary>
        private void FitMap()
        {
            if (Memory.Instance?.Terrain is not { } t) return;
            float s = MathF.Min(_canvasSize.X / t.W, _canvasSize.Y / t.H) * 0.95f;   // pixels per map unit
            _zoom = Math.Clamp((int)(s * 2f), 5, 400);
            Config.Zoom = _zoom;
            _panOffset = MapCentre();
        }

        private void DrawBackgroundMenu()
        {
            UI.PushContextMenuStyle(200f, 300f);
            if (ImGuiNET.ImGui.BeginPopup(BgCtxId))
            {
                bool terrain = Memory.Instance?.Terrain != null;
                if (UI.MenuItem("Centre on my base", hint: "The middle of your buildings (your units if you have none)."))
                    _panOffset = HomePosition(_bgUnits);
                if (UI.MenuItem("Centre on the map"))
                    _panOffset = MapCentre();
                if (terrain && UI.MenuItem("Fit the map to the window"))
                    FitMap();
                PoiBackgroundMenuItems();
                UI.MenuSeparator();
                if (UI.MenuItem("Terrain", selected: Config.ShowTerrain, hint: "The map's height map under the units (high ground lighter, cliffs outlined)."))
                { Config.ShowTerrain = !Config.ShowTerrain; Config.SaveDebounced(); }
                if (UI.MenuItem("Creep", selected: Config.ShowCreep, hint: "The creep spread, in the minimap's purple."))
                { Config.ShowCreep = !Config.ShowCreep; Config.SaveDebounced(); }
                if (UI.MenuItem("Pylon power", selected: Config.ShowPowerFields, hint: "The area each Pylon and phasing Warp Prism powers, in its owner's colour."))
                { Config.ShowPowerFields = !Config.ShowPowerFields; Config.SaveDebounced(); }
                if (UI.MenuItem("Labels", selected: Config.ShowLabels, hint: "Unit names once zoomed in."))
                { Config.ShowLabels = !Config.ShowLabels; Config.SaveDebounced(); }
                if (UI.MenuItem("Hide overlapping labels", selected: Config.DeclutterLabels, hint: "Skip a label that would overlap one already drawn, so crowded bases stay readable."))
                { Config.DeclutterLabels = !Config.DeclutterLabels; Config.SaveDebounced(); }
                if (UI.MenuItem("Game clock", selected: Config.ShowClockOverlay, hint: "In-game time and map name in the top-left corner."))
                { Config.ShowClockOverlay = !Config.ShowClockOverlay; Config.SaveDebounced(); }
                if (UI.MenuItem("Production progress", selected: Config.ShowProgress, hint: "A filling ring and percentage on buildings producing units or research (cyan) and anything morphing (amber)."))
                { Config.ShowProgress = !Config.ShowProgress; Config.SaveDebounced(); }
                if (Config.ShowProgress && UI.MenuItem("Progress: name and time left", selected: Config.ProgressDetails,
                        hint: "Over each ring: what it makes and the time left (\"+1 Weapons 0:42\"). Off: the percentage."))
                { Config.ProgressDetails = !Config.ProgressDetails; Config.SaveDebounced(); }
                UI.MenuSeparator();
                if (UI.MenuItem("Range: under the cursor", selected: Config.RangeHovered, hint: "The weapon range of the unit you hover."))
                { Config.RangeHovered = !Config.RangeHovered; Config.SaveDebounced(); }
                if (UI.MenuItem("Range: enemy defences", selected: Config.RangeDefences, hint: "Weapon range rings around enemy buildings that can shoot (cannons, bunkers, spines, ...)."))
                { Config.RangeDefences = !Config.RangeDefences; Config.SaveDebounced(); }
                if (UI.MenuItem("Range: all enemies", selected: Config.RangeEnemies, hint: "Weapon range rings around every armed enemy unit."))
                { Config.RangeEnemies = !Config.RangeEnemies; Config.SaveDebounced(); }
                if (UI.MenuItem("Enemy detection", selected: Config.DetectionRings, hint: "Detection radius of enemy turrets, cannons, spores, observers, overseers and ravens (light blue)."))
                { Config.DetectionRings = !Config.DetectionRings; Config.SaveDebounced(); }
                if (UI.MenuItem("Player cameras", selected: Config.ShowCameras, hint: "Where each other player's camera is looking - their screen on the map."))
                { Config.ShowCameras = !Config.ShowCameras; Config.SaveDebounced(); }
                if (Config.ShowCameras && UI.MenuItem("Include my camera", selected: Config.ShowOwnCamera))
                { Config.ShowOwnCamera = !Config.ShowOwnCamera; Config.SaveDebounced(); }
                if (Config.ShowCameras && UI.MenuItem("Enemies only", selected: Config.CamerasEnemiesOnly, hint: "Hide your allies' camera boxes."))
                { Config.CamerasEnemiesOnly = !Config.CamerasEnemiesOnly; Config.SaveDebounced(); }
                if (Config.ShowCameras && UI.MenuItem("Include computer players", selected: Config.ShowAiCameras, hint: "AI players' cameras too - they rarely move it, so it usually sits on their base."))
                { Config.ShowAiCameras = !Config.ShowAiCameras; Config.SaveDebounced(); }
                ImGuiNET.ImGui.EndPopup();
            }
            UI.PopContextMenuStyle();
        }

        /// <summary>Top-left corner: "12:34  Faster  ·  Map name" (and "paused").</summary>
        private void DrawClockOverlay(ImDrawListPtr dl)
        {
            var mem = Memory.Instance;
            if (!Config.ShowClockOverlay || mem == null || mem.GameTime < 0f) return;
            string text = $"{SC2Memory.FormatClock(mem.GameTime)}{(mem.GamePaused ? "  (paused)" : "")}" +
                          (mem.MapName.Length > 0 ? $"   {mem.MapName}" : "");
            RadarDraw.DrawOutlinedText(dl, _canvasPos + new Vector2(8f, 6f), text, mem.GamePaused ? 0xFF60C0FFu : 0xE6FFFFFFu);
        }

        private void DrawUnitContextMenu()
        {
            UI.PushContextMenuStyle(230f, 360f);
            if (ImGuiNET.ImGui.BeginPopup(UnitCtxId))
            {
                var S = UnitFilter.S;
                for (int gi = 0; gi < _ctxGroups.Count; gi++)
                {
                    var (u, n) = _ctxGroups[gi];
                    if (gi > 0) UI.MenuSeparator();
                    ImGuiNET.ImGui.TextDisabled("  " + Describe(u, n));

                    if (UI.MenuItem($"Hide \"{u.Name}\"##h{gi}", hint: "Hide this exact unit type (Units panel lists hidden types so you can bring them back)."))
                    { S.HiddenNames.Add(u.Name); UnitFilter.SaveDebounced(); ImGuiNET.ImGui.CloseCurrentPopup(); }

                    if (UI.MenuItem($"Blacklist names containing \"{u.Name}\"##b{gi}", hint: "Adds a blacklist pattern - anything whose name or model path contains this text is never drawn."))
                    { UnitFilter.AddToBlacklist(u.Name); ImGuiNET.ImGui.CloseCurrentPopup(); }

                    // family wildcard: "physicsimpactfleshlight" -> "physicsimpact*" (catches flesh/creep/rock/dust... variants)
                    if (FamilyPattern(u.Name) is { } fam &&
                        UI.MenuItem($"Blacklist family \"{fam}\"##bf{gi}", hint: "Wildcard over every name starting with this prefix (all the light/med/creep/rock... variants)."))
                    { UnitFilter.AddToBlacklist(fam); ImGuiNET.ImGui.CloseCurrentPopup(); }

                    if (u.Category == UnitCategory.Effect &&
                        UI.MenuItem($"Hide all effects##be{gi}", hint: "Turns off the Effects category (impacts, missiles, blood, flashes). Units panel > Show > Effects brings them back."))
                    { S.ShowEffects = false; UnitFilter.SaveDebounced(); ImGuiNET.ImGui.CloseCurrentPopup(); }

                    if (UI.MenuItem($"Show only \"{u.Name}\"##o{gi}", hint: "Sets the Units panel search to this name (everything else hidden)."))
                    { S.Search = u.Name; S.DimNonMatches = false; UnitFilter.SaveDebounced(); ImGuiNET.ImGui.CloseCurrentPopup(); }

                    string exact = "=" + u.Name;
                    bool highlighted = UnitFilter.FindHighlight(exact) != null;
                    if (UI.MenuItem(highlighted ? $"Remove highlight##hl{gi}" : $"Highlight \"{u.Name}\"##hl{gi}",
                            hint: "Saved ring around every unit of this type (Units panel > Rules to recolour / switch off)."))
                    {
                        if (highlighted) UnitFilter.RemoveHighlight(exact); else UnitFilter.AddHighlight(exact);
                        ImGuiNET.ImGui.CloseCurrentPopup();
                    }
                    if (FamilyPattern(u.Name) is { } hfam && UnitFilter.FindHighlight(hfam) == null &&
                        UI.MenuItem($"Highlight family \"{hfam}\"##hlf{gi}"))
                    { UnitFilter.AddHighlight(hfam); ImGuiNET.ImGui.CloseCurrentPopup(); }

                    bool focused = UnitFilter.FocusName == u.Name;
                    if (UI.MenuItem(focused ? $"Remove focus##f{gi}" : $"Focus \"{u.Name}\"##f{gi}", hint: "Ring every unit of this type on the radar."))
                    { UnitFilter.FocusName = focused ? null : u.Name; ImGuiNET.ImGui.CloseCurrentPopup(); }

                    if (UI.MenuItem($"Copy name##c{gi}"))
                    { ImGuiNET.ImGui.SetClipboardText(u.Name); ImGuiNET.ImGui.CloseCurrentPopup(); }
                }
                ImGuiNET.ImGui.EndPopup();
            }
            UI.PopContextMenuStyle();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Labels: same text + team within LabelMergePx of each other share one label "name ×n"
        // (a tight group of 9 ghosts otherwise reads as a single "ghost").
        // ─────────────────────────────────────────────────────────────────────

        /// <param name="Priority">Who keeps their label when they overlap: lower first (army, workers, buildings, resources).</param>
        /// <param name="Key">The unit (record, else actor): a stable identity, so the same label wins an overlap every frame.</param>
        /// <param name="R">The marker's radius: the label starts just right of it.</param>
        private readonly record struct PendingLabel(Vector2 Pos, string Text, uint Team, bool Dim, int Priority = 0, ulong Key = 0, float R = 3.5f);
        private readonly List<PendingLabel> _labels = new(512);
        private readonly List<(Vector2 TL, Vector2 BR)> _drawnLabels = new(512);
        // labels drawn last frame: they win ties, so two overlapping labels don't take turns as units shuffle
        private HashSet<ulong> _shownLabels = [], _shownNow = [];
        private readonly HashSet<ulong> _labelSeen = [];
        private const float LabelMergePx = 18f;

        private void DrawGroupedLabels(ImDrawListPtr dl)
        {
            // a fixed order: priority, then whatever was shown last frame, then the unit - so the same label wins an overlap
            // every frame (List.Sort isn't stable and the units come in a different order each pass)
            if (Config.DeclutterLabels)
                _labels.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority)
                                     : _shownLabels.Contains(a.Key) != _shownLabels.Contains(b.Key) ? (_shownLabels.Contains(a.Key) ? -1 : 1)
                                     : a.Key.CompareTo(b.Key));
            // one label per unit: a unit drawn with several actors (models) has the same record and type name on each,
            // and would count twice ("×2" for one locust)
            _labelSeen.Clear();
            _labels.RemoveAll(l => !_labelSeen.Add(l.Key));
            var used = new bool[_labels.Count];
            var members = new List<ulong>();
            for (int i = 0; i < _labels.Count; i++)
            {
                if (used[i]) continue;
                var a = _labels[i];
                members.Clear();
                members.Add(a.Key);
                int n = 1;
                Vector2 sum = a.Pos, top = a.Pos;
                bool allDim = a.Dim;
                for (int j = i + 1; j < _labels.Count; j++)
                {
                    if (used[j]) continue;
                    var b = _labels[j];
                    if (b.Text != a.Text || b.Team != a.Team || Vector2.Distance(a.Pos, b.Pos) > LabelMergePx) continue;
                    used[j] = true; n++; sum += b.Pos; allDim &= b.Dim;
                    members.Add(b.Key);
                    if (b.Pos.Y < top.Y) top = b.Pos;
                }
                // anchor the label at the group's centre horizontally, top unit vertically
                var anchor = n == 1 ? a.Pos : new Vector2(sum.X / n, top.Y);
                string text = n == 1 ? a.Text : $"{a.Text} ×{n}";
                var tl = anchor + new Vector2(a.R + 4f, -8f);
                if (Config.DeclutterLabels)
                {
                    // skip a label that would overlap one already drawn (the first one wins)
                    var br = tl + ImGuiNET.ImGui.CalcTextSize(text) + new Vector2(9f, 2f);
                    bool clash = false;
                    foreach (var (p, q) in _drawnLabels)
                        if (tl.X < q.X && br.X > p.X && tl.Y < q.Y && br.Y > p.Y) { clash = true; break; }
                    if (clash) continue;
                    _drawnLabels.Add((tl, br));
                }
                _shownNow.UnionWith(members);
                RadarDraw.DrawPill(dl, tl, text, 0xF0F0F0F0u, allDim ? 0.3f : 1f);
            }
            _labels.Clear();
            _drawnLabels.Clear();
            (_shownLabels, _shownNow) = (_shownNow, _shownLabels);
            _shownNow.Clear();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Input handling
        // ─────────────────────────────────────────────────────────────────────

        private void HandleInput()
        {
            var io = ImGuiNET.ImGui.GetIO();
            bool hovered = ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows);

            bool mouseInCanvas = io.MousePos.X >= _canvasPos.X &&
                                 io.MousePos.Y >= _canvasPos.Y &&
                                 io.MousePos.X <= _canvasPos.X + _canvasSize.X &&
                                 io.MousePos.Y <= _canvasPos.Y + _canvasSize.Y;

            if (mouseInCanvas && hovered) CursorWorld = (ScreenToWorld(io.MousePos), false);
            else if (CursorWorld is { Stale: false } cw) CursorWorld = (cw.Pos, true);

            if (io.MouseWheel != 0f && mouseInCanvas && hovered)
                AdjustZoom(io.MouseWheel > 0f ? 5 : -5);

            // POI Edit Mode: dragging a marker instead of the map
            if (HandlePoiDrag(hovered && mouseInCanvas)) { _isPanning = false; return; }

            // a drag pans only when it started on the map, and stops when the button is released.
            // The widget-resize-into-canvas case is already blocked by hovered (false while a
            // widget window covers the canvas under the mouse) and IsMouseClicked (false on any
            // frame after the initial press on the widget's edge), so no extra IsAnyItemActive
            // gate is needed here - and that gate was also firing on legitimate fresh canvas
            // clicks, which stopped the map ever starting to pan.
            if (!ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left)) _isPanning = false;
            if (!hovered) return;

            if (ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (!_isPanning)
                {
                    if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) && mouseInCanvas)
                    {
                        _isPanning = true;
                        _lastMouse = io.MousePos;
                    }
                }
                else
                {
                    float scale = _zoom * 0.5f;
                    var delta   = io.MousePos - _lastMouse;
                    _panOffset.X -= delta.X / scale;
                    _panOffset.Y += delta.Y / scale;
                    _lastMouse   = io.MousePos;
                }
            }
            else
            {
                _isPanning = false;
            }

            // Ctrl+RMB: back to the middle of the map
            if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right) && io.KeyCtrl)
                _panOffset = MapCentre();
        }

        // ─────────────────────────────────────────────────────────────────────
        // FPS / Status
        // ─────────────────────────────────────────────────────────────────────

        private void DrawFPS(ImDrawListPtr dl)
        {
            if (_fpsSw.ElapsedMilliseconds >= 1000)
            {
                _fps = System.Threading.Interlocked.Exchange(ref _fpsCounter, 0);
                _fpsSw.Restart();
            }
            else _fpsCounter++;

            string mode = PerfStats.PerfModeActive ? "  · performance mode" : "";
            if (!Config.ShowPerfOverlay)
            {
                var text  = $"{_fps} FPS{mode}";
                var tsize = ImGuiNET.ImGui.CalcTextSize(text);
                dl.AddText(
                    new Vector2(_canvasPos.X + _canvasSize.X - tsize.X - 6f, _canvasPos.Y + 4f),
                    ImGuiStyle.ESPColorText, text);
                return;
            }
            DrawPerfOverlay(dl, mode);
        }

        /// <summary>
        /// Settings ▸ Performance ▸ Performance Overlay: frame rate and frame time, the radar's own draw time, the memory
        /// worker's tick (actor read + memory writes) and how many snapshots arrive a second, with the frame time
        /// (light) and tick time (accent) over the last couple of seconds as a graph.
        /// </summary>
        /// <summary>Units owned by players 1-15 (not scenery, effects or resources): what performance mode's Auto counts.</summary>
        private static int PlayerUnitCount(UnitEntry[] units)
        {
            int n = 0;
            foreach (ref readonly var u in units.AsSpan())
                if (u.Owner is > 0 and < 16 && u.Category is UnitCategory.Unit or UnitCategory.Worker or UnitCategory.Building) n++;
            return n;
        }

        private void DrawPerfOverlay(ImDrawListPtr dl, string mode)
        {
            string[] lines =
            [
                $"{_fps} FPS   frame {PerfStats.FrameMsAvg:0.0} ms{mode}",
                $"radar draw {PerfStats.RadarMs:0.0} ms",
                $"positions {PerfStats.TickMsAvg:0.0} ms ({PerfStats.RealtimeReads} reads)  details {PerfStats.MiscMs:0.0} ms ({PerfStats.MiscReads})",
                $"scans {PerfStats.ScansMs:0.0} ms ({PerfStats.ScansReads})  writes {PerfStats.WritesMs:0.0} ms ({PerfStats.WriteRoundTrips})",
                $"{PerfStats.SnapshotsPerSec} snapshots/s, {PerfStats.Units:N0} player units",
            ];
            const float GraphW = 220f, GraphH = 36f, PadPx = 6f;
            float w = MathF.Max(GraphW, lines.Max(l => ImGuiNET.ImGui.CalcTextSize(l).X));
            float lh = ImGuiNET.ImGui.GetTextLineHeight();
            var size = new Vector2(w + PadPx * 2f, lines.Length * lh + GraphH + PadPx * 3f);
            var tl = new Vector2(_canvasPos.X + _canvasSize.X - size.X - 6f, _canvasPos.Y + 4f);
            dl.AddRectFilled(tl, tl + size, 0xB0101010, 4f);
            for (int i = 0; i < lines.Length; i++)
                dl.AddText(tl + new Vector2(PadPx, PadPx + i * lh), i == 0 ? ImGuiStyle.ESPColorText : 0xC0FFFFFFu, lines[i]);

            // graph: both series on one scale, at least 33 ms tall (30 FPS) so a calm graph stays flat
            var g0 = tl + new Vector2(PadPx, PadPx * 2f + lines.Length * lh);
            dl.AddRectFilled(g0, g0 + new Vector2(w, GraphH), 0x40000000, 2f);
            var frame = PerfStats.FrameHistory; var tick = PerfStats.TickHistory;
            float max = MathF.Max(33f, MathF.Max(frame.Max(), tick.Max()));
            void Series(float[] v, uint col)
            {
                var pts = new Vector2[v.Length];
                for (int i = 0; i < v.Length; i++)
                    pts[i] = g0 + new Vector2(w * i / (v.Length - 1), GraphH - Math.Clamp(v[i] / max, 0f, 1f) * (GraphH - 2f));
                dl.AddPolyline(ref pts[0], pts.Length, col, ImDrawFlags.None, 1.2f);
            }
            Series(frame, 0xA0FFFFFF);
            Series(tick, ImGuiStyle.Accent);
            dl.AddText(g0 + new Vector2(2f, 0f), 0x70FFFFFF, $"{max:0} ms");
        }

        /// <summary>A status card in the middle of the radar (before there is anything to draw).</summary>
        private void DrawStatusString(ImDrawListPtr dl, string text)
        {
            var ts = ImGuiNET.ImGui.CalcTextSize(text);
            var tl = _canvasPos + (_canvasSize - ts) * 0.5f - new Vector2(16f, 10f);
            var br = tl + ts + new Vector2(32f, 20f);
            dl.AddRectFilled(tl, br, ImGuiStyle.WithAlpha(ImGuiStyle.Dark3, 230), 8f);
            dl.AddRect(tl, br, ImGuiStyle.WithAlpha(ImGuiStyle.Border, 160), 8f);
            dl.AddText(tl + new Vector2(16f, 10f), ImGuiStyle.Col(0.78f, 0.78f, 0.78f), text);
        }

        /// <summary>A banner across the top of the radar in <paramref name="col"/>: something we had went away.</summary>
        private void DrawBanner(ImDrawListPtr dl, string text, uint col)
        {
            var ts = ImGuiNET.ImGui.CalcTextSize(text);
            var tl = new Vector2(_canvasPos.X + (_canvasSize.X - ts.X) * 0.5f - 18f, _canvasPos.Y + 14f);
            var br = tl + ts + new Vector2(36f, 14f);
            dl.AddRectFilled(tl, br, ImGuiStyle.WithAlpha(ImGuiStyle.Dark3, 235), 7f);
            dl.AddRect(tl, br, ImGuiStyle.WithAlpha(col, 220), 7f, ImDrawFlags.None, 1.5f);
            dl.AddCircleFilled(tl + new Vector2(11f, (br.Y - tl.Y) * 0.5f), 3.5f, col);
            dl.AddText(tl + new Vector2(22f, 7f), ImGuiStyle.Text, text);
        }

        public void Dispose() { }
    }
}
