// All theme/color/style constants for the ImGui UI.

using System.Numerics;
using ImGuiNET;

namespace SC2Radar.UI.ImGui
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public static partial class ImGuiStyle
    {
        // ???? Color palette ??????????????????????????????????????????????????????????????????????????????????????????????????????????
        // Defined as RGBA uint (0xAABBGGRR in ImGui convention)

        public static uint Col(byte r, byte g, byte b, byte a = 255)
            => (uint)(r | (g << 8) | (b << 16) | (a << 24));

        public static uint Col(float r, float g, float b, float a = 1f)
            => Col((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), (byte)(a * 255));

        // ???? Base palette ????????????????????????????????????????????????????????????????????????????????????????????????????????????
        public static readonly uint Dark0       = Col(0x12, 0x12, 0x12); // deepest bg
        public static uint Dark1       = Col(0x1A, 0x1A, 0x1A); // window bg            (mutable for interface color)
        public static uint Dark2       = Col(0x22, 0x22, 0x22); // child bg / frame bg  (mutable for interface color)
        public static uint Dark3       = Col(0x2C, 0x2C, 0x2C); // header bg            (mutable for interface color)
        public static readonly uint Dark4       = Col(0x38, 0x38, 0x38); // button / hovered
        public static readonly uint Dark5       = Col(0x48, 0x48, 0x48); // active
        public static uint Accent      = Col(0x00, 0x7A, 0xFF); // blue accent           (mutable for interface color)
        public static uint AccentHover = Col(0x33, 0x9A, 0xFF);
        public static uint AccentActive = Col(0x00, 0x5C, 0xCC);
        public static uint Text            = Col(0xE0, 0xE0, 0xE0);   // (mutable for interface color)
        public static uint ControlFill     = Col(0x00, 0x7A, 0xFF);   // toggle/checkbox checked fill (default = Accent)
        public static uint GroupTitleColor = Col(0x00, 0x7A, 0xFF);   // group box title text (default = Accent)
        public static uint PanelBg         = Col(0x10, 0x10, 0x14);   // chrome panel / group box body background
        public static uint WidgetBg        = Col(0x10, 0x10, 0x14);   // idle background for input widgets
        public static uint ContextMenuBg   = Col(0x10, 0x10, 0x14);   // right-click context menus & dropdown menus
        public static uint DropdownBg      = Col(0x10, 0x10, 0x14);   // combo / tag-combo dropdown list popups
        public static uint ModalBg         = Col(0x10, 0x10, 0x14);   // modal / dialog windows
        public static uint PopoverBg       = Col(0x10, 0x10, 0x14);   // popover panels (cog settings, item pickers)
        public static uint ColorPickerBg   = Col(0x10, 0x10, 0x14);   // color picker popup
        public static uint TooltipBg       = Col(0x10, 0x10, 0x14);   // tooltip popups
        public static uint InputBg         = Col(0x1E, 0x1E, 0x22);   // interactive control surface background (hover lift)
        public static uint InputBgHov      = Col(0x2C, 0x2C, 0x30);   // hover-brightened control surface

        // ── Floating radar widget chrome (Loot Info, Player Info, Kill Feed, etc.) ──
        // Independent from PanelBg/settings-panel colors so widgets can be themed on
        // their own - see WidgetChrome.ChromeBg/ChromeTitleBg/ChromeBorder/etc.
        public static uint WidgetChromeBg          = Col(0x10, 0x10, 0x14);
        public static uint WidgetChromeTitleBg     = Col(0x0C, 0x0C, 0x10);
        public static uint WidgetChromeBorder      = Col(0x26, 0x26, 0x36);
        public static uint WidgetChromeTitleText   = Col(0xE8, 0xE8, 0xEC);
        public static uint WidgetChromeScrollThumb = Col(0x3A, 0x3A, 0x50);

        // ── Main settings panel chrome (General Settings, ESP, Loot Filter, etc.) ──
        // Independent from PanelBg (shared with nested group boxes) and from the widget
        // chrome above - see ChromePanel.ChromeBg/ChromeTitleBg/ChromeBorder/etc.
        public static uint MainPanelBg          = Col(0x10, 0x10, 0x14);
        public static uint MainPanelTitleBg     = Col(0x0C, 0x0C, 0x10);
        public static uint MainPanelBorder      = Col(0x26, 0x26, 0x36);
        public static uint MainPanelTitleText   = Col(0xE8, 0xE8, 0xEC);
        public static uint MainPanelScrollThumb = Col(0x3A, 0x3A, 0x50);
        public static readonly uint TextDim     = Col(0x88, 0x88, 0x88);
        public static readonly uint TextDisabled = Col(0x55, 0x55, 0x55);
        public static uint Border      = Col(0x3A, 0x3A, 0x3A); // (mutable for interface color)
        public static uint BorderHover = Col(0x55, 0x55, 0x55);
        public static readonly uint Danger      = Col(0xE0, 0x30, 0x30);
        public static readonly uint Success     = Col(0x30, 0xC0, 0x30);
        public static readonly uint Warning     = Col(0xE0, 0xA0, 0x00);

        // ???? Transparency variants ??????????????????????????????????????????????????????????????????????????????????????????
        public static uint WithAlpha(uint col, byte alpha)
            => (col & 0x00FFFFFF) | ((uint)alpha << 24);

        public static readonly uint WindowBgTransparent = WithAlpha(Dark1, 220);
        public static readonly uint PopupBg             = WithAlpha(Dark2, 245);

        // ???? Color helpers ??????????????????????????????????????????????????????????????????????????????????????????
        private static bool _themeReady = false;

        /// <summary>Lighten a Col() uint by adding a fixed amount to each channel.</summary>
        public static uint BrighterColor(uint col, byte delta) =>
            Col((byte)Math.Min(255, (col & 0xFF) + delta),
                (byte)Math.Min(255, ((col >> 8) & 0xFF) + delta),
                (byte)Math.Min(255, ((col >> 16) & 0xFF) + delta));

        /// <summary>Darken a Col() uint by subtracting a fixed amount from each channel.</summary>
        public static uint DarkerColor(uint col, byte delta) =>
            Col((byte)Math.Max(0, (int)(col & 0xFF) - delta),
                (byte)Math.Max(0, (int)((col >> 8) & 0xFF) - delta),
                (byte)Math.Max(0, (int)((col >> 16) & 0xFF) - delta));

        /// <summary>
        /// Re-apply just the mutable theme colors (Accent, Region, Border) to the live ImGui style.
        /// Safe to call any time after Apply() has run once.
        /// </summary>
        public static void ApplyThemeColors()
        {
            if (!_themeReady) return;
            var c = ImGuiNET.ImGui.GetStyle().Colors;

            // Accent
            c[(int)ImGuiCol.ScrollbarGrabActive]  = V4(Accent);
            c[(int)ImGuiCol.CheckMark]             = V4(Accent);
            c[(int)ImGuiCol.SliderGrab]            = V4(Accent);
            c[(int)ImGuiCol.SliderGrabActive]      = V4(AccentActive);
            c[(int)ImGuiCol.ButtonActive]          = V4(AccentActive);
            c[(int)ImGuiCol.Header]                = V4(WithAlpha(Accent, 60));
            c[(int)ImGuiCol.HeaderHovered]         = V4(WithAlpha(Accent, 100));
            c[(int)ImGuiCol.HeaderActive]          = V4(WithAlpha(Accent, 150));
            c[(int)ImGuiCol.SeparatorActive]       = V4(Accent);
            c[(int)ImGuiCol.ResizeGrip]            = V4(WithAlpha(Accent, 50));
            c[(int)ImGuiCol.ResizeGripHovered]     = V4(WithAlpha(Accent, 100));
            c[(int)ImGuiCol.ResizeGripActive]      = V4(AccentActive);
            c[(int)ImGuiCol.TabHovered]            = V4(WithAlpha(AccentHover, 180));
            c[(int)ImGuiCol.TabSelected]           = V4(WithAlpha(Accent, 220));
            c[(int)ImGuiCol.DockingPreview]        = V4(WithAlpha(Accent, 120));
            c[(int)ImGuiCol.PlotLines]             = V4(AccentHover);
            c[(int)ImGuiCol.PlotLinesHovered]      = V4(Accent);
            c[(int)ImGuiCol.PlotHistogram]         = V4(Accent);
            c[(int)ImGuiCol.PlotHistogramHovered]  = V4(AccentHover);
            c[(int)ImGuiCol.TextSelectedBg]        = V4(WithAlpha(Accent, 100));
            c[(int)ImGuiCol.DragDropTarget]        = V4(Accent);
            c[(int)ImGuiCol.NavHighlight]          = V4(Accent);

            // Region (Dark2) - child bg, frame bg hover, popup bg
            c[(int)ImGuiCol.PopupBg]               = V4(WithAlpha(Dark2, 245));
            c[(int)ImGuiCol.ChildBg]               = new Vector4(0f);
            c[(int)ImGuiCol.FrameBgHovered]        = V4(Dark3);

            // Secondary region (Dark3) - header bg, buttons, tabs, table header
            c[(int)ImGuiCol.Button]                = V4(Dark3);
            c[(int)ImGuiCol.Tab]                   = V4(WithAlpha(Dark3, 200));
            c[(int)ImGuiCol.TabDimmedSelected]     = V4(WithAlpha(Dark3, 200));
            c[(int)ImGuiCol.TableHeaderBg]         = V4(Dark3);
            c[(int)ImGuiCol.TableRowBgAlt]         = V4(WithAlpha(Dark3, 30));

            // Border
            c[(int)ImGuiCol.Border]                = V4(Border);
            c[(int)ImGuiCol.Separator]             = V4(Border);
            c[(int)ImGuiCol.SeparatorHovered]      = V4(BorderHover);
            c[(int)ImGuiCol.TableBorderStrong]     = V4(Border);
            c[(int)ImGuiCol.TableBorderLight]      = V4(WithAlpha(Border, 100));

            // Text
            c[(int)ImGuiCol.Text]                  = V4(Text);
        }

        // ???? Apply the full theme ????????????????????????????????????????????????????????????????????????????????????????????
        public static void Apply()
        {
            _themeReady = true;
            var style = ImGuiNET.ImGui.GetStyle();

            // ???? Geometry ????????????????????????????????????????????????????????????????????????????????????????????????????????????
            style.WindowRounding    = 6f;
            style.ChildRounding     = 4f;
            style.FrameRounding     = 4f;
            style.PopupRounding     = 4f;
            style.ScrollbarRounding = 3f;
            style.GrabRounding      = 3f;
            style.TabRounding       = 4f;

            style.WindowPadding     = new Vector2(10, 8);
            style.FramePadding      = new Vector2(6, 5);
            style.CellPadding       = new Vector2(4, 2);
            style.ItemSpacing       = new Vector2(8, 6);
            style.ItemInnerSpacing  = new Vector2(4, 4);
            style.IndentSpacing     = 16f;
            style.ScrollbarSize     = 10f;
            style.GrabMinSize       = 12f;

            style.WindowBorderSize  = 1f;
            style.FrameBorderSize   = 1f;
            style.PopupBorderSize   = 1f;

            style.WindowMenuButtonPosition = ImGuiDir.None;

            // ???? Colors ????????????????????????????????????????????????????????????????????????????????????????????????????????????????
            var c = style.Colors;

            c[(int)ImGuiCol.Text]                  = V4(Text);
            c[(int)ImGuiCol.TextDisabled]           = V4(TextDisabled);
            c[(int)ImGuiCol.WindowBg]               = V4(WindowBgTransparent);
            c[(int)ImGuiCol.ChildBg]                = new Vector4(0f);
            c[(int)ImGuiCol.PopupBg]                = V4(PopupBg);
            c[(int)ImGuiCol.Border]                 = V4(Border);
            c[(int)ImGuiCol.BorderShadow]           = V4(WithAlpha(0, 0));
            c[(int)ImGuiCol.FrameBg]                = V4(Col(0x28, 0x28, 0x28));
            c[(int)ImGuiCol.FrameBgHovered]         = V4(Dark3);
            c[(int)ImGuiCol.FrameBgActive]          = V4(Dark4);
            c[(int)ImGuiCol.TitleBg]                = V4(Dark0);
            c[(int)ImGuiCol.TitleBgActive]          = V4(Dark1);
            c[(int)ImGuiCol.TitleBgCollapsed]       = V4(WithAlpha(Dark0, 180));
            c[(int)ImGuiCol.MenuBarBg]              = V4(Dark1);
            c[(int)ImGuiCol.ScrollbarBg]            = V4(WithAlpha(Dark1, 0));
            c[(int)ImGuiCol.ScrollbarGrab]          = V4(Dark4);
            c[(int)ImGuiCol.ScrollbarGrabHovered]   = V4(Dark5);
            c[(int)ImGuiCol.ScrollbarGrabActive]    = V4(Accent);
            c[(int)ImGuiCol.CheckMark]              = V4(Accent);
            c[(int)ImGuiCol.SliderGrab]             = V4(Accent);
            c[(int)ImGuiCol.SliderGrabActive]       = V4(AccentActive);
            c[(int)ImGuiCol.Button]                 = V4(Dark3);
            c[(int)ImGuiCol.ButtonHovered]          = V4(Dark4);
            c[(int)ImGuiCol.ButtonActive]           = V4(AccentActive);
            c[(int)ImGuiCol.Header]                 = V4(WithAlpha(Accent, 60));
            c[(int)ImGuiCol.HeaderHovered]          = V4(WithAlpha(Accent, 100));
            c[(int)ImGuiCol.HeaderActive]           = V4(WithAlpha(Accent, 150));
            c[(int)ImGuiCol.Separator]              = V4(Border);
            c[(int)ImGuiCol.SeparatorHovered]       = V4(BorderHover);
            c[(int)ImGuiCol.SeparatorActive]        = V4(Accent);
            c[(int)ImGuiCol.ResizeGrip]             = V4(WithAlpha(Accent, 50));
            c[(int)ImGuiCol.ResizeGripHovered]      = V4(WithAlpha(Accent, 100));
            c[(int)ImGuiCol.ResizeGripActive]       = V4(AccentActive);
            c[(int)ImGuiCol.Tab]                    = V4(WithAlpha(Dark3, 200));
            c[(int)ImGuiCol.TabHovered]             = V4(WithAlpha(AccentHover, 180));
            c[(int)ImGuiCol.TabSelected]              = V4(WithAlpha(Accent, 220));
            c[(int)ImGuiCol.TabDimmed]           = V4(WithAlpha(Dark2, 200));
            c[(int)ImGuiCol.TabDimmedSelected]     = V4(WithAlpha(Dark3, 200));
            c[(int)ImGuiCol.DockingPreview]         = V4(WithAlpha(Accent, 120));
            c[(int)ImGuiCol.DockingEmptyBg]         = V4(Dark0);
            c[(int)ImGuiCol.PlotLines]              = V4(AccentHover);
            c[(int)ImGuiCol.PlotLinesHovered]       = V4(Accent);
            c[(int)ImGuiCol.PlotHistogram]          = V4(Accent);
            c[(int)ImGuiCol.PlotHistogramHovered]   = V4(AccentHover);
            c[(int)ImGuiCol.TableHeaderBg]          = V4(Dark3);
            c[(int)ImGuiCol.TableBorderStrong]      = V4(Border);
            c[(int)ImGuiCol.TableBorderLight]       = V4(WithAlpha(Border, 100));
            c[(int)ImGuiCol.TableRowBg]             = V4(WithAlpha(0, 0));
            c[(int)ImGuiCol.TableRowBgAlt]          = V4(WithAlpha(Dark3, 30));
            c[(int)ImGuiCol.TextSelectedBg]         = V4(WithAlpha(Accent, 100));
            c[(int)ImGuiCol.DragDropTarget]         = V4(Accent);
            c[(int)ImGuiCol.NavHighlight]           = V4(Accent);
            c[(int)ImGuiCol.NavWindowingHighlight]  = V4(WithAlpha(Text, 180));
            c[(int)ImGuiCol.NavWindowingDimBg]      = V4(WithAlpha(Dark0, 100));
            c[(int)ImGuiCol.ModalWindowDimBg]       = V4(WithAlpha(Dark0, 150));
        }

        private static Vector4 V4(uint col)
        {
            float r = (col & 0xFF) / 255f;
            float g = ((col >> 8)  & 0xFF) / 255f;
            float b = ((col >> 16) & 0xFF) / 255f;
            float a = ((col >> 24) & 0xFF) / 255f;
            return new Vector4(r, g, b, a);
        }

        // ???? Utility: push/pop a colored button ????????????????????????????????????????????????????????????????
        public static void PushDangerButton()
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button,        V4(WithAlpha(Danger, 180)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, V4(Danger));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonActive,  V4(WithAlpha(Danger, 220)));
        }
        public static void PopDangerButton() => ImGuiNET.ImGui.PopStyleColor(3);

        public static void PushSuccessButton()
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button,        V4(WithAlpha(Success, 180)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, V4(Success));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonActive,  V4(WithAlpha(Success, 220)));
        }
        public static void PopSuccessButton() => ImGuiNET.ImGui.PopStyleColor(3);

        // ???? Font helpers ????????????????????????????????????????????????????????????????????????????????????????????????????????????
        private static ImFontPtr _fontNormal;
        private static ImFontPtr _fontMedium;
        private static ImFontPtr _fontBold;
        private static ImFontPtr _fontSmall;
        private static Dictionary<string, ImFontPtr> _slotFonts = new(StringComparer.OrdinalIgnoreCase);

        // HUD / overlay fonts baked at 64px so rendering across the 6–64pt slider never upscales.
        // SlotFonts is also baked at 64px so both radar and ESP share one cache with consistent quality.
        private static ImFontPtr _hudFontNormal;
        private static ImFontPtr _hudFontMedium;
        private static ImFontPtr _hudFontBold;

        public static ImFontPtr FontNormal  => _fontNormal;
        public static ImFontPtr FontMedium  => _fontMedium;
        public static ImFontPtr FontBold    => _fontBold;
        public static ImFontPtr FontSmall   => _fontSmall;
        public static IReadOnlyDictionary<string, ImFontPtr> SlotFonts => _slotFonts;

        public static ImFontPtr HudFontNormal => _hudFontNormal;
        public static ImFontPtr HudFontMedium => _hudFontMedium;
        public static ImFontPtr HudFontBold   => _hudFontBold;

        /// <summary>
        /// Load fonts. If <paramref name="customFontPath"/> points to a valid TTF/OTF file it is
        /// used for all weights; otherwise falls back to the embedded NeoSans fonts.
        /// Call BEFORE the first ImGui frame (or inside ReloadFonts between frames).
        /// <paramref name="slotFontPaths"/> lists any per-slot custom font files to bake in.
        /// </summary>
        public static unsafe void LoadFonts(float uiScale = 1f, string customFontPath = null,
            IReadOnlyList<string> slotFontPaths = null)
        {
            var io = ImGuiNET.ImGui.GetIO();
            io.Fonts.Clear();
            _slotFonts = new Dictionary<string, ImFontPtr>(StringComparer.OrdinalIgnoreCase);

            // Allow a wider atlas so the full CJK glyph set fits without excessive height.
            io.Fonts.TexDesiredWidth = 8192;

            // Glyph ranges loaded for every font weight.
            // Each pair is (first, last) inclusive; array is null-terminated.
            // The ranges pointer must remain valid until io.Fonts.Build() is called,
            // which is why the fixed block must span the entire Build() call.
            ushort[] ranges =
            {
                0x0020, 0x00FF,  // Basic Latin + Latin Supplement
                0x0100, 0x024F,  // Latin Extended-A/B (Vietnamese, Czech, Hungarian, etc.)
                0x1E00, 0x1EFF,  // Latin Extended Additional (Vietnamese tonal diacritics)
                0x0400, 0x04FF,  // Cyrillic (Russian locale)
                0x0E00, 0x0E7F,  // Thai script - Segoe UI covers this block
                0x2000, 0x206F,  // General Punctuation (… - – ' ' " " • etc.)
                0x20A0, 0x20CF,  // Currency Symbols (₽ = U+20BD)
                0x2190, 0x21FF,  // Arrows (↩ reply indicator, etc.)
                0x2200, 0x22FF,  // Mathematical Operators (∞ ± × ÷ ≤ ≥ ≠ √ etc.)
                0x25A0, 0x25FF,  // Geometric Shapes (▲ ▼ – height indicator arrows)
                0x2600, 0x27BF,  // Misc Symbols + Dingbats (✎ ★ ☆ ♠ ♣ ♥ ♦ ☀ ☂ ✈ ☎ ⚠ ✓ ✗ etc.) -
                                 // covers common flavor/status characters IRC chat text uses
                                 // that aren't full-color pictograph emoji (those need a color
                                 // font + FreeType, which this app's plain stb_truetype
                                 // rasterizer doesn't support).
                0
            };

            // Overlay fonts (HUD elements, radar slot labels) only need Latin + Cyrillic.
            // Using the full 'ranges' set at large bake sizes multiplies atlas memory
            // by ~3× per font; this minimal set keeps the atlas footprint manageable.
            ushort[] overlayRanges =
            {
                0x0020, 0x00FF,  // Basic Latin + Latin Supplement
                0x0400, 0x04FF,  // Cyrillic (player names)
                0x20A0, 0x20CF,  // Currency Symbols (₽ = U+20BD, loot values)
                0x221E, 0x221E,  // ∞ infinity (status text)
                0x25A0, 0x25FF,  // Geometric Shapes (▲ ▼ – height indicator arrows)
                0
            };

            // Segoe UI ships on every Windows install and covers Cyrillic, Thai, and
            // currency glyphs that NeoSans (a Latin-only commercial font) omits.
            // Merged as a transparent fallback - NeoSans glyphs are never replaced.
            string fallbackPath =
                new[] { @"C:\Windows\Fonts\segoeui.ttf", @"C:\Windows\Fonts\arial.ttf" }
                .FirstOrDefault(System.IO.File.Exists);

            // CJK font: Chinese + Japanese (Hiragana/Katakana in GetGlyphRangesChineseFull).
            // Microsoft YaHei ships on Windows 7+ for Simplified Chinese; SimSun as backup.
            string cjkFontPath =
                new[] { @"C:\Windows\Fonts\msyh.ttc", @"C:\Windows\Fonts\simsun.ttf" }
                .FirstOrDefault(System.IO.File.Exists);

            // Korean font: Malgun Gothic ships on Windows 8+; Gulim as fallback.
            string koreanFontPath =
                new[] { @"C:\Windows\Fonts\malgun.ttf", @"C:\Windows\Fonts\gulim.ttc" }
                .FirstOrDefault(System.IO.File.Exists);

            // Custom font from file
            if (!string.IsNullOrEmpty(customFontPath) && System.IO.File.Exists(customFontPath))
            {
                fixed (ushort* pR  = ranges)
                fixed (ushort* pOv = overlayRanges)
                {
                    IntPtr r  = (IntPtr)pR;
                    IntPtr ov = (IntPtr)pOv;
                    _fontNormal = io.Fonts.AddFontFromFileTTF(customFontPath, 14f * uiScale, default, r);
                    MergeFallbackFont(io.Fonts, fallbackPath, 14f * uiScale, r);
                    MergeCjkFont(io.Fonts, cjkFontPath, 14f * uiScale);
                    MergeKoreanFont(io.Fonts, koreanFontPath, 14f * uiScale);
                    _fontMedium = io.Fonts.AddFontFromFileTTF(customFontPath, 14f * uiScale, default, r);
                    MergeFallbackFont(io.Fonts, fallbackPath, 14f * uiScale, r);
                    MergeCjkFont(io.Fonts, cjkFontPath, 14f * uiScale);
                    MergeKoreanFont(io.Fonts, koreanFontPath, 14f * uiScale);
                    _fontBold   = io.Fonts.AddFontFromFileTTF(customFontPath, 14f * uiScale, default, r);
                    MergeFallbackFont(io.Fonts, fallbackPath, 14f * uiScale, r);
                    MergeCjkFont(io.Fonts, cjkFontPath, 14f * uiScale);
                    MergeKoreanFont(io.Fonts, koreanFontPath, 14f * uiScale);
                    _fontSmall  = io.Fonts.AddFontFromFileTTF(customFontPath, 11f * uiScale, default, r);
                    MergeFallbackFont(io.Fonts, fallbackPath, 11f * uiScale, r);
                    MergeCjkFont(io.Fonts, cjkFontPath, 11f * uiScale);
                    MergeKoreanFont(io.Fonts, koreanFontPath, 11f * uiScale);
                    // Slot and HUD fonts: 32px with minimal range - crisp up to 32pt, small atlas footprint.
                    const float hudSzC = 32f;
                    if (slotFontPaths != null)
                        foreach (var sp in slotFontPaths)
                            _slotFonts[sp] = io.Fonts.AddFontFromFileTTF(sp, hudSzC * uiScale, default, ov);

                    // HUD weight variants - custom font has no separate bold/medium files.
                    _hudFontNormal = io.Fonts.AddFontFromFileTTF(customFontPath, hudSzC * uiScale, default, ov);
                    MergeFallbackFont(io.Fonts, fallbackPath, hudSzC * uiScale, ov);
                    _hudFontMedium = _hudFontNormal;
                    _hudFontBold   = _hudFontNormal;

                    io.Fonts.Build();
                }
                return;
            }

            // Load from embedded resources.
            // IMPORTANT: Build the atlas while the byte arrays are still pinned.
            // Building after leaving the fixed block can create corrupted glyph UVs/text.
            byte[] regular = LoadEmbeddedFont("NeoSansStdRegular.otf");
            byte[] medium  = LoadEmbeddedFont("NeoSansStdMedium.otf");
            byte[] bold    = LoadEmbeddedFont("NeoSansStdBold.otf");

            if (regular.Length == 0)
            {
                _fontNormal = io.Fonts.AddFontDefault();
                _fontMedium = _fontNormal;
                _fontBold   = _fontNormal;
                _fontSmall  = _fontNormal;
                io.Fonts.Build();
                return;
            }

            if (medium.Length == 0) medium = regular;
            if (bold.Length == 0)   bold   = regular;

            fixed (byte*   pReg    = regular)
            fixed (byte*   pMed    = medium)
            fixed (byte*   pBld    = bold)
            fixed (ushort* pRanges = ranges)
            fixed (ushort* pOv     = overlayRanges)
            {
                IntPtr r  = (IntPtr)pRanges;
                IntPtr ov = (IntPtr)pOv;

                // FontDataOwnedByAtlas = false for ALL AddFontFromMemoryTTF calls that pass
                // GC-managed byte array pointers. With the default (true), ImGui stores the raw
                // GC pointer and calls IM_FREE() on it during ImFontAtlas_destroy. By that time
                // the GC may have moved the array, so free() on a stale address corrupts the
                // native heap and causes ExecutionEngineException at shutdown.
                // AddFontFromMemoryTTF copies the config struct internally, so one object covers all calls.
                var cfgNoOwn = new ImFontConfigPtr(ImGuiNET.ImGuiNative.ImFontConfig_ImFontConfig());
                cfgNoOwn.FontDataOwnedByAtlas = false;

                _fontNormal = io.Fonts.AddFontFromMemoryTTF((IntPtr)pReg, regular.Length, 14f * uiScale, cfgNoOwn, r);
                MergeFallbackFont(io.Fonts, fallbackPath, 14f * uiScale, r);
                MergeCjkFont(io.Fonts, cjkFontPath, 14f * uiScale);
                MergeKoreanFont(io.Fonts, koreanFontPath, 14f * uiScale);

                _fontMedium = io.Fonts.AddFontFromMemoryTTF((IntPtr)pMed, medium.Length, 14f * uiScale, cfgNoOwn, r);
                MergeFallbackFont(io.Fonts, fallbackPath, 14f * uiScale, r);
                MergeCjkFont(io.Fonts, cjkFontPath, 14f * uiScale);
                MergeKoreanFont(io.Fonts, koreanFontPath, 14f * uiScale);

                _fontBold   = io.Fonts.AddFontFromMemoryTTF((IntPtr)pBld, bold.Length,   14f * uiScale, cfgNoOwn, r);
                MergeFallbackFont(io.Fonts, fallbackPath, 14f * uiScale, r);
                MergeCjkFont(io.Fonts, cjkFontPath, 14f * uiScale);
                MergeKoreanFont(io.Fonts, koreanFontPath, 14f * uiScale);

                _fontSmall  = io.Fonts.AddFontFromMemoryTTF((IntPtr)pReg, regular.Length, 11f * uiScale, cfgNoOwn, r);
                MergeFallbackFont(io.Fonts, fallbackPath, 11f * uiScale, r);
                MergeCjkFont(io.Fonts, cjkFontPath, 11f * uiScale);
                MergeKoreanFont(io.Fonts, koreanFontPath, 11f * uiScale);

                // Slot and HUD fonts: 32px with minimal Latin+Cyrillic range.
                // 32px bake is crisp up to 32pt; atlas footprint is ~12× smaller than 64px/full-range.
                const float hudSz = 32f;
                if (slotFontPaths != null)
                    foreach (var sp in slotFontPaths)
                        _slotFonts[sp] = io.Fonts.AddFontFromFileTTF(sp, hudSz * uiScale, default, ov);

                var hudCfgN = new ImFontConfigPtr(ImGuiNET.ImGuiNative.ImFontConfig_ImFontConfig());
                hudCfgN.FontDataOwnedByAtlas = false;
                var hudCfgM = new ImFontConfigPtr(ImGuiNET.ImGuiNative.ImFontConfig_ImFontConfig());
                hudCfgM.FontDataOwnedByAtlas = false;
                var hudCfgB = new ImFontConfigPtr(ImGuiNET.ImGuiNative.ImFontConfig_ImFontConfig());
                hudCfgB.FontDataOwnedByAtlas = false;
                _hudFontNormal = io.Fonts.AddFontFromMemoryTTF((IntPtr)pReg, regular.Length, hudSz * uiScale, hudCfgN, ov);
                MergeFallbackFont(io.Fonts, fallbackPath, hudSz * uiScale, ov);
                _hudFontMedium = io.Fonts.AddFontFromMemoryTTF((IntPtr)pMed, medium.Length,  hudSz * uiScale, hudCfgM, ov);
                MergeFallbackFont(io.Fonts, fallbackPath, hudSz * uiScale, ov);
                _hudFontBold   = io.Fonts.AddFontFromMemoryTTF((IntPtr)pBld, bold.Length,    hudSz * uiScale, hudCfgB, ov);
                MergeFallbackFont(io.Fonts, fallbackPath, hudSz * uiScale, ov);

                io.Fonts.Build();
            }
        }

        // Merges glyphs from a fallback font file into the most recently added font.
        // Only glyphs not already present in the primary font are pulled in.
        // Segoe UI's cap-height is shorter relative to its em square than NeoSans, so the
        // fallback is loaded at 1.35x the primary size to visually match, with a small
        // downward Y offset to keep merged glyphs on the same baseline.
        private static unsafe void MergeFallbackFont(ImFontAtlasPtr atlas, string path, float size, IntPtr ranges)
        {
            if (path == null) return;
            var cfgPtr = new ImFontConfigPtr(ImGuiNET.ImGuiNative.ImFontConfig_ImFontConfig());
            cfgPtr.MergeMode    = true;
            cfgPtr.PixelSnapH   = true;
            cfgPtr.GlyphOffset  = new System.Numerics.Vector2(0f, size * 0.08f);
            atlas.AddFontFromFileTTF(path, size * 1.35f, cfgPtr, ranges);
            ImGuiNET.ImGuiNative.ImFontConfig_destroy(cfgPtr.NativePtr);
        }

        // Merges the full CJK Unified Ideographs block (Chinese + Hiragana/Katakana for
        // Japanese) into the most recently added font. Uses ChineseFull rather than
        // ChineseSimplifiedCommon so that uncommon transliteration characters like 柯尔特
        // ("Colt") are covered - the common subset omits ~18 000 characters.
        private static unsafe void MergeCjkFont(ImFontAtlasPtr atlas, string path, float size)
        {
            if (path == null) return;
            var cfgPtr = new ImFontConfigPtr(ImGuiNET.ImGuiNative.ImFontConfig_ImFontConfig());
            cfgPtr.MergeMode  = true;
            cfgPtr.PixelSnapH = true;
            atlas.AddFontFromFileTTF(path, size, cfgPtr, atlas.GetGlyphRangesChineseFull());
            ImGuiNET.ImGuiNative.ImFontConfig_destroy(cfgPtr.NativePtr);
        }

        // Merges the Hangul Syllables block (Korean) from Malgun Gothic into the most
        // recently added font. GetGlyphRangesKorean covers U+AC00-U+D7A3 (all precomposed
        // Hangul syllables) plus Hangul Jamo and Compatibility Jamo.
        private static unsafe void MergeKoreanFont(ImFontAtlasPtr atlas, string path, float size)
        {
            if (path == null) return;
            var cfgPtr = new ImFontConfigPtr(ImGuiNET.ImGuiNative.ImFontConfig_ImFontConfig());
            cfgPtr.MergeMode  = true;
            cfgPtr.PixelSnapH = true;
            atlas.AddFontFromFileTTF(path, size, cfgPtr, atlas.GetGlyphRangesKorean());
            ImGuiNET.ImGuiNative.ImFontConfig_destroy(cfgPtr.NativePtr);
        }

        private static byte[] LoadEmbeddedFont(string name)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var rn in asm.GetManifestResourceNames())
            {
                if (rn.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    using var s = asm.GetManifestResourceStream(rn)!;
                    var buf = new byte[s.Length];
                    s.ReadExactly(buf, 0, buf.Length);
                    return buf;
                }
            }
            return Array.Empty<byte>();
        }

        // ???? ESP draw-list colors (replaces SKPaints) ????????????????????????????????????????????????????
        // Format: RGBA packed as uint32 for ImGui DrawList

        public static uint ESPColor(byte r, byte g, byte b, byte a = 255)
            => (uint)(r | (g << 8) | (b << 16) | (a << 24));

        public static uint ESPColorLocalPlayer   = ESPColor(255, 255,   0);
        public static uint ESPColorTeammate      = ESPColor( 50, 200,  50);
        public static uint ESPColorUSEC          = ESPColor(200,  50,  50);
        public static uint ESPColorBEAR          = ESPColor(220,  80,  30);
        public static uint ESPColorScav          = ESPColor(200, 180,  50);
        public static uint ESPColorRaider        = ESPColor(180,  50, 180);
        public static uint ESPColorBlackDivision = ESPColor(127, 255,   0); // Chartreuse
        public static uint ESPColorBoss          = ESPColor(255,   0, 255); // Fuchsia/Magenta - matches default config
        public static uint ESPColorGuard         = ESPColor(255,  80,   0); // Burnt orange - boss guard
        public static uint ESPColorPScav         = ESPColor(255, 165,   0);
        public static uint ESPColorSpecial       = ESPColor(  0, 255, 200);
        public static uint ESPColorStreamer      = ESPColor(160,  32, 240);
        public static uint ESPColorAimbotLocked  = ESPColor(255,   0,   0);
        public static uint ESPColorFocused       = ESPColor(  0, 255, 255);
        public static uint ESPColorDeathMarker   = ESPColor(100, 100, 100);
        public static uint ESPColorExfil         = ESPColor( 50, 220,  50);
        public static uint ESPColorExfilPending  = ESPColor(220, 220,  50);
        public static uint ESPColorExfilClosed   = ESPColor(220,  50,  50);
        public static uint ESPColorExfilInactive = ESPColor(120, 120, 120);
        public static uint ESPColorExfilTransit  = ESPColor(255, 165,   0);
        public static readonly uint ESPColorLoot          = ESPColor(255, 255, 255);
        public static uint ESPColorQuestItem     = ESPColor(  0, 200, 255);
        public static uint ESPColorSTierLoot     = ESPColor(255, 215,   0);
        public static uint ESPColorContainer     = ESPColor(200, 150,  50);
        public static uint ESPColorGrenade       = ESPColor(255,  50,  50);
        public static uint ESPColorTripwire      = ESPColor(255, 165,   0);
        public static uint ESPColorMine          = ESPColor(255,  80,   0);
        public static uint ESPColorEventTrap     = ESPColor(255, 210,   0);
        public static uint ESPColorDoor          = ESPColor(150, 100,  50);
        public static uint ESPColorSwitch        = ESPColor(  0, 200, 200);
        public static readonly uint ESPColorCrosshair     = ESPColor(255, 255, 255);
        public static readonly uint ESPColorText          = ESPColor(255, 255, 255);
        public static readonly uint ESPColorTextOutline   = ESPColor(  0,   0,   0, 180);
        public static uint ESPColorBackground              = ESPColor(  0,   0,   0,   0); // full-screen ESP tint (alpha=0 = off, configurable)
        public static uint ESPColorHUDTextBackground        = ESPColor(  0,   0,   0, 140); // HUD element text background (behind HUD labels when Background is enabled)
        public static readonly uint ESPColorOverlayBg     = ESPColor(  0,   0,   0,   0); // fully transparent

        // Radar
        public static uint RadarBg              = ESPColor( 18,  18,  18, 240); // (mutable for interface color)
        public static readonly uint RadarGrid            = ESPColor( 40,  40,  40, 100);
        public static uint RadarLocalPlayer     = ESPColor(255, 255,   0);
        public static uint RadarAimline         = ESPColor(255, 255,   0, 150);
        public static uint RadarGroupLine       = ESPColor(100, 100, 100,  80);

        // Radar entity colors (separate from ESP colors)
        public static uint RadarTeammate           = ESPColor( 50, 200,  50);
        public static uint RadarUSEC               = ESPColor(200,  50,  50);
        public static uint RadarBEAR               = ESPColor(220,  80,  30);
        public static uint RadarScav               = ESPColor(200, 180,  50);
        public static uint RadarRaider             = ESPColor(180,  50, 180);
        public static uint RadarBlackDivision      = ESPColor(127, 255,   0); // Chartreuse
        public static uint RadarBTR                = ESPColor(255, 215,   0);
        public static uint RadarBoss               = ESPColor(255,   0, 255);
        public static uint RadarGuard              = ESPColor(255,  80,   0); // Burnt orange - boss guard
        public static uint RadarPScav              = ESPColor(255, 165,   0);
        public static uint RadarSpecial            = ESPColor(  0, 255, 200);
        public static uint RadarStreamer           = ESPColor(160,  32, 240);
        public static uint RadarAimbotLocked       = ESPColor(255,   0,   0);
        public static uint RadarFocused            = ESPColor(  0, 255, 255);
        public static uint RadarDeathMarker        = ESPColor(  0,   0,   0);
        public static uint RadarExfil              = ESPColor( 50, 220,  50);
        public static uint RadarExfilPending       = ESPColor(220, 220,  50);
        public static uint RadarExfilClosed        = ESPColor(220,  50,  50);
        public static uint RadarExfilInactive      = ESPColor(120, 120, 120);
        public static uint RadarExfilTransit       = ESPColor(255, 165,   0);
        public static uint RadarQuestItem          = ESPColor(  0, 200, 255);
        // Distinct from RadarQuestItem: RadarQuestItem is for the static/story quest items (the
        // "Q_"-prefixed ones with no market entry) and quest zone markers; this is for ordinary
        // loose loot (CMS, water, MRE, etc.) that just happens to be required by an active quest.
        // Default matches RadarColorOption's own YellowGreen default for QuestLoot.
        public static uint RadarQuestLoot          = ESPColor(154, 205,  50);
        public static uint RadarKappaQuest         = ESPColor(255, 165,   0);
        public static uint RadarSTierLoot          = ESPColor(255, 215,   0);
        public static uint RadarATierLoot          = ESPColor(255, 165,   0);
        public static uint RadarBTierLoot          = ESPColor(255, 215,   0);
        public static uint RadarCTierLoot          = ESPColor( 64, 224, 208);
        public static uint RadarDTierLoot          = ESPColor(245, 245, 245);
        public static uint RadarWishlistLoot       = ESPColor(255,   0,   0);
        public static uint RadarAirdrop            = ESPColor(255,   0,   0);
        public static uint RadarCorpse             = ESPColor(192, 192, 192);
        public static uint RadarMedsFilterLoot     = ESPColor(255, 160, 122);
        public static uint RadarFoodFilterLoot     = ESPColor(100, 149, 237);
        public static uint RadarWaterFilterLoot    = ESPColor(  0, 191, 255);
        public static uint RadarWeaponsFilterLoot  = ESPColor(255, 224, 104);
        public static uint RadarBackpackFilterLoot = ESPColor(  0, 176,  44);
        public static uint RadarUnknownFilterLoot  = ESPColor(180, 180, 180);
        public static uint RadarContainer          = ESPColor(200, 150,  50);
        public static uint RadarGrenade            = ESPColor(255,  50,  50);
        public static uint RadarTripwire           = ESPColor(255, 165,   0);
        public static uint RadarSwitch             = ESPColor(  0, 200, 200);
        public static uint RadarMine               = ESPColor(255,  50, 255);
        public static uint RadarEventTrap          = ESPColor(255, 210,   0);
        public static uint RadarDoorOpen           = ESPColor( 80, 200,  80);
        public static uint RadarDoorLocked         = ESPColor(200,  60,  60);
        public static uint RadarDoorShut           = ESPColor(255, 165,   0);
        public static uint RadarPOI                = ESPColor(  0, 191, 255);
        public static uint ESPPOI                  = ESPColor(  0, 191, 255);

        public static uint ESPColorTextBackground        = ESPColor(  0,   0,   0, 140);
    }
}
