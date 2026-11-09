using System.Numerics;
using SC2Radar.DMA.Features;
using SC2Radar.SC2.Features;
using SC2Radar.UI.ImGui.Panels;

namespace SC2Radar.UI.ImGui
{
    /// <summary>
    /// Memory write features: one toggle each (description on hover, settings and status behind the cog), grouped by
    /// <see cref="IMemWriteFeature.Group"/> in a two-column grid. Everything is gated by the title bar master switch;
    /// toggles and the switch go through <see cref="FeatureManager"/>.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class MemWritesPanel : ChromePanel
    {
        protected override Vector2 InitSize   => new(420f, 200f);
        protected override string  PanelTitle => "Memory Writes";

        protected override (string Label, bool Value, Action<bool> OnChange)? TitleMasterSwitch =>
            ("Master Switch", Config.MemWritesEnabled, FeatureManager.SetMasterSwitch);

        protected override string TitleMasterSwitchHint =>
            "Master enable/disable for all memory write features. Turning off puts back what they changed and stops all writes.";

        protected override void DrawPanelContent()
        {
            if (!Config.MemWritesEnabled)
            {
                UI.BeginGroup("Memory Writes Disabled");
                UI.Caption("Memory write features are unavailable while the master switch is off.", style: LabelStyle.Warning);
                UI.Caption("Use the Master Switch in the title bar to enable memory writes.");
                UI.EndGroup();
                return;
            }

            foreach (var group in FeatureManager.All.GroupBy(f => f.Group))
            {
                UI.BeginGroup(group.Key);
                UI.DrawGrid(UI.GroupInnerWidth, [.. group.Select(f => new UI.GridItem(w => DrawFeature(f, w)))]);
                UI.EndGroup();
            }
        }

        void DrawFeature(IMemWriteFeature feature, float width)
        {
            string id = $"##mw_{feature.Name}";
            if (feature.IsAction)
            {
                // one-shot: a button; while it runs it reads as secondary
                if (UI.Button(feature.ActionLabel + id, new Vector2(width, 0f),
                        feature.Enabled ? ButtonStyle.Ghost : ButtonStyle.Accent,
                        hint: feature.Description,
                        risky: feature.Risky, riskyWarning: feature.RiskyWarning))
                    FeatureManager.Trigger(feature);
                return;
            }
            bool enabled = feature.Enabled;
            var hotkey = Config.HotKeys.ForFeature(feature);
            bool flipped = feature.DrawSettings is { } settings
                ? UI.ToggleWithPopover(feature.Name + id, ref enabled, settings, width: width, hint: feature.Description, hotkey: hotkey,
                        risky: feature.Risky, riskyWarning: feature.RiskyWarning)
                : UI.Toggle(feature.Name + id, ref enabled, width, hint: feature.Description, hotkey: hotkey,
                        risky: feature.Risky, riskyWarning: feature.RiskyWarning);
            if (flipped) FeatureManager.SetEnabled(feature, enabled);
        }
    }
}
