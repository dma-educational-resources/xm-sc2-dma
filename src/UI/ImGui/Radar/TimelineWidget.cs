using System.Numerics;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Widgets;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// What each enemy got and when ("4:10 Spire", "5:30 Mutalisk", "6:02 Lair (started)"), newest first - the first time
    /// each unit / structure type is on the field (SC2.MatchStats.Timeline). What was already there when the radar first
    /// saw the match isn't listed. Allies too from the title-bar menu.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class TimelineWidget : WidgetChrome
    {
        protected override string  WidgetTitle => "Timeline";
        protected override Vector2 InitSize    => new(300, 260);
        protected override bool    HasOpacityOverride => Program.Config.TimelineWidgetOverrideOpacity;
        protected override float   OpacityOverride    => Program.Config.TimelineWidgetAlpha;
        protected override bool    HasPanelMenu => true;

        static Config Cfg => Program.Config;

        protected override void DrawPanelMenu()
        {
            if (UI.MenuItem("Include allies and me", selected: Cfg.TimelineAllPlayers))
            { Cfg.TimelineAllPlayers = !Cfg.TimelineAllPlayers; Cfg.SaveDebounced(); }
            if (UI.MenuItem("Include workers and buildings", selected: Cfg.TimelineBuildings, hint: "Off: units only (and tech morphs)."))
            { Cfg.TimelineBuildings = !Cfg.TimelineBuildings; Cfg.SaveDebounced(); }
        }

        protected override void DrawWidgetContent()
        {
            ImGuiNET.ImGui.Indent(ContentTopGap);
            var mem = Memory.Instance;
            int local = mem?.LocalPlayer ?? -1;
            bool enemiesKnown = local > 0 && mem is { HasAllianceTable: true };
            var events = MatchStats.Timeline
                .Where(e => e.Time > 0f)
                .Where(e => Cfg.TimelineAllPlayers || !enemiesKnown || !mem!.IsAlly(e.Owner, local))
                .Where(e => Cfg.TimelineBuildings || e.Started || !IsStructureOrWorker(e.Type))
                .Reverse().Take(200).ToList();

            if (!Memory.InGame || events.Count == 0)
            {
                ImGuiNET.ImGui.TextDisabled(Memory.InGame ? "Nothing new fielded yet." : "No match.");
                ImGuiNET.ImGui.Unindent(ContentTopGap);
                return;
            }
            foreach (var e in events)
            {
                ImGuiNET.ImGui.TextDisabled(SC2Memory.FormatClock(e.Time));
                ImGuiNET.ImGui.SameLine(ContentTopGap + 48f);
                var (name, col) = ObserverWidget.PlayerLabel(e.Owner);
                ImGuiNET.ImGui.TextColored(UI.ToV4(col), e.Started ? $"{e.Type} (started)" : e.Type);
                if (ImGuiNET.ImGui.IsItemHovered()) UI.Tooltip(name);
            }
            ImGuiNET.ImGui.Unindent(ContentTopGap);
        }

        /// <summary>The timeline's names are display names; buildings and workers by the names the melee races use.</summary>
        static bool IsStructureOrWorker(string name) => Structures.Contains(name);

        static readonly HashSet<string> Structures = new(StringComparer.OrdinalIgnoreCase)
        {
            "SCV", "Probe", "Drone", "MULE",
            "Command Center", "Orbital Command", "Planetary Fortress", "Supply Depot", "Supply Depot Lowered", "Refinery", "Barracks",
            "Engineering Bay", "Bunker", "Missile Turret", "Sensor Tower", "Factory", "Ghost Academy", "Armory", "Starport", "Fusion Core",
            "Tech Lab", "Reactor", "Barracks Tech Lab", "Barracks Reactor", "Factory Tech Lab", "Factory Reactor", "Starport Tech Lab", "Starport Reactor",
            "Nexus", "Pylon", "Assimilator", "Gateway", "Warp Gate", "Forge", "Cybernetics Core", "Photon Cannon", "Shield Battery",
            "Twilight Council", "Robotics Facility", "Robotics Bay", "Stargate", "Templar Archives", "Dark Shrine", "Fleet Beacon",
            "Hatchery", "Extractor", "Spawning Pool", "Evolution Chamber", "Spine Crawler", "Spore Crawler", "Roach Warren",
            "Baneling Nest", "Lair", "Hydralisk Den", "Infestation Pit", "Spire", "Nydus Network", "Lurker Den MP", "Ultralisk Cavern",
            "Hive", "Greater Spire",
        };
    }
}
