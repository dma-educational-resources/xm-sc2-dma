using System.IO;
using System.Numerics;
using System.Text.Json;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Radar unit filtering, shared by the Unit Filter panel and the radar.
    /// Teams are keyed by team colour (ABGR, as in <see cref="UnitEntry.Color"/>); grey = neutral.
    /// Saved to unit_filter.json next to the radar config.
    /// </summary>
    /// <summary>A saved radar highlight: units matching <see cref="Pattern"/> get a ring in <see cref="Color"/>.</summary>
    public sealed class HighlightRule
    {
        public string Pattern { get; set; } = "";
        public uint   Color   { get; set; } = 0xFF00FFFF;   // ABGR
        public bool   Enabled { get; set; } = true;
    }

    public static class UnitFilter
    {
        public const uint NeutralColor = 0xFFAAAAAA;

        public sealed class State
        {
            public string                   Search      { get; set; } = "";
            public bool                     DimNonMatches { get; set; } = false; // search dims non-matches instead of hiding them
            public bool                     OnlyBurrowed  { get; set; } = false; // show burrowed units only
            public bool                     OnlyCloaked   { get; set; } = false; // show cloaked units only (with OnlyBurrowed: either)
            public List<string>             Blacklist     { get; set; } = [];    // name / model-path patterns never drawn ('*' wildcard)
            public HashSet<string>          BlacklistOff  { get; set; } = new(StringComparer.OrdinalIgnoreCase); // kept but switched off
            public List<HighlightRule>      Highlights    { get; set; } = [];    // ringed on the radar in their colour
            public bool                     ShowDamage    { get; set; } = true;  // HP bar on damaged units
            public bool                     ShowShields   { get; set; } = true;  // shield bar on units with damaged shields
            public bool                     ShowEnergy    { get; set; } = true;  // energy next to the label
            public bool                     ShowVitalText { get; set; } = true;  // HP / shield / energy numbers when zoomed in
            public bool                     MatchPath   { get; set; } = true;   // search the full model path too
            public bool                     ShowResources { get; set; } = true; // mineral fields, vespene geysers
            public bool                     ShowScenery { get; set; } = false;  // doodads (rocks, trees, props)
            public bool                     ShowOther   { get; set; } = false;  // decals, skybox, portraits, ...
            public bool                     ShowEffects { get; set; } = false;  // impacts, missiles, blood, weapon flashes
            public bool                     ShowRemains { get; set; } = false;  // dead units' death / ragdoll models (with Other on)
            public HashSet<string>          HiddenNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<uint>            HiddenTeams { get; set; } = [];
            public Dictionary<uint, string> TeamLabels  { get; set; } = [];
        }

        static readonly string File_ = Path.Combine(Program.DataDir, "unit_filter.json");
        public static State S { get; } = Load();

        /// <summary>Set by the panel ("Center on radar"), consumed by the radar on its next frame.</summary>
        public static Vector2? CenterRequest { get; set; }

        /// <summary>Name the panel row selection is focused on (radar draws a focus ring), or null.</summary>
        public static string? FocusName { get; set; }

        static string   _termsSrc = "";
        static string[] _terms    = [];

        /// <summary>Comma-separated search terms, lower-cased.</summary>
        static string[] Terms
        {
            get
            {
                var s = S.Search ?? "";
                if (!ReferenceEquals(s, _termsSrc))
                {
                    _termsSrc = s;
                    _terms = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }
                return _terms;
            }
        }

        public static bool HasSearch => Terms.Length > 0;

        public static bool IsMatch(string name)
        {
            foreach (var t in Terms)
                if (name.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsMatch(in UnitEntry u)
        {
            foreach (var t in Terms)
                if (IsBuffPattern(t) && BuffMatches(t, in u) || IsScriptPattern(t) && ScriptMatches(t, in u)) return true;
            return IsMatch(u.Name) || (u.TypeName.Length > 0 && (IsMatch(u.TypeName) || IsMatch(u.DisplayName))) || (S.MatchPath && IsMatch(u.Path));
        }

        /// <summary>Show-chip categories (Units / Workers / Buildings in Config, Scenery / Other here).</summary>
        public static bool CategoryShown(in UnitEntry u) => u.Category switch
        {
            UnitCategory.Building => Program.Config.ShowBuildings,
            UnitCategory.Resource => S.ShowResources,
            UnitCategory.Worker   => Program.Config.ShowWorkers,
            UnitCategory.Scenery  => S.ShowScenery,
            UnitCategory.Other    => S.ShowOther,
            UnitCategory.Effect   => S.ShowEffects,
            _                     => Program.Config.ShowUnits,   // Unit, Hero, Critter
        };

        // ── blacklist ────────────────────────────────────────────────────────
        /// <summary>Common effect / impact noise, offered as a one-click preset in the panel.</summary>
        public static readonly string[] EffectNoisePreset = ["physicsimpact*", "*impact*", "blood*", "*missile*"];

        static readonly Dictionary<string, System.Text.RegularExpressions.Regex?> _rxCache = new(StringComparer.Ordinal);

        /// <summary>
        /// Rule pattern match (blacklist and highlights). With '*' the pattern is a wildcard over the whole name
        /// (e.g. "physicsimpact*"); with a leading '=' it is an exact name ("=ghost"); otherwise a case-insensitive
        /// substring of the name or model path.
        /// </summary>
        public static bool PatternMatches(string pattern, in UnitEntry u)
        {
            string p = pattern.Trim();
            if (p.Length == 0) return false;
            if (IsBuffPattern(p)) return BuffMatches(p, in u);
            if (IsScriptPattern(p)) return ScriptMatches(p, in u);
            if (p[0] == '=') return string.Equals(u.Name, p[1..], StringComparison.OrdinalIgnoreCase);
            if (p.Contains('*'))
            {
                if (!_rxCache.TryGetValue(p, out var rx))
                    _rxCache[p] = rx = new System.Text.RegularExpressions.Regex(
                        "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*") + "$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                return rx!.IsMatch(u.Name);
            }
            return u.Name.Contains(p, StringComparison.OrdinalIgnoreCase) || u.Path.Contains(p, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"buff:Red": matches units with a buff / debuff (behaviour id, SC2Memory.UnitBuffs) - the part after the
        /// colon follows the usual rules ('=' exact, '*' wildcard, else part of the id). Works in the search, highlights,
        /// hide rules, markers and custom styles. Changes as buffs come and go, so callers that cache by name re-check it.</summary>
        public static bool IsBuffPattern(string p) => p.StartsWith("buff:", StringComparison.OrdinalIgnoreCase);

        /// <summary>"script:lib1_gv_units.lv_luckyLizard": matches the unit(s) a map-script variable holds (a unit or unitgroup
        /// variable, SC2Memory.GetScript) - e.g. the Special Forces lucky lizard. Works wherever buff: does.</summary>
        public static bool IsScriptPattern(string p) => p.StartsWith("script:", StringComparison.OrdinalIgnoreCase);

        /// <summary>Patterns whose matches change while the unit stays the same (buffs, script variables): callers that cache
        /// matches by name re-check these per unit.</summary>
        public static bool IsDynamicPattern(string p) => IsBuffPattern(p) || IsScriptPattern(p);

        static bool ScriptMatches(string pattern, in UnitEntry u)
        {
            string path = pattern.Trim()[7..].Trim();
            return path.Length > 0 && u.Record != 0 && DMA.Memory.Instance?.GetScript(path) is { } v && Array.IndexOf(v.Records, u.Record) >= 0;
        }

        static bool BuffMatches(string pattern, in UnitEntry u)
        {
            string p = pattern.Trim()[5..].Trim();
            if (p.Length == 0 || u.Record == 0 || DMA.Memory.Instance?.UnitBuffs.TryGetValue(u.Record, out var ids) != true) return false;
            foreach (var id in ids!)
            {
                if (p[0] == '=') { if (string.Equals(id, p[1..], StringComparison.OrdinalIgnoreCase)) return true; }
                else if (p.Contains('*')) { if (Wildcard(p).IsMatch(id)) return true; }
                else if (id.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static System.Text.RegularExpressions.Regex Wildcard(string p)
        {
            if (!_rxCache.TryGetValue(p, out var rx) || rx == null)
                _rxCache[p] = rx = new System.Text.RegularExpressions.Regex(
                    "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*") + "$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return rx;
        }

        /// <summary>Never drawn: a global blacklist pattern, or one of the active radar profile's Hide rules.</summary>
        public static bool IsBlacklisted(in UnitEntry u)
        {
            foreach (var p in S.Blacklist)
                if (!S.BlacklistOff.Contains(p) && PatternMatches(p, in u)) return true;
            foreach (var p in RadarProfiles.Active.Hide)
                if (PatternMatches(p, in u)) return true;
            return false;
        }

        public static void AddToBlacklist(string pattern)
        {
            pattern = pattern.Trim();
            if (pattern.Length == 0 || S.Blacklist.Contains(pattern, StringComparer.OrdinalIgnoreCase)) return;
            S.Blacklist.Add(pattern);
            SaveDebounced();
        }

        // ── highlights ───────────────────────────────────────────────────────
        /// <summary>Ring colours handed out to new highlights (ABGR).</summary>
        public static readonly uint[] HighlightPalette =
            [0xFF00FFFF, 0xFFFF00FF, 0xFFFFFF00, 0xFF0080FF, 0xFF00FF00, 0xFF8080FF, 0xFFFF8000, 0xFFFFFFFF];

        /// <summary>Ring colour of the first enabled highlight rule matching this unit (the active profile's first, then
        /// the global ones), or null.</summary>
        public static uint? HighlightOf(in UnitEntry u)
        {
            foreach (var h in RadarProfiles.Active.Highlights)
                if (h.Enabled && PatternMatches(h.Pattern, in u)) return h.Color;
            foreach (var h in S.Highlights)
                if (h.Enabled && PatternMatches(h.Pattern, in u)) return h.Color;
            return null;
        }

        public static HighlightRule? FindHighlight(string pattern) =>
            S.Highlights.FirstOrDefault(h => string.Equals(h.Pattern, pattern.Trim(), StringComparison.OrdinalIgnoreCase));

        public static void AddHighlight(string pattern, uint? color = null)
        {
            pattern = pattern.Trim();
            if (pattern.Length == 0 || FindHighlight(pattern) != null) return;
            S.Highlights.Add(new HighlightRule
            {
                Pattern = pattern,
                Color   = color ?? HighlightPalette[S.Highlights.Count % HighlightPalette.Length],
            });
            SaveDebounced();
        }

        public static void RemoveHighlight(string pattern)
        {
            if (FindHighlight(pattern) is { } h) { S.Highlights.Remove(h); SaveDebounced(); }
        }

        /// <summary>"Burrowed only" / "Cloaked only": with both on, either state passes.</summary>
        public static bool PassesHiddenFilter(in UnitEntry u) =>
            !(S.OnlyBurrowed || S.OnlyCloaked) || (S.OnlyBurrowed && u.Burrowed) || (S.OnlyCloaked && u.Cloaked);

        /// <summary>Drawn at all? (A search hides non-matches unless DimNonMatches is set.)</summary>
        public static bool IsVisible(in UnitEntry u)
        {
            if (!CategoryShown(in u)) return false;
            if (IsBlacklisted(in u)) return false;
            if (!PassesHiddenFilter(in u)) return false;
            if (S.HiddenTeams.Contains(u.Color)) return false;
            if (S.HiddenNames.Contains(u.Name))  return false;
            if (HasSearch && !S.DimNonMatches && !IsMatch(in u)) return false;
            return true;
        }

        /// <summary>Drawn faded: a search is active, dimming is on, and this one doesn't match.</summary>
        public static bool IsDimmed(in UnitEntry u) => HasSearch && S.DimNonMatches && !IsMatch(in u);

        public static string TeamLabel(uint color) =>
            S.TeamLabels.TryGetValue(color, out var l) && l.Length > 0 ? l
            : color == NeutralColor ? "Neutral"
            : $"Team {ColorName(color)}";

        /// <summary>Rough colour name for an unlabelled team (ABGR input).</summary>
        static string ColorName(uint abgr)
        {
            float r = (abgr & 0xFF) / 255f, g = ((abgr >> 8) & 0xFF) / 255f, b = ((abgr >> 16) & 0xFF) / 255f;
            float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
            if (max - min < 0.12f) return max > 0.6f ? "white" : max > 0.3f ? "grey" : "black";
            float h = max == r ? (g - b) / (max - min) : max == g ? 2f + (b - r) / (max - min) : 4f + (r - g) / (max - min);
            h = (h * 60f + 360f) % 360f;
            return h switch
            {
                < 15f  => "red",    < 40f  => "orange", < 70f  => "yellow", < 160f => "green",
                < 200f => "teal",   < 250f => "blue",   < 290f => "purple", < 335f => "pink",
                _      => "red",
            };
        }

        static State Load()
        {
            try
            {
                if (File.Exists(File_))
                {
                    var s = JsonSerializer.Deserialize<State>(File.ReadAllText(File_));
                    if (s != null)
                    {
                        // JSON loses the comparer
                        s.HiddenNames  = new HashSet<string>(s.HiddenNames, StringComparer.OrdinalIgnoreCase);
                        s.BlacklistOff = new HashSet<string>(s.BlacklistOff ?? [], StringComparer.OrdinalIgnoreCase);
                        s.Highlights ??= [];
                        return s;
                    }
                }
            }
            catch { }
            return new();
        }

        static System.Threading.Timer? _saveTimer;
        /// <summary>Bumped on every change (every change saves): the memory reader re-checks which actors to read.</summary>
        public static int Version => _version;
        static volatile int _version;

        /// <summary>
        /// Not worth reading at all: decoration (scenery, other, effects - never a player's unit, so no widget, alert or
        /// memory write needs it) that is never drawn: a dead unit's remains while Remains is off, a hidden type, or a
        /// blacklist / profile Hide pattern (not buff: / script:, which need the unit read). Units, workers, buildings and
        /// resources are always read, drawn or not.
        /// </summary>
        public static bool SkipReading(string name, string path, UnitCategory category, bool remains)
        {
            if (category is not (UnitCategory.Scenery or UnitCategory.Other or UnitCategory.Effect)) return false;
            if (remains && !S.ShowRemains) return true;
            if (S.HiddenNames.Contains(name)) return true;
            var probe = new UnitEntry(0f, 0f, 0u, name, category, path, 0UL);
            foreach (var p in S.Blacklist)
                if (!S.BlacklistOff.Contains(p) && !IsDynamicPattern(p) && PatternMatches(p, in probe)) return true;
            foreach (var p in RadarProfiles.Active.Hide)
                if (!IsDynamicPattern(p) && PatternMatches(p, in probe)) return true;
            return false;
        }

        public static void SaveDebounced()
        {
            _version++;
            string json;   // serialised here, on the thread that changed the data, not on the timer's thread
            try { json = JsonSerializer.Serialize(S, new JsonSerializerOptions { WriteIndented = true }); } catch { return; }
            _saveTimer?.Dispose();
            _saveTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
                    if (!SettingsBackup.BeforeSave(File_)) return;
                    File.WriteAllText(File_, json);
                }
                catch { }
            }, null, 1000, -1);
        }
    }
}
