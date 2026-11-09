using SC2Radar.DMA.ScatterAPI;
using SC2Radar.SC2;

namespace SC2Radar.DMA.Features
{
    /// <summary>
    /// Base of every memory-write feature (the EFT radar's pattern): a singleton registered with the
    /// <see cref="SC2Radar.SC2.Features.FeatureManager"/>, which calls <see cref="TryApply"/> once per memory tick with
    /// one shared <see cref="ScatterWriteHandle"/> and executes all features' writes in a single scatter write.
    /// <para>
    /// The flow is the same for every feature: while <see cref="Enabled"/>, <see cref="Apply"/> runs every
    /// <see cref="Delay"/>; on the tick after it is turned off, <see cref="Restore"/> puts back what it changed. When a
    /// match ends <see cref="OnMatchEnd"/> forgets that state without writing (the next match has new objects).
    /// Reads happen right away; writes are only queued, so a feature checks a write's effect on its next run.
    /// </para>
    /// </summary>
    public abstract class MemWriteFeature<T> : IMemWriteFeature
        where T : MemWriteFeature<T>
    {
        /// <summary>Singleton instance.</summary>
        public static T Instance { get; } = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;

        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private bool _due = true;
        private bool _applied;   // Apply has run since the feature was turned on: Restore is owed when it turns off
        private bool _ran;       // Apply ran this tick: OnApply restarts the throttle

        public abstract string Name        { get; }
        public abstract string Description { get; }
        public virtual  string Group       => "Display";
        private volatile bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set { if (value && !_enabled) _due = true; _enabled = value; }   // turning on applies right away
        }

        private volatile string _status = "-";
        public string Status
        {
            get => Enabled || IsAction ? _status : "Off";
            protected set => _status = value;
        }

        public virtual bool    IsAction     => false;
        public virtual string  ActionLabel  => Name;
        public virtual void    Trigger()    { }
        public virtual Action? DrawSettings => null;
        public virtual bool    Risky         => false;
        public virtual string? RiskyWarning  => null;

        /// <summary>How often <see cref="Apply"/> runs while enabled (zero: every memory tick).</summary>
        protected virtual TimeSpan Delay => TimeSpan.Zero;

        protected bool DelayElapsed => _due || Delay == TimeSpan.Zero || _sw.Elapsed >= Delay;

        /// <summary>Run <see cref="Apply"/> on the next tick instead of waiting out <see cref="Delay"/> (a setting changed).</summary>
        protected void ApplySoon() => _due = true;

        /// <summary>
        /// Writes are allowed in a live match with a human in it (<see cref="SC2Memory.MemWritesAllowed"/>, checked once per tick), never
        /// on a menu or loading screen. A feature that works outside matches overrides this.
        /// </summary>
        public virtual bool CanRun => Program.Config.MemWritesEnabled && SC2Radar.SC2.Features.FeatureManager.MatchWritable;

        public void TryApply(SC2Memory mem, ScatterWriteHandle writes)
        {
            if (!Enabled)
            {
                Restore(mem, writes);
                return;
            }
            if (!DelayElapsed) return;
            _applied = _ran = true;
            _due = false;
            Apply(mem, writes);
        }

        /// <summary>Queue this run's writes. Runs on the features worker; must not throw.</summary>
        protected abstract void Apply(SC2Memory mem, ScatterWriteHandle writes);

        /// <summary>Queue the writes that put back what <see cref="Apply"/> changed (once after it ran), and forget it.</summary>
        public void Restore(SC2Memory mem, ScatterWriteHandle writes)
        {
            if (!_applied) return;
            _applied = false;
            Revert(mem, writes);
            Status = "-";
        }

        /// <summary>Queue the writes that put back what <see cref="Apply"/> changed, and forget it.</summary>
        protected virtual void Revert(SC2Memory mem, ScatterWriteHandle writes) { }

        /// <summary>Forget what was changed without writing anything (the objects it lived in are gone).</summary>
        protected virtual void Forget() { }

        public void OnApply()
        {
            if (!_ran) return;
            _ran = false;
            _sw.Restart();
        }

        public virtual void OnGameStart() { }
        public virtual void OnMatchStart() { ApplySoon(); }
        public virtual void OnMatchEnd() { Forget(); _applied = false; Status = "-"; }
        public virtual void OnGameStop() { Forget(); _applied = false; Status = "-"; }
    }
}
