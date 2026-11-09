using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace SC2Radar.UI.ImGui.Panels
{
    /// <summary>
    /// Floating search panel that lets the user find any UI control by keyword.
    /// Clicking a result opens its parent panel, navigates to its tab, and starts
    /// a pulsing highlight animation on the control.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class SearchPanel : ChromePanel
    {
        #region Chrome Overrides

        protected override Vector2 InitSize   => new(420f, 480f);
        protected override string  PanelTitle => "Search Features";

        #endregion

        #region State

        private string _query   = "";
        private IReadOnlyList<SearchRegistry.Result> _results = Array.Empty<SearchRegistry.Result>();
        private int    _hovered = -1;

        #endregion

        #region UI - Entry Point

        protected override void DrawPanelContent()
        {
            // ── Search box ────────────────────────────────────────────────────
            UI.BeginGroup("Search");
            bool changed = UI.InputText("##sfq", ref _query, 128,
                placeholder: "e.g. recoil, tier, chams...", width: UI.GroupInnerWidth);
            if (changed)
                OnQuery_Changed(_query);
            UI.EndGroup();

            // ── Results list ──────────────────────────────────────────────────
            UI.BeginGroup("Results");

            if (string.IsNullOrWhiteSpace(_query))
                UI.Empty("Start typing to search.");
            else if (_results.Count == 0)
                UI.Empty("No matches found.");
            else
            {
                // Fill all available height so the Results EndGroup border lands at
                // contentH, giving the universal 12px chrome bottom gap.
                float listH = ImGuiNET.ImGui.GetContentRegionAvail().Y;
                if (listH < 60f) listH = 60f;

                ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 4f));
                if (ImGuiNET.ImGui.BeginChild("##sflist", new Vector2(UI.GroupInnerWidth, listH),
                    ImGuiChildFlags.None))
                {
                    var dl   = ImGuiNET.ImGui.GetWindowDrawList();
                    var font = ImGuiStyle.FontMedium;
                    float fs = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();

                    for (int i = 0; i < _results.Count; i++)
                    {
                        var r    = _results[i];
                        bool isH = i == _hovered;

                        // Breadcrumb: "Panel > Tab > SubTab"
                        string crumb = BuildBreadcrumb(r.Panel, r.TabPath);

                        Vector2 rowTL = ImGuiNET.ImGui.GetCursorScreenPos();
                        float   rowW  = ImGuiNET.ImGui.GetContentRegionAvail().X;
                        float   rowH  = fs * 2f + 6f;

                        ImGuiNET.ImGui.InvisibleButton("##sfr_" + i, new Vector2(rowW, rowH));
                        bool clicked = ImGuiNET.ImGui.IsItemClicked();
                        bool hov     = ImGuiNET.ImGui.IsItemHovered();
                        if (hov) _hovered = i; else if (_hovered == i) _hovered = -1;

                        Vector2 rowBR = new(rowTL.X + rowW, rowTL.Y + rowH);

                        // Row background
                        uint bg = hov
                            ? UI.Alpha(ImGuiStyle.Dark3, 180)
                            : (i % 2 == 0 ? UI.Alpha(ImGuiStyle.Dark2, 60) : 0u);
                        if (bg != 0) dl.AddRectFilled(rowTL, rowBR, bg, 4f);

                        // Vertically centre the two-line text block inside the row.
                        float textH = fs + 2f + fs * 0.85f;
                        float yBase = rowTL.Y + MathF.Round((rowH - textH) * 0.5f);

                        // Key name (translated - show the user their own locale, not the raw English key)
                        uint keyCol  = UI.Alpha(0xFFFFFFFF, hov ? (byte)230 : (byte)200);
                        dl.AddText(font, fs,
                            new Vector2(rowTL.X + 8f, yBase),
                            keyCol, LocManager.Get(r.Key));

                        // Breadcrumb below
                        uint crumbCol = UI.Alpha(ImGuiStyle.Accent, hov ? (byte)200 : (byte)140);
                        dl.AddText(ImGuiStyle.FontNormal, fs * 0.85f,
                            new Vector2(rowTL.X + 8f, yBase + fs + 2f),
                            crumbCol, crumb);

                        // Hover accent left bar
                        if (hov)
                            dl.AddRectFilled(
                                rowTL,
                                new Vector2(rowTL.X + 2f, rowBR.Y),
                                ImGuiStyle.Accent, 1f);

                        if (clicked)
                            OnResult_Clicked(r.Key);
                    }
                }
                ImGuiNET.ImGui.EndChild();
                ImGuiNET.ImGui.PopStyleVar();
            }

            UI.EndGroup();
        }

        #endregion

        #region Events

        private void OnQuery_Changed(string query)
        {
            _results = SearchRegistry.Search(query);
        }

        private static void OnResult_Clicked(string key)
        {
            SearchRegistry.NavigateTo(key);
        }

        #endregion

        #region Helpers

        private static string BuildBreadcrumb(string panel, IReadOnlyList<string> tabs)
        {
            string locPanel = LocManager.Get(panel ?? "");
            if (tabs == null || tabs.Count == 0) return locPanel;
            var sb = new System.Text.StringBuilder(locPanel);
            foreach (var t in tabs) sb.Append(" > ").Append(LocManager.Get(t));
            return sb.ToString();
        }

        #endregion
    }
}
