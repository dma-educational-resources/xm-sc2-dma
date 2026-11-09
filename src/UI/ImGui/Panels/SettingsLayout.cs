using SC2Radar.Common.Misc.Data;

namespace SC2Radar.UI.ImGui.Panels
{
    /// <summary>
    /// The settings panels' layout, as the EFT radar's General panel lays out controls: toggles in a two-column grid
    /// (<see cref="Grid"/>), sliders one per row with their label in a shared left column
    /// (<see cref="UI.BeginSliderGroup"/>), two related sliders side by side (<see cref="UI.BeginColumns"/>).
    /// </summary>
    internal static class SettingsLayout
    {
        /// <summary>A config toggle for <see cref="Grid"/>: saves the config when flipped.</summary>
        public static UI.GridItem Toggle(string label, Func<bool> get, Action<bool> set, string? hint = null, HotkeyEntry? hotkey = null) =>
            new(w =>
            {
                bool v = get();
                if (UI.Toggle(label, ref v, w, hint: hint, hotkey: hotkey)) { set(v); Program.Config.SaveDebounced(); }
            });

        /// <summary>Toggles (or colour edits, full-width items) in the group's two-column grid.</summary>
        public static void Grid(params UI.GridItem[] items) => UI.DrawGrid(UI.GroupInnerWidth, items);
    }
}
