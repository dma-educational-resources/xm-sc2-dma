using System.Numerics;
using ImGuiNET;
using SC2Radar.DMA;
using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Points of interest on the radar (as the EFT radar): the enabled POIs of the map being played, a hover card with
    /// the name and description, a right-click menu (edit / centre / delete), "Add point of interest here" on the empty-map
    /// menu, and dragging while the POI panel's Edit Mode is on.
    /// </summary>
    public sealed partial class RadarWindow
    {
        private readonly List<(Vector2 Pos, POIEntry Poi)> _poiHits = [];
        private POIEntry? _poiDragging, _ctxPoi;
        private Vector2 _bgWorld;   // where the empty-map menu was opened (map units)
        private const string PoiCtxId = "##radar_poi_ctx";

        /// <summary>The map point at the middle of the radar view.</summary>
        public Vector2 ViewCentre => _panOffset;

        /// <summary>Map point under the mouse while it is over the radar; else the last one (Stale = true), null before any.</summary>
        public (Vector2 Pos, bool Stale)? CursorWorld { get; private set; }

        /// <summary>Moves the view so <paramref name="world"/> is in the middle.</summary>
        public void CentreOn(Vector2 world) => _panOffset = world;

        /// <summary>Screen pixels to map units (the inverse of RadarFrameCtx.WorldToScreen).</summary>
        private Vector2 ScreenToWorld(Vector2 s)
        {
            float scale = _zoom * 0.5f;
            return new Vector2(_panOffset.X + (s.X - _canvasPos.X - _canvasSize.X * 0.5f) / scale,
                               _panOffset.Y - (s.Y - _canvasPos.Y - _canvasSize.Y * 0.5f) / scale);
        }

        private void DrawPOIs(in RadarFrameCtx ctx)
        {
            _poiHits.Clear();
            string map = Memory.Instance?.MapName ?? "";
            if (!Config.ShowPOIs || map.Length == 0) return;

            var dl = ctx.DrawList;
            float r = Config.POISize;
            uint col = Config.POIColor;
            foreach (var poi in PoiManager.EnabledForMap(map))
            {
                var pos = ctx.WorldToScreen(poi.X, poi.Y);
                if (pos.X < _canvasPos.X - 40 || pos.X > _canvasPos.X + _canvasSize.X + 40 ||
                    pos.Y < _canvasPos.Y - 40 || pos.Y > _canvasPos.Y + _canvasSize.Y + 40) continue;
                bool dragging = ReferenceEquals(poi, _poiDragging);
                if (POIPanel.EditMode) RadarDraw.DrawGlow(dl, pos, r, col);   // draggable
                RadarDraw.DrawMarker(dl, pos, Config.POIShape, dragging ? r * 1.25f : r, col, outline: true);
                if (Config.POILabels && poi.Name.Length > 0)
                {
                    var ts = ImGuiNET.ImGui.CalcTextSize(poi.Name);
                    RadarDraw.DrawPill(dl, pos + new Vector2(-ts.X * 0.5f - 4f, r + 3f), poi.Name, col);
                }
                _poiHits.Add((pos, poi));
            }
        }

        private POIEntry? PoiUnderMouse()
        {
            var mp = ImGuiNET.ImGui.GetIO().MousePos;
            float hit = MathF.Max(HitPx, Config.POISize + 3f);
            POIEntry? best = null;
            float bestSq = hit * hit;
            foreach (var (pos, poi) in _poiHits)
                if (Vector2.DistanceSquared(pos, mp) is var d && d <= bestSq) { bestSq = d; best = poi; }
            return best;
        }

        /// <summary>Hover card and right-click for the POI under the cursor; false when there is none.</summary>
        private bool HandlePoiHover()
        {
            if (_poiDragging != null) return true;
            if (PoiUnderMouse() is not { } poi) return false;

            ImGuiNET.ImGui.BeginTooltip();
            ImGuiNET.ImGui.TextColored(UI.ToV4(Config.POIColor), poi.Name.Length > 0 ? poi.Name : "(unnamed)");
            if (!string.IsNullOrWhiteSpace(poi.Description))
            {
                ImGuiNET.ImGui.Separator();
                ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + 240f);
                ImGuiNET.ImGui.TextUnformatted(poi.Description);
                ImGuiNET.ImGui.PopTextWrapPos();
            }
            if (PoiManager.Owner(poi) is { } o) ImGuiNET.ImGui.TextDisabled(o.Group.Name);
            ImGuiNET.ImGui.EndTooltip();

            if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right) && !ImGuiNET.ImGui.GetIO().KeyCtrl)
            {
                _ctxPoi = poi;
                ImGuiNET.ImGui.OpenPopup(PoiCtxId);
            }
            return true;
        }

        private void DrawPoiContextMenu()
        {
            UI.PushContextMenuStyle(200f, 300f);
            if (ImGuiNET.ImGui.BeginPopup(PoiCtxId))
            {
                if (_ctxPoi is { } poi)
                {
                    ImGuiNET.ImGui.TextDisabled(poi.Name.Length > 0 ? poi.Name : "(unnamed)");
                    UI.MenuSeparator();
                    if (UI.MenuItem("Edit in Points of Interest"))
                    { POIPanel.SelectRequest = poi; POIPanel.OpenRequested = true; }
                    if (UI.MenuItem("Centre the radar on it")) CentreOn(new Vector2(poi.X, poi.Y));
                    if (UI.MenuItem("Edit Mode (drag to move)", selected: POIPanel.EditMode)) POIPanel.EditMode = !POIPanel.EditMode;
                    UI.MenuSeparator();
                    if (UI.MenuItem("Delete")) { PoiManager.Remove(poi); _ctxPoi = null; }
                }
                ImGuiNET.ImGui.EndPopup();
            }
            UI.PopContextMenuStyle();
        }

        /// <summary>The empty-map menu's POI item: a new POI where the menu was opened, in the panel's selected group.</summary>
        private void PoiBackgroundMenuItems()
        {
            string map = Memory.Instance?.MapName ?? "";
            if (map.Length == 0) return;
            if (!UI.MenuItem("Add point of interest here", hint: "A marker at this spot on this map - name it in the Points of Interest panel.")) return;

            var group = POIPanel.TargetGroup ?? PoiManager.Groups.FirstOrDefault();
            if (group == null) { group = new POIGroup { Name = "My Points" }; PoiManager.Groups.Add(group); }
            var poi = new POIEntry { Name = $"POI {group.CountForMap(map) + 1}", X = _bgWorld.X, Y = _bgWorld.Y };
            group.GetOrCreateMapList(map).Add(poi);
            PoiManager.SaveDebounced();
            POIPanel.SelectRequest = poi;
            POIPanel.OpenRequested = true;
        }

        /// <summary>Edit Mode: a press on a POI drags it (instead of panning). True while a POI is being dragged.</summary>
        private bool HandlePoiDrag(bool hovered)
        {
            if (!POIPanel.EditMode) { _poiDragging = null; return false; }
            if (_poiDragging != null)
            {
                if (ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    var w = ScreenToWorld(ImGuiNET.ImGui.GetIO().MousePos);
                    (_poiDragging.X, _poiDragging.Y) = (MathF.Round(w.X, 1), MathF.Round(w.Y, 1));
                    return true;
                }
                _poiDragging = null;
                PoiManager.SaveDebounced();
                return false;
            }
            if (hovered && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) && PoiUnderMouse() is { } poi)
            {
                _poiDragging = poi;
                return true;
            }
            return false;
        }
    }
}
