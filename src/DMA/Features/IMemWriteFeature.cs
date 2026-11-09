using SC2Radar.DMA.ScatterAPI;
using SC2Radar.SC2;

namespace SC2Radar.DMA.Features
{
    /// <summary>A memory-write feature: one toggle (or button) in the Memory Writes panel.</summary>
    public interface IMemWriteFeature : IFeature
    {
        string Name        { get; }
        string Description { get; }
        /// <summary>The panel group the toggle sits in ("Display", "Vision", "Other").</summary>
        string Group       { get; }
        bool   Enabled     { get; set; }
        /// <summary>What the feature is doing right now, for its settings popover.</summary>
        string Status      { get; }

        /// <summary>A one-shot action: drawn as a button that calls <see cref="Trigger"/>; <see cref="Enabled"/> then
        /// means "running", and the feature switches it back off when done.</summary>
        bool   IsAction    { get; }
        /// <summary>The button text of an action.</summary>
        string ActionLabel { get; }
        void   Trigger();

        /// <summary>Non-null to draw the toggle with a settings cog; the action draws the popover.</summary>
        Action? DrawSettings { get; }

        /// <summary>True to render the panel label in risky-orange and show <see cref="RiskyWarning"/> in the tooltip.</summary>
        bool    Risky         { get; }
        /// <summary>Warning shown beneath the hint in risky-orange when <see cref="Risky"/> is true.</summary>
        string? RiskyWarning  { get; }

        /// <summary>
        /// Queue this tick's writes (reads may happen right away). Puts back what the feature changed once it is
        /// turned off. Must not throw.
        /// </summary>
        void TryApply(SC2Memory mem, ScatterWriteHandle writes);

        /// <summary>Queue the writes that put back everything the feature changed (the master switch is turning off).</summary>
        void Restore(SC2Memory mem, ScatterWriteHandle writes);
    }
}
