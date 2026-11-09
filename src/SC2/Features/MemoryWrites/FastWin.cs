using SC2Radar.DMA.Features;

namespace SC2Radar.SC2.Features.MemoryWrites
{
    /// <summary>
    /// Fast win / tie (experimental, a button): the local player's type is set to spectator (SC2Memory.SetPlayerType);
    /// the melee win check then finds no player left to oppose and ends the game - a win against the AI ("Fast Win"),
    /// with other people in the game it reads "Fast Tie". Held until the match ends, then forgotten (the next match has
    /// new player objects). One press: there is no cancel or timeout; the master switch still puts the type back.
    /// </summary>
    public sealed class FastWin : MemWriteFeature<FastWin>
    {
        public override string Name         => "Fast Win/Tie Hack";
        public override string Description  => "Forces win in VS AI; Forces Tie in Ladder. Doesn't work in Arcade.";
        public override string Group        => "Other";
        public override bool   IsAction     => true;
        public override string ActionLabel => (Memory.Instance is { } m && m.MatchLive && !m.OnlyHumanInGame ? "Fast Tie" : "Fast Win") + (Enabled ? " (ending...)" : "");

        int _player = -1;     // whose type we changed, -1 when none
        DateTime _armed;
        float _startClock;    // the match clock when it fired: going backwards means a new match

        FastWin() { }

        /// <summary>Holding the local player as a spectator until the match ends (writes stay allowed meanwhile).</summary>
        public bool Holding => _player > 0;

        public override void Trigger()
        {
            if (Enabled) return;   // already ending the match: one press is enough
            if (Memory.Instance is not { MemWritesAllowed: true }) return;   // not in a match
            _armed = DateTime.UtcNow;
            Enabled = true;        // Apply picks it up on the next tick
        }

        protected override void Apply(SC2Memory mem, ScatterWriteHandle writes)
        {
            int local = mem.LocalPlayer;
            if (_player < 0)
            {
                // only for the match running when it was pressed - never a later one
                if (DateTime.UtcNow - _armed > TimeSpan.FromSeconds(3)) { Enabled = false; return; }
                if (mem.PlayerType(local) != SC2Memory.PlayerTypeUser) { Enabled = false; return; }   // unexpected type: not touched
                if (!mem.SetPlayerType(local, SC2Memory.PlayerTypeSpectator, writes)) { Enabled = false; return; }
                _player = local;
                _startClock = mem.GameTime;
                return;
            }
            // the match is over once the mission is gone, the local player changed, or the clock restarted (a new match:
            // its player objects are new, so there is nothing to put back)
            if (mem.Snapshot.Units.Length == 0 || local != _player || mem.GameTime < _startClock - 1f)
            { Forget(); return; }
            if (mem.PlayerType(_player) != SC2Memory.PlayerTypeSpectator) mem.SetPlayerType(_player, SC2Memory.PlayerTypeSpectator, writes);
        }

        protected override void Revert(SC2Memory mem, ScatterWriteHandle writes)
        {
            if (_player > 0 && mem.PlayerType(_player) == SC2Memory.PlayerTypeSpectator)
                mem.SetPlayerType(_player, SC2Memory.PlayerTypeUser, writes);
            Forget();
        }

        /// <summary>The match it was ending is over (or a press never found one): done.</summary>
        protected override void Forget()
        {
            _player = -1;
            Enabled = false;
        }
    }
}
