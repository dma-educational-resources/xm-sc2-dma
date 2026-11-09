using SC2Radar.DMA;
using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Display colour of a unit. Default: each player's own colour - the actor's colour, or, when the game itself is
    /// in team-colour mode (local player's units drawn green), SC2's standard colour for the player's slot (a player's
    /// default colour index is its player number, see PlayerGetColorIndex). With Alliance colours on: local player
    /// green, allies yellow, enemies red (the game's alliance table, SC2Memory.IsAlly). Neutral and unresolved units
    /// keep the actor colour.
    /// </summary>
    public static class PlayerColors
    {
        public const uint Self = 0xFF00BB00, Ally = 0xFF00FFFF, Enemy = 0xFF0000FF;   // ABGR, as the game's team colours

        // SC2 player colours by colour index (ABGR); dark grey and brown lifted a little to read on the radar background
        static readonly uint[] Palette =
        [
            0xFFAAAAAA, Abgr(180, 20, 30), Abgr(0, 66, 255), Abgr(28, 167, 234), Abgr(84, 0, 129), Abgr(235, 225, 41),
            Abgr(254, 138, 14), Abgr(22, 128, 0), Abgr(204, 166, 252), Abgr(31, 1, 201), Abgr(82, 84, 148), Abgr(16, 98, 70),
            Abgr(140, 80, 20), Abgr(150, 255, 145), Abgr(110, 110, 110), Abgr(229, 91, 176),
        ];

        static uint Abgr(int r, int g, int b) => 0xFF000000u | (uint)(b << 16) | (uint)(g << 8) | (uint)r;

        /// <summary>True while the game draws team colours (its own setting), so actor colours aren't per player.</summary>
        public static bool GameTeamColors { get; private set; }

        /// <summary>Once per frame: detect the game's team-colour mode from the local player's units.</summary>
        public static void Update(UnitEntry[] units)
        {
            int local = Memory.Instance?.LocalPlayer ?? -1;
            if (local < 0) { GameTeamColors = false; return; }
            int green = 0, other = 0;
            foreach (ref readonly var u in units.AsSpan())
                if (u.Owner == local && u.Record != 0)
                {
                    if (u.Color == Self) green++; else other++;
                }
            GameTeamColors = green > other;
        }

        /// <param name="alliance">Alliance or player colours regardless of the setting (the web radar picks its own); null = the setting.</param>
        public static uint For(in UnitEntry u, bool? alliance = null)
        {
            if (u.Owner is 0 or >= 16 || u.Record == 0) return u.Color;
            var mem = Memory.Instance;
            if ((alliance ?? Program.Config.AllianceColors) && mem is { LocalPlayer: >= 0, HasAllianceTable: true })
                return u.Owner == mem.LocalPlayer ? Self : mem.IsAlly(u.Owner, mem.LocalPlayer) ? Ally : Enemy;
            return GameTeamColors ? Palette[u.Owner] : u.Color;
        }

        /// <summary>A player's colour in the current mode (for widgets and the vision overlay).</summary>
        public static uint ForPlayer(int player, uint actorColor, bool? alliance = null)
        {
            var mem = Memory.Instance;
            if (player is <= 0 or >= 16) return actorColor;
            if ((alliance ?? Program.Config.AllianceColors) && mem is { LocalPlayer: >= 0, HasAllianceTable: true })
                return player == mem.LocalPlayer ? Self : mem.IsAlly(player, mem.LocalPlayer) ? Ally : Enemy;
            return GameTeamColors ? Palette[player] : actorColor;
        }
    }
}
