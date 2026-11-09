using System.Numerics;
using ImGuiNET;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Panels;

namespace SC2Radar.UI.ImGui
{
    /// <summary>
    /// Points of Interest (the EFT radar's POI panel): groups on the left (click the dot to turn one on / off, right-click
    /// to rename / export / delete), the selected group's map, the selected POI's details and the map's POI list on the
    /// right. Maps are the game's map names; it follows the map being played. POIs are also added from the radar's
    /// right-click menu, and dragged there while Edit Mode is on.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class POIPanel : ChromePanel
    {
        protected override string  PanelTitle => "Points of Interest";
        protected override Vector2 InitSize   => new(760f, 560f);
        protected override ImGuiWindowFlags ChromeScrollExtraFlags => ImGuiWindowFlags.NoScrollbar;
        protected override bool HasPanelMenu => true;

        // group list / map / POI selection
        int    _groupIdx = -1, _renameIdx = -1, _poiIdx = -1;
        string _renameBuffer = "", _selectedMap = "", _filter = "", _lastLiveMap = "";

        // import modal
        bool   _importOpen, _importMerge = true;
        string _importJson = "", _importError = "";

        /// <summary>While on, POI markers on the radar can be dragged to new spots.</summary>
        public static bool EditMode { get; set; }

        /// <summary>The group the radar's "Add point of interest here" adds to (the one selected here).</summary>
        public static POIGroup? TargetGroup { get; private set; }

        /// <summary>Set by the radar: open the panel on this POI. MenuManager shows the panel; the next draw selects it.</summary>
        public static POIEntry? SelectRequest { get; set; }
        public static bool OpenRequested { get; set; }

        const float LeftW = 200f, Gap = 6f, BtnH = 26f, RowH = 28f, LabelW = 90f, InputH = 30f, NumH = 30f;

        static string LiveMap => Memory.InGame ? Memory.Instance?.MapName ?? "" : "";

        POIGroup? SelectedGroup => _groupIdx >= 0 && _groupIdx < PoiManager.Groups.Count ? PoiManager.Groups[_groupIdx] : null;

        // ── Panel menu ───────────────────────────────────────────────────────

        protected override void DrawPanelMenu()
        {
            var c = Program.Config;
            if (UI.MenuItem("Show on radar", selected: c.ShowPOIs)) { c.ShowPOIs = !c.ShowPOIs; c.SaveDebounced(); }
            if (UI.MenuItem("Names on radar", selected: c.POILabels, hint: "Each POI's name under its marker.")) { c.POILabels = !c.POILabels; c.SaveDebounced(); }
            UI.MenuSeparator();
            var group = SelectedGroup;
            if (UI.ContextMenuRow("##pm_expgrp", "Export Selected Group", group is { TotalCount: > 0 })) ExportGroup(group!);
            if (UI.ContextMenuRow("##pm_expall", "Export All Groups", PoiManager.Groups.Any(g => g.TotalCount > 0))) ExportAll();
            UI.MenuSeparator();
            if (UI.ContextMenuRow("##pm_import", "Import...")) { _importOpen = true; _importJson = ""; _importError = ""; }
        }

        static void ExportGroup(POIGroup group)
        {
            ImGuiNET.ImGui.SetClipboardText(PoiManager.ExportJson(group));
            ToastManager.Show("Exported", $"\"{group.Name}\" ({group.TotalCount} POI{(group.TotalCount == 1 ? "" : "s")}) copied to the clipboard.", ToastStyle.Success);
        }

        static void ExportAll()
        {
            var groups = PoiManager.Groups;
            ImGuiNET.ImGui.SetClipboardText(PoiManager.ExportJson(groups));
            int total = groups.Sum(g => g.TotalCount);
            ToastManager.Show("Exported", $"{total} POI{(total == 1 ? "" : "s")} in {groups.Count} group{(groups.Count == 1 ? "" : "s")} copied to the clipboard.", ToastStyle.Success);
        }

        // ── Content ──────────────────────────────────────────────────────────

        protected override void DrawPanelContent()
        {
            DrawImportModal();

            if ((_groupIdx < 0 || _groupIdx >= PoiManager.Groups.Count) && PoiManager.Groups.Count > 0) SelectGroup(0);
            TargetGroup = SelectedGroup;

            // follow the map being played (before a radar pick, which must win)
            string live = LiveMap;
            if (live.Length > 0 && live != _lastLiveMap) { _lastLiveMap = live; SetSelectedMap(live); }

            // a POI picked on the radar: its group, map and row
            var want = SelectRequest;
            SelectRequest = null;
            if (want != null && PoiManager.Owner(want) is { } o)
            {
                SelectGroup(PoiManager.Groups.IndexOf(o.Group));
                SetSelectedMap(o.Map);
                _poiIdx = o.Group.Maps[o.Map].IndexOf(want);
            }

            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() - 13f);   // flush with the title divider, as EFT
            float h = ImGuiNET.ImGui.GetContentRegionAvail().Y;

            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGuiNET.ImGui.BeginChild("##poi_left", new Vector2(LeftW, h), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar);
            ImGuiNET.ImGui.PopStyleVar();
            DrawGroupList();
            ImGuiNET.ImGui.EndChild();

            ImGuiNET.ImGui.SameLine(0f, 0f);
            var divP = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.GetWindowDrawList().AddRectFilled(divP, new Vector2(divP.X + 1f, divP.Y + h), UI.Alpha(ImGuiStyle.Border, 70));
            ImGuiNET.ImGui.Dummy(new Vector2(1f, h));

            ImGuiNET.ImGui.SameLine(0f, 0f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 0f));
            ImGuiNET.ImGui.BeginChild("##poi_right", new Vector2(-1f, h), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar);
            ImGuiNET.ImGui.PopStyleVar();
            if (SelectedGroup != null) DrawRightPane(live);
            else UI.Empty("Create a POI group with the\n+ New Group button on the left.");
            ImGuiNET.ImGui.EndChild();
        }

        // ── Group list (left) ────────────────────────────────────────────────

        void DrawGroupList()
        {
            var groups = PoiManager.Groups;
            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            var font = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

            ImGuiNET.ImGui.SetCursorPos(new Vector2(8f, UI.GroupTitleClearance));
            if (UI.Button("+ New Group##poinewgrp", new Vector2(LeftW - 16f, 28f), ButtonStyle.Accent)) AddGroup();
            UI.Tooltip("Create a new empty POI group.");
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + 4f);
            UI.Separator();
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + 2f);

            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGuiNET.ImGui.BeginChild("##poi_grplist", new Vector2(LeftW, ImGuiNET.ImGui.GetContentRegionAvail().Y), ImGuiChildFlags.None);
            ImGuiNET.ImGui.PopStyleVar();
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

            for (int i = 0; i < groups.Count; i++)
            {
                var grp = groups[i];
                bool sel = _groupIdx == i;
                var rowTL = ImGuiNET.ImGui.GetCursorScreenPos();

                // count pill, then the on / off dot left of it
                string cnt = grp.TotalCount.ToString();
                var cntSz = font.CalcTextSizeA(fsize, float.MaxValue, -1f, cnt);
                float bW = cntSz.X + 12f, bH = fsize + 4f, bR = bH * 0.5f;
                float trashLeft = rowTL.X + LeftW - BtnH;
                float bX = trashLeft - 8f - bW, bY = rowTL.Y + (RowH - bH) * 0.5f;
                float dotCX = bX - 12f;

                ImGuiNET.ImGui.InvisibleButton($"##poigrp_{i}", new Vector2(LeftW - BtnH, RowH));
                uint id = ImGuiNET.ImGui.GetItemID();
                bool hov = ImGuiNET.ImGui.IsItemHovered();
                if (ImGuiNET.ImGui.IsItemClicked())
                {
                    if (ImGuiNET.ImGui.GetIO().MouseClickedPos[0].X >= dotCX - 8f) { grp.Enabled = !grp.Enabled; PoiManager.SaveDebounced(); }
                    else SelectGroup(i);
                }
                bool dotHov = hov && ImGuiNET.ImGui.GetIO().MousePos.X >= dotCX - 8f;
                if (dotHov)
                {
                    ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    UI.Tooltip(grp.Enabled ? "On - click to turn off" : "Off - click to turn on");
                }

                if (hov && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right)) ImGuiNET.ImGui.OpenPopup($"##poigrpctx_{i}");
                UI.PushContextMenuStyle();
                if (ImGuiNET.ImGui.BeginPopup($"##poigrpctx_{i}"))
                {
                    if (UI.ContextMenuRow($"##ctx_ren_{i}", "Rename")) { SelectGroup(i); _renameIdx = i; _renameBuffer = grp.Name; }
                    if (UI.ContextMenuRow($"##ctx_exp_{i}", "Export", grp.TotalCount > 0)) ExportGroup(grp);
                    UI.MenuSeparator();
                    if (UI.ContextMenuRow($"##ctx_del_{i}", "Delete")) ConfirmDeleteGroup(grp);
                    ImGuiNET.ImGui.EndPopup();
                }
                UI.PopContextMenuStyle();

                if (sel)
                {
                    dl.AddRectFilled(rowTL, new Vector2(rowTL.X + LeftW, rowTL.Y + RowH), UI.Alpha(ImGuiStyle.Accent, 28));
                    dl.AddRectFilled(rowTL, new Vector2(rowTL.X + 3f, rowTL.Y + RowH), ImGuiStyle.Accent);
                }
                else if (hov) dl.AddRectFilled(rowTL, new Vector2(rowTL.X + LeftW, rowTL.Y + RowH), UI.Alpha(0xFFFFFFFF, 8));

                float nameX = rowTL.X + 8f;
                if (_renameIdx == i)
                {
                    ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(nameX, rowTL.Y + (RowH - 22f) * 0.5f));
                    ImGuiNET.ImGui.SetKeyboardFocusHere();
                    UI.InputText($"##poiren_{i}", ref _renameBuffer, 128, width: dotCX - 8f - nameX);
                    if (ImGuiNET.ImGui.IsItemDeactivated())
                    {
                        if (_renameBuffer.Trim() is { Length: > 0 } t) { grp.Name = t; PoiManager.SaveDebounced(); }
                        _renameIdx = -1;
                    }
                }
                else
                {
                    uint nameCol = sel ? 0xFFFFFFFF : hov ? UI.Alpha(0xFFFFFFFF, 230) : UI.Alpha(0xFFFFFFFF, 190);
                    dl.PushClipRect(new Vector2(nameX, rowTL.Y), new Vector2(dotCX - 8f, rowTL.Y + RowH), true);
                    dl.AddText(font, fsize, new Vector2(nameX, rowTL.Y + (RowH - fsize) * 0.5f), nameCol, grp.Name);
                    dl.PopClipRect();
                    StateDot(dl, new Vector2(dotCX, rowTL.Y + RowH * 0.5f), grp.Enabled, UI.Anim(id ^ 0xD0u, dotHov ? 1f : 0f, 12f));
                    dl.AddRectFilled(new Vector2(bX, bY), new Vector2(bX + bW, bY + bH), UI.Alpha(sel ? ImGuiStyle.Accent : ImGuiStyle.Dark3, (byte)(sel ? 55 : 140)), bR);
                    dl.AddRect(new Vector2(bX, bY), new Vector2(bX + bW, bY + bH), UI.Alpha(sel ? ImGuiStyle.Accent : ImGuiStyle.Border, (byte)(sel ? 130 : 65)), bR, ImDrawFlags.None, 1f);
                    dl.AddText(font, fsize, new Vector2(bX + 6f, bY + 2f), UI.Alpha(0xFFFFFFFF, (byte)(sel ? 220 : 170)), cnt);
                }

                dl.AddLine(new Vector2(rowTL.X + 8f, rowTL.Y + RowH - 0.5f), new Vector2(rowTL.X + LeftW - 8f, rowTL.Y + RowH - 0.5f), UI.Alpha(ImGuiStyle.Border, 35));

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(trashLeft, rowTL.Y + (RowH - BtnH) * 0.5f));
                if (UI.IconButton($"##poigrptrash_{i}", IconCache.Icon.Trash, w: BtnH, h: BtnH, style: ButtonStyle.Danger)) ConfirmDeleteGroup(grp);
                UI.Tooltip("Delete group");
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rowTL.X, rowTL.Y + RowH));
            }
            if (groups.Count == 0) UI.Empty("No groups yet. Click + New Group to start.");

            ImGuiNET.ImGui.PopStyleVar();
            ImGuiNET.ImGui.EndChild();
        }

        /// <summary>The green / red on-off dot, with a soft ring while its click zone is hovered.</summary>
        static void StateDot(ImDrawListPtr dl, Vector2 c, bool on, float hover)
        {
            uint col = on ? ImGuiStyle.Col(0x4C, 0xAF, 0x50) : ImGuiStyle.Col(0xF4, 0x43, 0x36);
            if (hover > 0.01f)
            {
                dl.AddCircleFilled(c, 8f, UI.Alpha(col, (byte)(45 * hover)));
                dl.AddCircle(c, 5.5f, UI.Alpha(col, (byte)(160 * hover)), 0, 1.5f);
            }
            dl.AddCircleFilled(c, 4f, col);
        }

        void AddGroup()
        {
            var g = new POIGroup();
            PoiManager.Groups.Add(g);
            PoiManager.SaveDebounced();
            SelectGroup(PoiManager.Groups.Count - 1);
            (_renameIdx, _renameBuffer) = (PoiManager.Groups.Count - 1, g.Name);
        }

        void ConfirmDeleteGroup(POIGroup grp) =>
            ConfirmModal.Open("Delete Group", $"Delete group \"{grp.Name}\" and all its POIs?", () =>
            {
                // the callback runs a frame or more later: find the group again
                int idx = PoiManager.Groups.IndexOf(grp);
                if (idx < 0) return;
                PoiManager.Groups.RemoveAt(idx);
                _groupIdx = Math.Min(_groupIdx, PoiManager.Groups.Count - 1);
                if (_groupIdx >= 0) SelectGroup(_groupIdx);
                PoiManager.SaveDebounced();
            }, confirmLabel: "Delete");

        // ── Right pane ───────────────────────────────────────────────────────

        void DrawRightPane(string live)
        {
            var group = SelectedGroup!;
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + UI.GroupTitleClearance);
            UI.BeginGroup("Group");
            float gw = UI.GroupInnerWidth;
            string gname = group.Name;
            if (UI.InputText("##poigrpname", ref gname, 128, width: gw, hint: "The display name for this POI group."))
            { group.Name = gname; PoiManager.SaveDebounced(); }
            ImGuiNET.ImGui.Spacing();
            DrawMapRow(group, live, gw);
            UI.EndGroup();

            DrawEditor(group);

            float avail = ImGuiNET.ImGui.GetContentRegionAvail().Y;
            float boxBottom = ImGuiNET.ImGui.GetCursorScreenPos().Y + avail;
            UI.BeginGroup("POI List");
            DrawPoiList(group, Math.Max(80f, avail - 84f), UI.GroupInnerWidth);
            UI.EndGroup(boxBottom);
        }

        void DrawMapRow(POIGroup group, string live, float rw)
        {
            // the map being played first, then every map the group has POIs on
            var maps = new List<string>();
            if (live.Length > 0) maps.Add(live);
            foreach (var m in group.Maps.Keys.OrderBy(k => k))
                if (!maps.Contains(m, StringComparer.OrdinalIgnoreCase)) maps.Add(m);
            if (maps.Count == 0)
            {
                UI.Caption("Start a match to add POIs for its map (or import a group).");
                return;
            }
            if (!maps.Contains(_selectedMap, StringComparer.OrdinalIgnoreCase)) SetSelectedMap(maps[0]);

            string[] names = [.. maps.Select(m =>
            {
                int n = group.CountForMap(m);
                string s = m == live ? $"{m} (playing)" : m;
                return n > 0 ? $"{s}  ({n})" : s;
            })];
            int idx = Math.Max(0, maps.FindIndex(m => m.Equals(_selectedMap, StringComparison.OrdinalIgnoreCase)));

            float rowY = ImGuiNET.ImGui.GetCursorPosY();
            if (UI.Combo("##poiMapCombo", ref idx, names, rw * 0.45f, localizeItems: false)) SetSelectedMap(maps[idx]);
            UI.Tooltip("Which map's POI list to view and edit.");

            float toggleY = rowY + (30f - (MathF.Max(16f, ImGuiNET.ImGui.GetTextLineHeight()) + 4f)) * 0.5f;
            ImGuiNET.ImGui.SameLine(0f, Gap);
            ImGuiNET.ImGui.SetCursorPosY(toggleY);
            bool mapOn = group.IsMapEnabled(_selectedMap);
            if (UI.Toggle("Enable Map##poiMapEn", ref mapOn, 110f)) { group.ToggleMapEnabled(_selectedMap); PoiManager.SaveDebounced(); }
            UI.Tooltip("Show this group's POIs on this map.");

            ImGuiNET.ImGui.SameLine(0f, Gap);
            ImGuiNET.ImGui.SetCursorPosY(toggleY);
            bool em = EditMode;
            if (UI.Toggle("Edit Mode##poiEM", ref em, 110f)) EditMode = em;
            UI.Tooltip("While on, POI markers can be dragged on the radar to new spots.");
        }

        void DrawEditor(POIGroup group)
        {
            var pois = _selectedMap.Length == 0 ? null : group.GetOrCreateMapList(_selectedMap);
            var poi = pois != null && _poiIdx >= 0 && _poiIdx < pois.Count ? pois[_poiIdx] : null;

            UI.BeginGroup("Details", disabled: poi == null, disabledReason: "Select a POI from the list to edit it");
            float gw = UI.GroupInnerWidth;
            string nm = poi?.Name ?? "";
            if (LabeledText("Name", ref nm, 64, gw) && poi != null) { poi.Name = nm; PoiManager.SaveDebounced(); }
            string desc = poi?.Description ?? "";
            if (LabeledText("Description", ref desc, 512, gw) && poi != null) { poi.Description = desc; PoiManager.SaveDebounced(); }
            ImGuiNET.ImGui.Spacing();

            float half = (gw - Gap) / 2f;
            float px = poi?.X ?? 0f, py = poi?.Y ?? 0f;
            var r0 = ImGuiNET.ImGui.GetCursorScreenPos();
            if (UI.InputFloat("X##poiX", ref px, step: 0.5f, fmt: "F1", width: half) && poi != null) { poi.X = px; PoiManager.SaveDebounced(); }
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(r0.X + half + Gap, r0.Y));
            if (UI.InputFloat("Y##poiY", ref py, step: 0.5f, fmt: "F1", width: half) && poi != null) { poi.Y = py; PoiManager.SaveDebounced(); }
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(r0.X, r0.Y + NumH));
            ImGuiNET.ImGui.Dummy(new Vector2(gw, 0f));
            UI.EndGroup();
        }

        static bool LabeledText(string label, ref string value, uint maxLen, float rowW)
        {
            var font = ImGuiStyle.FontNormal;
            float fs = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            var p0 = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.GetWindowDrawList().AddText(font, fs, new Vector2(p0.X, p0.Y + (InputH - fs) * 0.5f), UI.Alpha(0xFFFFFFFF, 130), LocManager.Get(label));
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p0.X + LabelW, p0.Y));
            bool changed = UI.InputText($"##poifld_{label}", ref value, maxLen, width: rowW - LabelW);
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p0.X, ImGuiNET.ImGui.GetCursorScreenPos().Y + 4f));
            return changed;
        }

        void DrawPoiList(POIGroup group, float scrollH, float rw)
        {
            var pois = _selectedMap.Length == 0 ? null : group.GetOrCreateMapList(_selectedMap);
            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            var font = ImGuiStyle.FontNormal;
            float fsize = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

            UI.InputText("##poiFilter", ref _filter, 64, placeholder: "Filter by name...", width: rw - Gap - BtnH, hint: "Filter the list by name.");
            ImGuiNET.ImGui.SameLine(0f, Gap);
            if (pois == null) UI.BeginDisabled("Select a map to add a POI");
            if (UI.IconButton("##poiAdd", IconCache.Icon.Plus, w: BtnH, iconSz: 16f, style: ButtonStyle.Accent) && pois != null)
            {
                // the middle of the radar view - drag it from there in Edit Mode, or right-click the radar to add on the spot
                var at = ImGuiApp.Radar?.ViewCentre ?? Vector2.Zero;
                pois.Add(new POIEntry { X = at.X, Y = at.Y });
                _poiIdx = pois.Count - 1;
                PoiManager.SaveDebounced();
            }
            if (pois == null) UI.EndDisabled();
            UI.Tooltip("Add a POI at the middle of the radar view (or right-click the radar: Add point of interest here).");
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + 3f);

            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGuiNET.ImGui.BeginChild("##poi_scroll", new Vector2(rw, scrollH), ImGuiChildFlags.None);
            ImGuiNET.ImGui.PopStyleVar();
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

            if (pois == null)
            {
                UI.Empty("Select a map above to manage its POIs.");
                ImGuiNET.ImGui.PopStyleVar();
                ImGuiNET.ImGui.EndChild();
                return;
            }

            bool filtering = !string.IsNullOrWhiteSpace(_filter);
            uint poiCol = Program.Config.POIColor;
            int shown = 0;
            for (int i = 0; i < pois.Count; i++)
            {
                var poi = pois[i];
                if (filtering && !poi.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
                shown++;
                bool sel = _poiIdx == i;
                var rowTL = ImGuiNET.ImGui.GetCursorScreenPos();
                float trashLeft = rowTL.X + rw - BtnH, dotCX = trashLeft - 20f;

                ImGuiNET.ImGui.InvisibleButton($"##poirow_{i}", new Vector2(rw - BtnH, RowH));
                uint id = ImGuiNET.ImGui.GetItemID();
                bool hov = ImGuiNET.ImGui.IsItemHovered();
                if (ImGuiNET.ImGui.IsItemClicked())
                {
                    if (ImGuiNET.ImGui.GetIO().MouseClickedPos[0].X >= dotCX - 8f) { poi.Enabled = !poi.Enabled; PoiManager.SaveDebounced(); }
                    else _poiIdx = i;
                }
                if (hov && ImGuiNET.ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) ImGuiApp.Radar?.CentreOn(new Vector2(poi.X, poi.Y));
                if (hov && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right)) { _poiIdx = i; ImGuiNET.ImGui.OpenPopup($"##poictx_{i}"); }
                UI.PushContextMenuStyle();
                if (ImGuiNET.ImGui.BeginPopup($"##poictx_{i}"))
                {
                    if (UI.ContextMenuRow($"##ctx_ctr_{i}", "Centre the radar on it")) ImGuiApp.Radar?.CentreOn(new Vector2(poi.X, poi.Y));
                    UI.MenuSeparator();
                    if (UI.ContextMenuRow($"##ctx_del_{i}", "Delete")) ConfirmDeletePoi(pois, poi);
                    ImGuiNET.ImGui.EndPopup();
                }
                UI.PopContextMenuStyle();

                bool dotHov = hov && ImGuiNET.ImGui.GetIO().MousePos.X >= dotCX - 8f;
                if (dotHov)
                {
                    ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    UI.Tooltip(poi.Enabled ? "On - click to turn off" : "Off - click to turn on");
                }
                else if (hov) UI.Tooltip("Double-click to centre the radar on it.");

                if (sel)
                {
                    dl.AddRectFilled(rowTL, new Vector2(rowTL.X + rw, rowTL.Y + RowH), UI.Alpha(ImGuiStyle.Accent, 28));
                    dl.AddRectFilled(rowTL, new Vector2(rowTL.X + 3f, rowTL.Y + RowH), ImGuiStyle.Accent);
                }
                else if (hov) dl.AddRectFilled(rowTL, new Vector2(rowTL.X + rw, rowTL.Y + RowH), UI.Alpha(0xFFFFFFFF, 8));

                float cy = rowTL.Y + RowH * 0.5f, cx = rowTL.X + 10f;
                Radar.RadarDraw.DrawMarker(dl, new Vector2(cx, cy), Program.Config.POIShape, 4.5f, poiCol, outline: true, shadow: false);

                float nameX = cx + 11f;
                uint nameCol = sel ? 0xFFFFFFFF : hov ? UI.Alpha(0xFFFFFFFF, 230) : UI.Alpha(0xFFFFFFFF, 190);
                dl.PushClipRect(new Vector2(nameX, rowTL.Y), new Vector2(dotCX - 12f, rowTL.Y + RowH), true);
                dl.AddText(font, fsize, new Vector2(nameX, rowTL.Y + (RowH - fsize) * 0.5f), nameCol, poi.Name);
                dl.PopClipRect();
                StateDot(dl, new Vector2(dotCX, cy), poi.Enabled, UI.Anim(id ^ 0xD0u, dotHov ? 1f : 0f, 12f));

                dl.AddLine(new Vector2(rowTL.X + 8f, rowTL.Y + RowH - 0.5f), new Vector2(rowTL.X + rw - 8f, rowTL.Y + RowH - 0.5f), UI.Alpha(ImGuiStyle.Border, 35));

                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(trashLeft, rowTL.Y + (RowH - BtnH) * 0.5f));
                if (UI.IconButton($"##poitrash_{i}", IconCache.Icon.Trash, w: BtnH, h: BtnH, style: ButtonStyle.Danger)) ConfirmDeletePoi(pois, poi);
                UI.Tooltip("Delete POI");
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rowTL.X, rowTL.Y + RowH));
            }
            if (shown == 0) UI.Empty(filtering ? "No matching POIs." : "No POIs on this map yet - press + or right-click the radar.");

            ImGuiNET.ImGui.PopStyleVar();
            ImGuiNET.ImGui.EndChild();
        }

        void ConfirmDeletePoi(List<POIEntry> pois, POIEntry poi) =>
            ConfirmModal.Open("Delete POI", $"Delete \"{poi.Name}\"?", () =>
            {
                int idx = pois.IndexOf(poi);
                if (idx < 0) return;
                pois.RemoveAt(idx);
                if (_poiIdx == idx) _poiIdx = pois.Count > 0 ? Math.Min(idx, pois.Count - 1) : -1;
                else if (_poiIdx > idx) _poiIdx--;
                PoiManager.SaveDebounced();
            }, confirmLabel: "Delete");

        // ── Import ───────────────────────────────────────────────────────────

        void DrawImportModal()
        {
            if (_importOpen) ImGuiNET.ImGui.OpenPopup("##poiimpmod");
            _importOpen = false;
            if (!UI.BeginModal("##poiimpmod", 560f)) return;

            float avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
            UI.ModalTitle("Import POI Groups");
            UI.Caption("Paste JSON exported from this panel (one group, or an array of groups):");
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 4f));
            UI.InputTextMultiline("##poiimpjson", ref _importJson, 131072, height: 200f);
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 6f));
            UI.Toggle("Merge into existing groups##poimgr", ref _importMerge);
            UI.Caption("When off, every existing group is replaced with the imported ones.");
            if (_importError.Length > 0)
            {
                ImGuiNET.ImGui.Dummy(new Vector2(avail, 4f));
                UI.Notice(_importError, NoticeStyle.Danger);
            }
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 4f));

            if (UI.Button("Import##poiimp", new Vector2(80f, BtnH), ButtonStyle.Accent))
            {
                _importError = PoiManager.Import(_importJson, _importMerge);
                if (_importError.Length == 0) ImGuiNET.ImGui.CloseCurrentPopup();
            }
            ImGuiNET.ImGui.SameLine(0f, 8f);
            if (UI.Button("Paste from Clipboard##poipst", new Vector2(170f, BtnH), ButtonStyle.Ghost))
                _importJson = ImGuiNET.ImGui.GetClipboardText() ?? "";
            ImGuiNET.ImGui.SameLine(0f, 8f);
            if (UI.Button("Cancel##poiimpc", new Vector2(80f, BtnH), ButtonStyle.Ghost)) ImGuiNET.ImGui.CloseCurrentPopup();
            UI.EndModal();
        }

        // ── Selection ────────────────────────────────────────────────────────

        void SelectGroup(int idx)
        {
            (_groupIdx, _poiIdx, _filter) = (idx, -1, "");
            if (SelectedGroup is not { } g) return;
            string live = LiveMap;
            SetSelectedMap(live.Length > 0 ? live : g.Maps.Keys.FirstOrDefault() ?? "");
        }

        void SetSelectedMap(string map)
        {
            if (map.Equals(_selectedMap, StringComparison.OrdinalIgnoreCase)) return;
            (_selectedMap, _poiIdx, _filter) = (map, -1, "");
        }
    }
}
