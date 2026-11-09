using SC2Radar.DMA.Features;
using Ctl = SC2Radar.UI.ImGui.UI;

namespace SC2Radar.SC2.Features.MemoryWrites
{
    /// <summary>
    /// Observer view (experimental): holds the client's local player id (SC2Memory.LocalPlayer.cs, local info +0x0C) at
    /// 16, the id an observer client has - the game then draws the map as an observer sees it, every player's units and
    /// vision, and the local player's units can't be controlled. The id says which player this PC is, so unlike the
    /// alliance / fog writes it isn't shared simulation state (each client has its own); whether anything synced reads
    /// it is what testing has to show. Turning it off writes the real id back.
    /// </summary>
    public sealed class ObserverView : MemWriteFeature<ObserverView>
    {
        public override string Name        => "Observer View";
        public override string Description => "Forces your game client to act as Observer, showing production panel, showing enemy units etc; does not allow controlling your own units";
        public override string Group       => "Vision";
        protected override TimeSpan Delay  => TimeSpan.FromMilliseconds(250);

        ObserverView() { }

        protected override void Apply(SC2Memory mem, ScatterWriteHandle writes)
        {
            if (mem.RawLocalId() is not { } raw) { Status = "Waiting for a game"; return; }
            if (raw == SC2Memory.ObserverId)
            {
                Status = mem.ObserverRealPlayer > 0 ? $"Observing (you are player {mem.ObserverRealPlayer})" : "Already an observer";
                return;
            }
            if (raw is <= 0 or > 15) { Status = $"Unexpected local id {raw}"; return; }
            // a player id: the first time, or a new match - remember who we are, then become the observer
            mem.ObserverRealPlayer = raw;
            Status = mem.WriteLocalId(SC2Memory.ObserverId, writes) ? $"Observing (you are player {raw})" : "Write failed";
        }

        protected override void Revert(SC2Memory mem, ScatterWriteHandle writes)
        {
            if (mem.ObserverRealPlayer > 0 && mem.RawLocalId() == SC2Memory.ObserverId)
                mem.WriteLocalId((byte)mem.ObserverRealPlayer, writes);
            mem.ObserverRealPlayer = -1;
        }

        protected override void Forget()
        {
            if (Memory.Instance is { } mem) mem.ObserverRealPlayer = -1;
        }

        public override Action? DrawSettings => () =>
        {
            Ctl.Caption("You see the game as an observer: the whole map and everyone's units. Your units can't be controlled until you turn this off. Test vs AI first.");
            Ctl.KVRow("Status", Status);
        };
    }
}
