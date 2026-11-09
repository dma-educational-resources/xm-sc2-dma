namespace SC2Radar.DMA.Features
{
    /// <summary>
    /// A feature driven by <see cref="SC2Radar.SC2.Features.FeatureManager"/>: lifecycle notifications for the game
    /// process and for matches. All of them run on the features worker, under the manager's lock.
    /// </summary>
    public interface IFeature
    {
        /// <summary>The feature may run this tick (game state, master switch, its own throttle).</summary>
        bool CanRun { get; }
        /// <summary>Called after each <see cref="IMemWriteFeature.TryApply"/> (restarts the throttle).</summary>
        void OnApply();
        /// <summary>The radar attached to a (new) SC2 process.</summary>
        void OnGameStart();
        /// <summary>A match is running (the clock started, units loaded, the local player known).</summary>
        void OnMatchStart();
        /// <summary>The match ended (or went away): anything cached for it is stale, there is nothing to put back.</summary>
        void OnMatchEnd();
        /// <summary>The SC2 process went away.</summary>
        void OnGameStop();
    }
}
