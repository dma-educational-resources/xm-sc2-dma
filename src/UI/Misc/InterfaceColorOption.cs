using SkiaSharp;
using System;
using System.Collections.Generic;
using SC2Radar.UI.ImGui;
using SC2Radar.UI.Misc;
using SC2Radar.SC2;

namespace SC2Radar.UI
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public enum InterfaceColorOption
    {
        AccentColor          = 0,
        RegionColor          = 1,
        SecondaryRegionColor = 2,
        BorderColor          = 3,
        RadarBackground      = 4,
        FuserBackground      = 5,
        TextColor            = 6,
        ControlFill          = 7,
        GroupBoxTitle        = 8,
        PanelBackground      = 9,
        InputBackground      = 10,
        WidgetBackground     = 11,
        ContextMenuBackground = 12,
        DropdownBackground    = 13,
        ModalBackground       = 14,
        PopoverBackground     = 15,
        ColorPickerBackground = 16,
        TooltipBackground     = 17,
        WidgetChromeBackground     = 18,
        WidgetChromeTitleBackground = 19,
        WidgetChromeBorder          = 20,
        WidgetChromeTitleText       = 21,
        WidgetChromeScrollThumb     = 22,
        MainPanelBackground      = 23,
        MainPanelTitleBackground = 24,
        MainPanelBorder          = 25,
        MainPanelTitleText       = 26,
        MainPanelScrollThumb     = 27,
    }

    internal static class InterfaceColorOptions
    {
        #region Static Interfaces

        internal static void LoadColors(Config config)
        {
            config.InterfaceColors ??= new Dictionary<InterfaceColorOption, string>();

            foreach (var defaultColor in GetDefaultColors())
                config.InterfaceColors.TryAdd(defaultColor.Key, defaultColor.Value);

            SetColors(config.InterfaceColors);
        }

        internal static Dictionary<InterfaceColorOption, string> GetDefaultColors() =>
            new()
            {
                [InterfaceColorOption.AccentColor]          = "#007AFF",
                [InterfaceColorOption.RegionColor]          = "#222222",
                [InterfaceColorOption.SecondaryRegionColor] = "#333333",
                [InterfaceColorOption.BorderColor]          = "#3A3A3A",
                [InterfaceColorOption.RadarBackground]      = "#F0121212",
                [InterfaceColorOption.FuserBackground]      = "#8C000000",
                [InterfaceColorOption.TextColor]            = "#E0E0E0",
                [InterfaceColorOption.ControlFill]          = "#007AFF",
                [InterfaceColorOption.GroupBoxTitle]        = "#007AFF",
                [InterfaceColorOption.PanelBackground]      = "#101014",
                [InterfaceColorOption.InputBackground]      = "#1E1E22",
                [InterfaceColorOption.WidgetBackground]     = "#101014",
                [InterfaceColorOption.ContextMenuBackground] = "#101014",
                [InterfaceColorOption.DropdownBackground]    = "#101014",
                [InterfaceColorOption.ModalBackground]       = "#101014",
                [InterfaceColorOption.PopoverBackground]     = "#101014",
                [InterfaceColorOption.ColorPickerBackground] = "#101014",
                [InterfaceColorOption.TooltipBackground]     = "#101014",
                [InterfaceColorOption.WidgetChromeBackground]      = "#101014",
                [InterfaceColorOption.WidgetChromeTitleBackground] = "#0C0C10",
                [InterfaceColorOption.WidgetChromeBorder]          = "#262636",
                [InterfaceColorOption.WidgetChromeTitleText]       = "#E8E8EC",
                [InterfaceColorOption.WidgetChromeScrollThumb]     = "#3A3A50",
                [InterfaceColorOption.MainPanelBackground]      = "#101014",
                [InterfaceColorOption.MainPanelTitleBackground] = "#0C0C10",
                [InterfaceColorOption.MainPanelBorder]          = "#262636",
                [InterfaceColorOption.MainPanelTitleText]       = "#E8E8EC",
                [InterfaceColorOption.MainPanelScrollThumb]     = "#3A3A50",
            };

        internal static void SetColors(IReadOnlyDictionary<InterfaceColorOption, string> colors)
        {
            bool imguiDirty = false;
            foreach (var kvp in colors)
            {
                if (!SKColor.TryParse(kvp.Value, out var c)) continue;
                switch (kvp.Key)
                {
                    case InterfaceColorOption.AccentColor:
                        ImGuiStyle.Accent      = ImGuiStyle.Col(c.Red, c.Green, c.Blue);
                        ImGuiStyle.AccentHover  = ImGuiStyle.BrighterColor(ImGuiStyle.Accent, 0x33);
                        ImGuiStyle.AccentActive = ImGuiStyle.DarkerColor(ImGuiStyle.Accent, 0x2A);
                        imguiDirty = true;
                        break;
                    case InterfaceColorOption.RegionColor:
                        ImGuiStyle.Dark2 = ImGuiStyle.Col(c.Red, c.Green, c.Blue);
                        imguiDirty = true;
                        break;
                    case InterfaceColorOption.SecondaryRegionColor:
                        ImGuiStyle.Dark3 = ImGuiStyle.Col(c.Red, c.Green, c.Blue);
                        imguiDirty = true;
                        break;
                    case InterfaceColorOption.BorderColor:
                        ImGuiStyle.Border      = ImGuiStyle.Col(c.Red, c.Green, c.Blue);
                        ImGuiStyle.BorderHover = ImGuiStyle.BrighterColor(ImGuiStyle.Border, 0x1B);
                        imguiDirty = true;
                        break;
                    case InterfaceColorOption.RadarBackground:
                        ImGuiStyle.RadarBg = ImGuiStyle.ESPColor(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.FuserBackground:
                        ImGuiStyle.ESPColorHUDTextBackground = ImGuiStyle.ESPColor(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.TextColor:
                        ImGuiStyle.Text = ImGuiStyle.Col(c.Red, c.Green, c.Blue);
                        imguiDirty = true;
                        break;
                    case InterfaceColorOption.ControlFill:
                        ImGuiStyle.ControlFill = ImGuiStyle.Col(c.Red, c.Green, c.Blue);
                        break;
                    case InterfaceColorOption.GroupBoxTitle:
                        ImGuiStyle.GroupTitleColor = ImGuiStyle.Col(c.Red, c.Green, c.Blue);
                        break;
                    case InterfaceColorOption.PanelBackground:
                        ImGuiStyle.PanelBg = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.InputBackground:
                        ImGuiStyle.InputBg    = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        ImGuiStyle.InputBgHov = ImGuiStyle.BrighterColor(ImGuiStyle.InputBg, 0x0E);
                        break;
                    case InterfaceColorOption.WidgetBackground:
                        ImGuiStyle.WidgetBg = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.ContextMenuBackground:
                        ImGuiStyle.ContextMenuBg = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.DropdownBackground:
                        ImGuiStyle.DropdownBg = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.ModalBackground:
                        ImGuiStyle.ModalBg = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.PopoverBackground:
                        ImGuiStyle.PopoverBg = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.ColorPickerBackground:
                        ImGuiStyle.ColorPickerBg = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.TooltipBackground:
                        ImGuiStyle.TooltipBg = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    // These four (unlike most of the panel-style options above) preserve
                    // c.Alpha - widget chrome is meant to support translucent backgrounds.
                    case InterfaceColorOption.WidgetChromeBackground:
                        ImGuiStyle.WidgetChromeBg = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.WidgetChromeTitleBackground:
                        ImGuiStyle.WidgetChromeTitleBg = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.WidgetChromeBorder:
                        ImGuiStyle.WidgetChromeBorder = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.WidgetChromeTitleText:
                        ImGuiStyle.WidgetChromeTitleText = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.WidgetChromeScrollThumb:
                        ImGuiStyle.WidgetChromeScrollThumb = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.MainPanelBackground:
                        ImGuiStyle.MainPanelBg = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.MainPanelTitleBackground:
                        ImGuiStyle.MainPanelTitleBg = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.MainPanelBorder:
                        ImGuiStyle.MainPanelBorder = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.MainPanelTitleText:
                        ImGuiStyle.MainPanelTitleText = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                    case InterfaceColorOption.MainPanelScrollThumb:
                        ImGuiStyle.MainPanelScrollThumb = ImGuiStyle.Col(c.Red, c.Green, c.Blue, c.Alpha);
                        break;
                }
            }
            if (imguiDirty) ImGuiStyle.ApplyThemeColors();
        }

        internal static void UpdateColor(Config config, InterfaceColorOption option, SKColor color)
        {
            var singleColor = new Dictionary<InterfaceColorOption, string>
            {
                [option] = color.ToString()
            };

            SetColors(singleColor);

            if (config.InterfaceColors == null)
                config.InterfaceColors = new Dictionary<InterfaceColorOption, string>();

            config.InterfaceColors[option] = color.ToString();
            config.SaveDebounced();
        }

        internal static void ResetToDefaults(Config config)
        {
            var defaults = GetDefaultColors();
            config.InterfaceColors = new Dictionary<InterfaceColorOption, string>(defaults);
            SetColors(config.InterfaceColors);
            config.SaveDebounced();
        }

        private static readonly InterfaceColorOption[] WidgetChromeOptions =
        {
            InterfaceColorOption.WidgetChromeBackground,
            InterfaceColorOption.WidgetChromeTitleBackground,
            InterfaceColorOption.WidgetChromeBorder,
            InterfaceColorOption.WidgetChromeTitleText,
            InterfaceColorOption.WidgetChromeScrollThumb,
        };

        /// <summary>Resets just the floating-widget chrome colors, leaving the rest of the theme alone.</summary>
        internal static void ResetWidgetColorsToDefaults(Config config)
        {
            var defaults = GetDefaultColors();
            config.InterfaceColors ??= new Dictionary<InterfaceColorOption, string>();
            foreach (var opt in WidgetChromeOptions)
                config.InterfaceColors[opt] = defaults[opt];
            SetColors(config.InterfaceColors);
            config.SaveDebounced();
        }

        private static readonly InterfaceColorOption[] MainPanelOptions =
        {
            InterfaceColorOption.MainPanelBackground,
            InterfaceColorOption.MainPanelTitleBackground,
            InterfaceColorOption.MainPanelBorder,
            InterfaceColorOption.MainPanelTitleText,
            InterfaceColorOption.MainPanelScrollThumb,
        };

        /// <summary>Resets just the main settings-panel chrome colors, leaving the rest of the theme alone.</summary>
        internal static void ResetMainPanelColorsToDefaults(Config config)
        {
            var defaults = GetDefaultColors();
            config.InterfaceColors ??= new Dictionary<InterfaceColorOption, string>();
            foreach (var opt in MainPanelOptions)
                config.InterfaceColors[opt] = defaults[opt];
            SetColors(config.InterfaceColors);
            config.SaveDebounced();
        }

        internal static bool TryGetColorOption(string tag, out InterfaceColorOption option)
        {
            switch (tag)
            {
                case "Interface.Accent":          option = InterfaceColorOption.AccentColor;          return true;
                case "Interface.Region":          option = InterfaceColorOption.RegionColor;          return true;
                case "Interface.SecondaryRegion": option = InterfaceColorOption.SecondaryRegionColor; return true;
                case "Interface.BorderColor":     option = InterfaceColorOption.BorderColor;          return true;
                case "Interface.RadarBackground": option = InterfaceColorOption.RadarBackground;      return true;
                case "Interface.FuserBackground": option = InterfaceColorOption.FuserBackground;      return true;
                case "Interface.TextColor":       option = InterfaceColorOption.TextColor;            return true;
                case "Interface.ControlFill":     option = InterfaceColorOption.ControlFill;          return true;
                case "Interface.GroupBoxTitle":   option = InterfaceColorOption.GroupBoxTitle;        return true;
                case "Interface.PanelBackground":       option = InterfaceColorOption.PanelBackground;       return true;
                case "Interface.InputBackground":       option = InterfaceColorOption.InputBackground;       return true;
                case "Interface.WidgetBackground":      option = InterfaceColorOption.WidgetBackground;      return true;
                case "Interface.ContextMenuBackground": option = InterfaceColorOption.ContextMenuBackground; return true;
                case "Interface.DropdownBackground":    option = InterfaceColorOption.DropdownBackground;    return true;
                case "Interface.ModalBackground":       option = InterfaceColorOption.ModalBackground;       return true;
                case "Interface.PopoverBackground":     option = InterfaceColorOption.PopoverBackground;     return true;
                case "Interface.ColorPickerBackground": option = InterfaceColorOption.ColorPickerBackground; return true;
                case "Interface.TooltipBackground":     option = InterfaceColorOption.TooltipBackground;     return true;
                case "Interface.WidgetChromeBackground":      option = InterfaceColorOption.WidgetChromeBackground;      return true;
                case "Interface.WidgetChromeTitleBackground": option = InterfaceColorOption.WidgetChromeTitleBackground; return true;
                case "Interface.WidgetChromeBorder":          option = InterfaceColorOption.WidgetChromeBorder;          return true;
                case "Interface.WidgetChromeTitleText":       option = InterfaceColorOption.WidgetChromeTitleText;       return true;
                case "Interface.WidgetChromeScrollThumb":     option = InterfaceColorOption.WidgetChromeScrollThumb;     return true;
                case "Interface.MainPanelBackground":      option = InterfaceColorOption.MainPanelBackground;      return true;
                case "Interface.MainPanelTitleBackground": option = InterfaceColorOption.MainPanelTitleBackground; return true;
                case "Interface.MainPanelBorder":          option = InterfaceColorOption.MainPanelBorder;          return true;
                case "Interface.MainPanelTitleText":       option = InterfaceColorOption.MainPanelTitleText;       return true;
                case "Interface.MainPanelScrollThumb":     option = InterfaceColorOption.MainPanelScrollThumb;     return true;
                default:                                option = InterfaceColorOption.AccentColor;           return false;
            }
        }

        internal static string GetTagFromOption(InterfaceColorOption option) => option switch
        {
            InterfaceColorOption.AccentColor          => "Interface.Accent",
            InterfaceColorOption.RegionColor          => "Interface.Region",
            InterfaceColorOption.SecondaryRegionColor => "Interface.SecondaryRegion",
            InterfaceColorOption.BorderColor          => "Interface.BorderColor",
            InterfaceColorOption.RadarBackground      => "Interface.RadarBackground",
            InterfaceColorOption.FuserBackground      => "Interface.FuserBackground",
            InterfaceColorOption.TextColor            => "Interface.TextColor",
            InterfaceColorOption.ControlFill          => "Interface.ControlFill",
            InterfaceColorOption.GroupBoxTitle        => "Interface.GroupBoxTitle",
            InterfaceColorOption.PanelBackground       => "Interface.PanelBackground",
            InterfaceColorOption.InputBackground       => "Interface.InputBackground",
            InterfaceColorOption.WidgetBackground      => "Interface.WidgetBackground",
            InterfaceColorOption.ContextMenuBackground => "Interface.ContextMenuBackground",
            InterfaceColorOption.DropdownBackground    => "Interface.DropdownBackground",
            InterfaceColorOption.ModalBackground       => "Interface.ModalBackground",
            InterfaceColorOption.PopoverBackground     => "Interface.PopoverBackground",
            InterfaceColorOption.ColorPickerBackground => "Interface.ColorPickerBackground",
            InterfaceColorOption.TooltipBackground     => "Interface.TooltipBackground",
            InterfaceColorOption.WidgetChromeBackground      => "Interface.WidgetChromeBackground",
            InterfaceColorOption.WidgetChromeTitleBackground => "Interface.WidgetChromeTitleBackground",
            InterfaceColorOption.WidgetChromeBorder          => "Interface.WidgetChromeBorder",
            InterfaceColorOption.WidgetChromeTitleText       => "Interface.WidgetChromeTitleText",
            InterfaceColorOption.WidgetChromeScrollThumb     => "Interface.WidgetChromeScrollThumb",
            InterfaceColorOption.MainPanelBackground      => "Interface.MainPanelBackground",
            InterfaceColorOption.MainPanelTitleBackground => "Interface.MainPanelTitleBackground",
            InterfaceColorOption.MainPanelBorder          => "Interface.MainPanelBorder",
            InterfaceColorOption.MainPanelTitleText       => "Interface.MainPanelTitleText",
            InterfaceColorOption.MainPanelScrollThumb     => "Interface.MainPanelScrollThumb",
            _                                          => string.Empty,
        };

        #endregion
    }
}
