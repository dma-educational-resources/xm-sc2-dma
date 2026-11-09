using SC2Radar.DMA.Features;
using Ctl = SC2Radar.UI.ImGui.UI;

namespace SC2Radar.SC2.Features.MemoryWrites
{
    /// <summary>
    /// Unit sight (experimental): every unit and building you own sees at least as far as the slider says (types that
    /// already see further keep their own). Sight lives in the unit type's catalog entry, read live by the game's vision
    /// update (there is no per unit copy), so it changes per type. A type whose entry another player's units use too is
    /// left alone (and put back if one shows up). Synced simulation state like Reveal Map: turning it off restores every
    /// type. The game caps sight at about 32.
    /// </summary>
    public sealed class UnitSight : MemWriteFeature<UnitSight>
    {
        public override string Name         => "Unit Sight";
        public override string Description  => "Forces your units vision & detection radius; will cause dropping in online play";
        public override string Group        => "Vision";
        public override bool   Risky        => true;
        public override string RiskyWarning => "May cause drop from game with other players.";
        // new unit types (a first Overlord, a morph) pick it up within a second
        protected override TimeSpan Delay  => TimeSpan.FromSeconds(1);

        static UnitSightSettings Settings => Program.Config.MemWrites.UnitSight;

        readonly Dictionary<ulong, (string Type, int Original)> _changed = [];   // catalog entry -> type, the game's own Sight (fixed)
        readonly Dictionary<ulong, (ulong Record, ushort Original, ushort Written)> _detChanged = [];   // detector stack count address -> unit, the game's count, ours

        UnitSight() { }

        protected override void Apply(SC2Memory mem, ScatterWriteHandle writes)
        {
            int local = mem.LocalPlayer;
            // one sample unit per (type, owner): a type's units share their catalog entry, so that's all the reads needed
            var samples = mem.Snapshot.Units.Where(u => u.Record != 0 && u.TypeName.Length > 0 && u.Owner is > 0 and < 16)
                                             .DistinctBy(u => (u.TypeName, u.Owner)).ToList();
            var entryOf = samples.ToDictionary(u => (u.TypeName, u.Owner), u => mem.UnitTypeEntry(u.Record));
            var shared = samples.Where(u => u.Owner != local).Select(u => entryOf[(u.TypeName, u.Owner)]).Where(e => e != 0).ToHashSet();
            int want = (int)MathF.Round(Settings.Sight * 4096f);

            // entries we changed: someone else now uses it, or the game reset it (new match) - hand back / forget;
            // a lower slider than a type's own sight - give it its own back
            foreach (var (entry, ch) in _changed.ToList())
            {
                int? now = mem.ReadTypeSightFx(entry);
                bool ours = now != ch.Original;
                if (shared.Contains(entry) || ch.Original >= want)
                {
                    if (ours) mem.WriteTypeSightFx(entry, ch.Original, writes);
                    _changed.Remove(entry);
                }
                else if (!ours && now != want) _changed.Remove(entry);
            }

            var skipped = new List<string>();
            foreach (var u in samples.Where(u => u.Owner == local))
            {
                ulong entry = entryOf[(u.TypeName, u.Owner)];
                if (entry == 0) continue;
                if (shared.Contains(entry)) { skipped.Add(u.TypeName); continue; }
                if (mem.ReadTypeSightFx(entry) is not { } now || now == want) continue;
                int original = _changed.TryGetValue(entry, out var ch) ? ch.Original : now;
                if (original >= want) continue;   // already sees at least that far
                _changed[entry] = (u.TypeName, original);
                mem.WriteTypeSightFx(entry, want, writes);
            }
            int detectors = Settings.Detection ? ApplyDetection(mem, local, writes) : RevertDetection(mem, writes);
            Status = (_changed.Count == 0 ? "Nothing below the slider" : $"{_changed.Count} unit types at {Settings.Sight:0}") +
                     (skipped.Count > 0 ? $"; skipped (opponent's too): {string.Join(", ", skipped.Distinct().Select(PrettyNames.Get))}" : "") +
                     (detectors > 0 ? $"; {detectors} detectors at {Settings.Sight:0}+" : "");
        }

        /// <summary>Detection with the sight: a detector's range is Detect x the stack count on its own behaviour instance
        /// (SC2Memory.DetectorStacks), so the count goes up to the first multiple at or past the slider - your units only,
        /// the catalog entry the AI's detectors share is left alone. Returns how many detectors are raised.</summary>
        int ApplyDetection(SC2Memory mem, int local, ScatterWriteHandle writes)
        {
            var live = mem.Snapshot.Units.Where(u => u.Record != 0 && u.Owner == local && u.Detector).Select(u => u.Record).ToHashSet();
            // gone units: forget without writing (their memory may be reused)
            foreach (var (addr, d) in _detChanged.ToList()) if (!live.Contains(d.Record)) _detChanged.Remove(addr);
            foreach (ulong rec in live)
                foreach (var s in mem.DetectorStacks(rec))
                {
                    ushort original = _detChanged.TryGetValue(s.CountAddr, out var d) && s.Count == d.Written ? d.Original : s.Count;
                    ushort want = (ushort)Math.Clamp((int)MathF.Ceiling(Settings.Sight / s.Detect), original, 15);
                    if (want == original) { if (_detChanged.Remove(s.CountAddr) && s.Count != original) mem.WriteU16(s.CountAddr, original, writes); continue; }
                    _detChanged[s.CountAddr] = (rec, original, want);
                    if (s.Count != want) mem.WriteU16(s.CountAddr, want, writes);
                }
            return _detChanged.Count;
        }

        /// <summary>Put the game's stack counts back (only where our value is still there).</summary>
        int RevertDetection(SC2Memory mem, ScatterWriteHandle writes)
        {
            foreach (var (addr, d) in _detChanged)
                if (mem.ReadU16(addr) == d.Written) mem.WriteU16(addr, d.Original, writes);
            _detChanged.Clear();
            return 0;
        }

        protected override void Revert(SC2Memory mem, ScatterWriteHandle writes)
        {
            foreach (var (entry, ch) in _changed)
                if (mem.ReadTypeSightFx(entry) is { } now && now != ch.Original) mem.WriteTypeSightFx(entry, ch.Original, writes);
            _changed.Clear();
            RevertDetection(mem, writes);
        }

        protected override void Forget()
        {
            _changed.Clear();
            _detChanged.Clear();
        }

        public override Action? DrawSettings => () =>
        {
            Ctl.BeginSliderGroup("Sight");
            float s = Settings.Sight;
            if (Ctl.SliderFloat("Sight##unit_sight", ref s, 5f, 32f, "%.0f", smallChange: 1f,
                    hint: "How far your units and buildings see at least; types that see further keep their own. The game caps sight at about 32."))
            { Settings.Sight = s; Program.Config.SaveDebounced(); ApplySoon(); }
            Ctl.EndSliderGroup();
            bool det = Settings.Detection;
            if (Ctl.Toggle("Detection too##unit_sight_det", ref det,
                    hint: "Your detectors also detect at least as far as the slider (in steps of their own range: 10, 20, 30 ...). Only your units; the AI's detectors are left alone."))
            { Settings.Detection = det; Program.Config.SaveDebounced(); ApplySoon(); }
            Ctl.Caption("Changes every unit of a type you own at once.");
            Ctl.KVRow("Status", Status);
        };
    }
}
