using SC2Radar.DMA.Features;
using Ctl = SC2Radar.UI.ImGui.UI;

namespace SC2Radar.SC2.Features.MemoryWrites
{
    /// <summary>
    /// Share enemy vision (experimental): every opponent shares its vision with the local player, as an ally that ticked
    /// "share vision" would - you see what their units see, and so the units themselves, while the fog stays everywhere
    /// else. A bit in both copies of the alliance table's vision row (SC2Memory.SetAllianceBits) - the vision update
    /// reads the second, transposed copy every update. Synced simulation state like Reveal Map; turning it off clears
    /// exactly the bits it set.
    /// </summary>
    public sealed class ShareEnemyVision : MemWriteFeature<ShareEnemyVision>
    {
        public override string Name         => "Share Enemy Vision";
        public override string Description  => "Opponents share their vision with you, you can see their units & what they see. Works on ladder; causes drops on some arcade modes";
        public override string Group        => "Vision";
        public override bool   Risky        => true;
        public override string RiskyWarning => "May cause drop from game with other players.";
        protected override TimeSpan Delay  => TimeSpan.FromMilliseconds(500);

        static ShareEnemyVisionSettings Settings => Program.Config.MemWrites.EnemyVision;

        readonly HashSet<int> _set = [];   // players whose vision row we gave the local player's bit
        int _local = -1;
        bool? _transposeOk;                // checked once per match

        ShareEnemyVision() { }

        protected override void Apply(SC2Memory mem, ScatterWriteHandle writes)
        {
            if (!mem.HasAllianceTable) { Status = "Unavailable in this build"; return; }
            int local = mem.LocalPlayer;
            if (local != _local) { Forget(); _local = local; }
            // both copies of the table, or nothing: the vision update reads the second one (SC2Memory.SetAllianceBit)
            _transposeOk ??= mem.AllianceTransposeOk(local);
            if (_transposeOk != true) { Status = "Unavailable: the alliance tables don't look as expected in this game version"; return; }
            var opponents = mem.Snapshot.Units.Where(u => u.Owner is > 0 and < 16 && u.Owner != local && !mem.IsAlly(u.Owner, local))
                                              .Select(u => (int)u.Owner).Distinct().ToList();
            // neutral (player 0) owns the map's own objects - crates, pickups, critters - and usually sees all of them
            if (Settings.IncludeNeutral) opponents.Add(0);
            else if (_set.Remove(0))
            {
                // the rest on the next run: both writes would touch the same transposed word in one batch
                mem.SetAllianceBit(SC2Memory.AllianceVision, 0, local, false, writes);
                ApplySoon();
                return;
            }
            // already shared (by us, or the map itself): nothing to write
            var add = opponents.Where(p => !mem.AllianceBitBoth(SC2Memory.AllianceVision, p, local)).ToList();
            if (mem.SetAllianceBits(SC2Memory.AllianceVision, add, local, true, writes)) _set.UnionWith(add);
            int players = _set.Count(p => p != 0);
            Status = opponents.Count == 0 ? "No opponents seen yet"
                   : $"Seeing what {players} opponent{(players == 1 ? "" : "s")} see{(players == 1 ? "s" : "")}{(_set.Contains(0) ? ", plus neutral" : "")}";
        }

        protected override void Revert(SC2Memory mem, ScatterWriteHandle writes)
        {
            if (_local > 0) mem.SetAllianceBits(SC2Memory.AllianceVision, _set, _local, false, writes);
            Forget();
        }

        protected override void Forget()
        {
            _set.Clear();
            _local = -1;
            _transposeOk = null;
        }

        public override Action? DrawSettings => () =>
        {
            bool neutral = Settings.IncludeNeutral;
            if (Ctl.Toggle("Include neutral##ev_neutral", ref neutral,
                    hint: "Also share the neutral player's vision: the map's own objects (crates, pickups, critters), which it usually sees all of."))
            { Settings.IncludeNeutral = neutral; Program.Config.SaveDebounced(); ApplySoon(); }
            Ctl.Caption("You see what they see - units they have no vision of themselves stay hidden.");
            Ctl.KVRow("Status", Status);
        };
    }
}
