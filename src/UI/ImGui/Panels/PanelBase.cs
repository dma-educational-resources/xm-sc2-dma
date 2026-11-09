using System.Numerics;
using ImGuiNET;
using SkiaSharp;
using SC2Radar.UI.Misc;
using SC2Radar.UI.ImGui;
using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Panels
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public abstract class ImGuiPanel : IDisposable
    {
        // ── Cross-type focus tracking (shared by ChromePanel & WidgetChrome) ───
        // Records the title/bounds of whichever chrome window (settings panel OR
        // radar widget) is currently the globally-focused ImGui window - i.e. the
        // one actually drawn on top of every other panel and widget, regardless of
        // which of the two (otherwise independent) chrome hierarchies it belongs to.
        // HandleResizeProbe in both ChromePanel and WidgetChrome consults this so an
        // occluded panel/widget underneath can never claim an edge-resize/hover that
        // visually belongs to whatever is drawn on top of it.
        internal static string  FocusedChromeTitle;
        internal static Vector2 FocusedChromeMin;
        internal static Vector2 FocusedChromeMax;

        protected static Config Config => Program.Config;

        protected virtual Vector2 MinSize  => new(120f, 115f);
        protected virtual Vector2 InitSize => new(380, 500);
        protected virtual Vector2 MaxSize  => new(1500f, 1500f);

        public abstract void Draw(ref bool open);

        /// <summary>
        /// Draws a row of tab buttons that WRAP onto multiple rows when they don't fit,
        /// so long tab names are never cut off. The selected tab is highlighted.
        /// </summary>
        protected static void DrawWrappedTabs(string[] names, ref int selected)
        {
            var style = ImGuiNET.ImGui.GetStyle();
            float avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
            float rowX = 0f;
            var accent = new Vector4(0.26f, 0.59f, 0.98f, 1f);

            for (int i = 0; i < names.Length; i++)
            {
                float w = ImGuiNET.ImGui.CalcTextSize(names[i]).X + style.FramePadding.X * 2f;

                if (i > 0)
                {
                    if (rowX + style.ItemSpacing.X + w <= avail)
                    {
                        ImGuiNET.ImGui.SameLine();
                        rowX += style.ItemSpacing.X;
                    }
                    else
                    {
                        rowX = 0f; // wrap to a new row
                    }
                }

                bool sel = selected == i;
                if (sel)
                {
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button, accent);
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, accent);
                }
                if (ImGuiNET.ImGui.Button(names[i] + "##wtab" + i))
                    selected = i;
                if (sel) ImGuiNET.ImGui.PopStyleColor(2);

                rowX += w;
            }
        }

        // Strip ##id suffix, register with LocManager, return translated label + original ##id.
        private static string Loc(string label)
        {
            if (string.IsNullOrEmpty(label)) return label;
            int idx = label.IndexOf("##", System.StringComparison.Ordinal);
            string key    = idx >= 0 ? label[..idx] : label;
            string suffix = idx >= 0 ? label[idx..] : "";
            if (string.IsNullOrEmpty(key)) return label;
            return LocManager.Get(key) + suffix;
        }

        protected static bool Checkbox(string label, ref bool value)
        {
            bool changed = ImGuiNET.ImGui.Checkbox(Loc(label), ref value);
            if (changed) Config.SaveDebounced();
            return changed;
        }

        protected static bool SliderFloat(string label, ref float value, float min, float max, string fmt = "%.2f")
        {
            bool changed = ImGuiNET.ImGui.SliderFloat(Loc(label), ref value, min, max, fmt);
            if (changed) Config.SaveDebounced();
            return changed;
        }

        protected static bool SliderInt(string label, ref int value, int min, int max)
        {
            bool changed = ImGuiNET.ImGui.SliderInt(Loc(label), ref value, min, max);
            if (changed) Config.SaveDebounced();
            return changed;
        }

        protected static bool ColorEdit(string label, ref uint imguiCol)
        {
            var v4 = ImGui4ToVec4(imguiCol);
            bool changed = ImGuiNET.ImGui.ColorEdit4(Loc(label), ref v4,
                ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.DisplayHex);
            if (changed) { imguiCol = Vec4ToImGui4(v4); Config.SaveDebounced(); }
            return changed;
        }

        protected static bool ColorEditRadar(string label, RadarColorOption opt)
        {
            var colors = Config.Colors;
            SKColor cur = SKColors.White;
            if (colors != null && colors.TryGetValue(opt, out var hex))
                SKColor.TryParse(hex, out cur);
            uint imCol = ImGuiStyle.ESPColor(cur.Red, cur.Green, cur.Blue, cur.Alpha);
            bool changed = ColorEdit(label + "##rc_" + opt, ref imCol);
            if (changed && colors != null)
            {
                colors[opt] = SKColorFromImGui(imCol).ToString();
                RadarColorOptions.SetColors(colors);
                Config.SaveDebounced();
            }
            return changed;
        }

        protected static bool ColorEditInterface(string label, InterfaceColorOption opt)
        {
            var colors = Config.InterfaceColors;
            SKColor cur = SKColors.White;
            if (colors != null && colors.TryGetValue(opt, out var hex))
                SKColor.TryParse(hex, out cur);
            uint imCol = ImGuiStyle.ESPColor(cur.Red, cur.Green, cur.Blue, cur.Alpha);
            bool changed = ColorEdit(label + "##ic_" + opt, ref imCol);
            if (changed)
                InterfaceColorOptions.UpdateColor(Config, opt, SKColorFromImGui(imCol));
            return changed;
        }

        protected static Vector4 ImGui4ToVec4(uint col) =>
            new((col & 0xFF) / 255f, ((col >> 8) & 0xFF) / 255f,
                ((col >> 16) & 0xFF) / 255f, ((col >> 24) & 0xFF) / 255f);

        protected static uint Vec4ToImGui4(Vector4 v) =>
            ((uint)(v.X * 255) & 0xFF) |
            (((uint)(v.Y * 255) & 0xFF) << 8) |
            (((uint)(v.Z * 255) & 0xFF) << 16) |
            (((uint)(v.W * 255) & 0xFF) << 24);

        protected static SKColor SKColorFromImGui(uint col) =>
            new((byte)(col & 0xFF), (byte)((col >> 8) & 0xFF), (byte)((col >> 16) & 0xFF));

        /// <summary>
        /// Draws a pin toggle button in the title bar, beside the close button.
        /// Call immediately after ImGui.Begin() returns true.
        /// Pinned panels are drawn last (on top) in DrawAllPanels.
        /// </summary>
        public static void DrawPinButton(string panelKey)
        {
            var  state  = Program.Config.PanelOpen;
            bool pinned = state.IsPinned(panelKey);

            float titleH = ImGuiNET.ImGui.GetFrameHeight(); // title bar height
            var   winPos = ImGuiNET.ImGui.GetWindowPos();
            float winW   = ImGuiNET.ImGui.GetWindowWidth();

            // Place our button just left of the close button.
            // Close button occupies the rightmost titleH × titleH square.
            var p1 = new Vector2(winPos.X + winW - titleH * 2f, winPos.Y);
            var p2 = new Vector2(winPos.X + winW - titleH,       winPos.Y + titleH);

            // ── Click detection ───────────────────────────────────────────────
            // Override the content clip rect to include the title bar so that
            // InvisibleButton registers input there.
            var savedCursor = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.PushClipRect(p1, p2, false);
            ImGuiNET.ImGui.SetCursorScreenPos(p1);
            bool clicked = ImGuiNET.ImGui.InvisibleButton("##pin_" + panelKey,
                new Vector2(titleH, titleH));
            bool hovered = ImGuiNET.ImGui.IsItemHovered();
            UI.RegisterLastItemForTooltip();
            ImGuiNET.ImGui.PopClipRect();
            ImGuiNET.ImGui.SetCursorScreenPos(savedCursor); // restore content cursor

            if (clicked)
            {
                state.TogglePin(panelKey);
                Program.Config.SaveDebounced();
            }

            // ── Visuals ───────────────────────────────────────────────────────
            var dl = ImGuiNET.ImGui.GetWindowDrawList();

            uint bg = hovered  ? 0x66FFFFFFu
                    : pinned   ? 0x44448BFFu
                    :            0x00000000u;
            if (bg != 0)
                dl.AddRectFilled(p1, p2, bg, 3f);

            // Icon: filled pin = pinned, outline pin = unpinned
            string icon  = pinned ? "+" : "o";
            uint   icCol = pinned ? 0xFF88CCFF : 0x88AAAAAA;
            var    ts    = ImGuiNET.ImGui.CalcTextSize(icon);
            dl.AddText(new Vector2(
                    p1.X + (titleH - ts.X) * 0.5f,
                    p1.Y + (titleH - ts.Y) * 0.5f),
                icCol, icon);

            UI.Tooltip(pinned
                ? "Pinned - always on top\nClick to unpin."
                : "Click to pin on top of all others.");
        }

        protected static void SectionHeader(string text)
        {
            ImGuiNET.ImGui.Spacing();

            var dl    = ImGuiNET.ImGui.GetWindowDrawList();
            var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
            float lineH = ImGuiNET.ImGui.GetTextLineHeight() + 8f;
            float width = ImGuiNET.ImGui.GetContentRegionAvail().X;

            // Background strip
            dl.AddRectFilled(pos,
                new Vector2(pos.X + width, pos.Y + lineH),
                ImGuiStyle.WithAlpha(ImGuiStyle.Dark3, 140));
            // Left accent bar
            dl.AddRectFilled(pos,
                new Vector2(pos.X + 3f, pos.Y + lineH),
                ImGuiStyle.Accent);

            // Advance cursor height, then draw text via DrawList (no cursor side-effects)
            ImGuiNET.ImGui.Dummy(new Vector2(width, lineH));

            var boldFont = ImGuiStyle.FontBold;
            float fontSize = boldFont.FontSize > 0 ? boldFont.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            float textY = pos.Y + (lineH - fontSize) * 0.5f;
            dl.AddText(boldFont, fontSize,
                new Vector2(pos.X + 9f, textY),
                ImGuiStyle.Accent, LocManager.Get(text));

            ImGuiNET.ImGui.Spacing();
        }

        // Vertical accent line on the left-side of nested feature settings
        private static Vector2 _cardStartPos;

        protected static void BeginFeatureCard()
        {
            ImGuiNET.ImGui.Indent(10f);
            _cardStartPos = ImGuiNET.ImGui.GetCursorScreenPos();
        }

        protected static void EndFeatureCard()
        {
            var endPos = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.Unindent(10f);

            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            dl.AddRectFilled(
                new Vector2(_cardStartPos.X - 6f, _cardStartPos.Y - 1f),
                new Vector2(_cardStartPos.X - 4f, endPos.Y),
                ImGuiStyle.WithAlpha(ImGuiStyle.Accent, 110));

            ImGuiNET.ImGui.Spacing();
        }

        public virtual void Dispose() { }
    }
}
