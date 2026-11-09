using SC2Radar.DMA.Features;
using Ctl = SC2Radar.UI.ImGui.UI;

namespace SC2Radar.SC2.Features.MemoryWrites
{
    /// <summary>
    /// Reveal map (experimental): turns fog of war off for the local player only, the way a map trigger's
    /// VisEnable(c_visTypeFog, false) does, by adding the player to the game's fog-off mask (SC2Memory.Fog.cs). The
    /// game's own vision update then shows the whole map, units included. That mask is synced simulation state, so it
    /// desyncs a game with other people in it. Turning it off takes the player back out of the mask.
    /// </summary>
    public sealed class RevealMap : MemWriteFeature<RevealMap>
    {
        public override string Name         => "Reveal Map";
        public override string Description  => "Removes Fog of War for you. Only works in VS AI; causes drop in multiplayer";
        public override string Group        => "Vision";
        public override bool   Risky        => true;
        public override string RiskyWarning => "May cause drop from game with other players.";
        protected override TimeSpan Delay  => TimeSpan.FromMilliseconds(500);

        int _player = -1;   // the player we added to the mask, -1 when none

        RevealMap() { }

        protected override void Apply(SC2Memory mem, ScatterWriteHandle writes)
        {
            if (!mem.HasFogSwitch) { Status = "Unavailable in this build"; return; }
            if (mem.FogDisabledMask() is not { } mask) { Status = "Waiting for a game"; _player = -1; return; }
            int p = mem.LocalPlayer;
            if ((mask & (1u << p)) != 0)
            {
                // a map's VisEnable sets every bit; just ours is a leftover of an earlier run, taken back on turning off
                if (mask != uint.MaxValue) _player = p;
                Status = _player == p ? "Fog of war off" : "Fog already off (map)";
                return;
            }
            // the bit is missing: first time, or the write didn't take - checked again on the next run
            _player = mem.SetFogDisabledMask(mask | (1u << p), writes) ? p : -1;
            Status = _player > 0 ? "Turning fog of war off" : "Write failed";
        }

        protected override void Revert(SC2Memory mem, ScatterWriteHandle writes)
        {
            if (_player > 0 && mem.HasFogSwitch && mem.FogDisabledMask() is { } mask && (mask & (1u << _player)) != 0)
                mem.SetFogDisabledMask(mask & ~(1u << _player), writes);
            _player = -1;
        }

        protected override void Forget() => _player = -1;

        public override Action? DrawSettings => () =>
        {
            Ctl.Caption("Only for games where you are the only human. Fog comes back when you turn this off.");
            Ctl.KVRow("Status", Status);
        };
    }
}
