using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SC2Radar.DMA;

namespace SC2Radar.SC2
{
    /// <summary>What a unit is drawn as: the radar's per-type style list (General settings ▸ Visuals ▸ Entity Info).</summary>
    public enum EntityKind { Army, Worker, Building, Mineral, Geyser, Neutral }

    /// <summary>Race a unit belongs to, from its model path ("assets\units\protoss\...").</summary>
    public enum Faction { Any, Terran, Protoss, Zerg }

    public enum MarkerShape { Circle, Square, Diamond, Triangle, Hexagon, Ring }

    /// <summary>When a vitals bar is drawn.</summary>
    public enum BarMode { Never, WhenDamaged, Always }

    /// <summary>How one kind of entity is drawn.</summary>
    public sealed class EntityStyle
    {
        public bool        Show       { get; set; } = true;
        public MarkerShape Shape      { get; set; } = MarkerShape.Circle;
        public float       Size       { get; set; } = 3.5f;     // radius in pixels
        public bool        TeamColor  { get; set; } = true;     // the owner's colour; off: Color
        public uint        Color      { get; set; } = 0xFFFFFFFF;   // ABGR
        public bool        Outline    { get; set; } = true;
        public bool        Label      { get; set; } = true;     // name once zoomed in
        public BarMode     Health     { get; set; } = BarMode.WhenDamaged;
        public BarMode     Shields    { get; set; } = BarMode.WhenDamaged;
        public bool        Energy     { get; set; } = true;     // energy figure next to the label
        public bool        Amount     { get; set; } = true;     // minerals / gas left (resources)

        public EntityStyle Clone() => (EntityStyle)MemberwiseClone();
    }

    /// <summary>
    /// A game-mode specific marker: units whose name / type / model path matches <see cref="Pattern"/> (same rules as
    /// the Unit Filter: '*' wildcard, '=' exact name, else a substring) are drawn with this shape, colour and size and a
    /// glow ring, over their type style - e.g. the boxes in Special Forces, or a mode's hero units.
    /// </summary>
    public sealed class MarkerRule
    {
        public string      Pattern { get; set; } = "";
        public bool        Enabled { get; set; } = true;
        public string      Category { get; set; } = "";     // "Loot", "Bosses", ... - toggled together (RadarProfile.HiddenCategories)
        public string      Label   { get; set; } = "";       // shown instead of the unit's name ("" = its name)
        public MarkerShape Shape   { get; set; } = MarkerShape.Diamond;
        public uint        Color   { get; set; } = 0xFF00FFFF;   // ABGR
        public float       Size    { get; set; } = 6f;
        public bool        Glow    { get; set; } = true;
        public bool        AlwaysLabel { get; set; } = true;  // label at any zoom
        public int         HealthBelow { get; set; } = 0;     // only while health is under this % (0 = any health)
        public int         Alert       { get; set; } = 0;     // when a unit starts matching: 0 nothing, 1 a toast, 2 a toast and a sound (MarkerAlerts)
    }

    /// <summary>
    /// A named look for specific units (Visuals ▸ Entity Info ▸ Custom): units matching any of <see cref="Pattern"/>'s
    /// comma-separated patterns (name, type or model path - '=' exact, '*' wildcard, "buff:Red" for a buff) are drawn
    /// with <see cref="Style"/> instead of their kind / race style - e.g. Spine and Spore Crawlers as plain dots without
    /// labels while other Zerg buildings keep theirs. The first enabled match wins.
    /// </summary>
    public sealed class CustomStyle
    {
        public string      Name    { get; set; } = "";
        public string      Pattern { get; set; } = "";
        public bool        Enabled { get; set; } = true;
        public EntityStyle Style   { get; set; } = new();

        public IEnumerable<string> Patterns => Pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// A named set of entity styles and markers. Each profile lists map-name fragments ("Special Forces", "Nexus Wars");
    /// the first profile matching the current map is used, else the Default profile.
    /// </summary>
    public sealed class RadarProfile
    {
        public string Name { get; set; } = "Default";
        public List<string> MapMatch { get; set; } = [];
        /// <summary>Keyed by StyleKey: "Army", or "Army.Protoss" for a faction's override of it.</summary>
        public Dictionary<string, EntityStyle> Styles { get; set; } = RadarProfiles.DefaultStyles();
        public List<MarkerRule> Markers { get; set; } = [];
        /// <summary>Named styles for specific units, over the kind / race styles (first enabled match wins).</summary>
        public List<CustomStyle> CustomStyles { get; set; } = [];
        /// <summary>Marker categories switched off: their markers don't apply.</summary>
        public HashSet<string> HiddenCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Widgets this profile keeps closed (Production in an arcade mode where it means nothing), by title.</summary>
        public HashSet<string> HiddenWidgets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The layout preset (Config.LayoutPresets) applied when this profile takes over, "" = leave the layout alone.</summary>
        public string LayoutPreset { get; set; } = "";
        /// <summary>Unit Filter rules for this profile only, on top of the global ones: patterns never drawn, and highlight rings.</summary>
        public List<string> Hide { get; set; } = [];
        public List<HighlightRule> Highlights { get; set; } = [];
    }

    public static class RadarProfiles
    {
        public sealed class State
        {
            public List<RadarProfile> Profiles { get; set; } = [new RadarProfile()];
            /// <summary>Name of the profile to use regardless of the map, "" = pick by map.</summary>
            public string Forced { get; set; } = "";
        }

        static readonly string File_ = Path.Combine(Program.DataDir, "radar_profiles.json");
        // before S: static initialisers run in order, and Load needs these (enum names are strings in the file)
        static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        public static State S { get; } = Load();

        /// <summary>Bumped on every edit: caches keyed on it throw themselves away.</summary>
        public static int Version { get; private set; }

        public static string StyleKey(EntityKind kind, Faction faction = Faction.Any) =>
            faction == Faction.Any ? kind.ToString() : $"{kind}.{faction}";

        public static Dictionary<string, EntityStyle> DefaultStyles() => new()
        {
            ["Army"]     = new() { Shape = MarkerShape.Circle,  Size = 3.5f },
            ["Worker"]   = new() { Shape = MarkerShape.Circle,  Size = 2.6f, Label = false },
            ["Building"] = new() { Shape = MarkerShape.Square,  Size = 5f },
            ["Mineral"]  = new() { Shape = MarkerShape.Diamond, Size = 3.2f, TeamColor = false, Color = 0xFFFFC870, Label = false, Health = BarMode.Never, Shields = BarMode.Never },
            ["Geyser"]   = new() { Shape = MarkerShape.Hexagon, Size = 5f,   TeamColor = false, Color = 0xFF70D870, Label = false, Health = BarMode.Never, Shields = BarMode.Never },
            ["Neutral"]  = new() { Shape = MarkerShape.Circle,  Size = 2.5f, TeamColor = false, Color = 0xFFAAAAAA, Label = false },
        };

        // ── the active profile ───────────────────────────────────────────────

        static string? _activeMap, _activeForced;
        static int _activeVersion = -1;
        static RadarProfile? _active;

        /// <summary>The profile for the current map (or the forced one). Called per unit per frame: no allocation unless the
        /// map, the override or a profile changed.</summary>
        public static RadarProfile Active
        {
            get
            {
                string map = Memory.Instance?.MapName ?? "";
                if (_active == null || !ReferenceEquals(map, _activeMap) && map != _activeMap || S.Forced != _activeForced || Version != _activeVersion)
                {
                    (_activeMap, _activeForced, _activeVersion) = (map, S.Forced, Version);
                    _active = (S.Forced.Length > 0 ? S.Profiles.FirstOrDefault(p => p.Name == S.Forced) : null)
                              ?? S.Profiles.FirstOrDefault(p => p.MapMatch.Any(m => m.Length > 0 && map.Contains(m, StringComparison.OrdinalIgnoreCase)))
                              ?? Default;
                    _styleCache.Clear(); _markerCache.Clear(); _customCache.Clear();
                }
                return _active;
            }
        }

        public static RadarProfile Default
        {
            get
            {
                var d = S.Profiles.FirstOrDefault(p => p.Name == "Default");
                if (d == null) { d = new RadarProfile(); S.Profiles.Insert(0, d); }
                return d;
            }
        }

        // ── classifying a unit ───────────────────────────────────────────────

        public static EntityKind KindOf(in UnitEntry u) => u.Category switch
        {
            UnitCategory.Resource => IsGeyser(in u) ? EntityKind.Geyser : EntityKind.Mineral,
            UnitCategory.Building => EntityKind.Building,
            UnitCategory.Worker   => EntityKind.Worker,
            _ when u.Record != 0 && u.Owner is 0 or >= 16 => EntityKind.Neutral,
            UnitCategory.Critter or UnitCategory.Scenery or UnitCategory.Other or UnitCategory.Effect => EntityKind.Neutral,
            _                     => EntityKind.Army,
        };

        public static bool IsGeyser(in UnitEntry u) =>
            u.Name.Contains("geyser", StringComparison.OrdinalIgnoreCase) || u.TypeName.Contains("Geyser", StringComparison.OrdinalIgnoreCase) ||
            u.TypeName.Contains("Vespene", StringComparison.OrdinalIgnoreCase);

        public static Faction FactionOf(in UnitEntry u) =>
            u.Path.Contains(@"\protoss\", StringComparison.OrdinalIgnoreCase) ? Faction.Protoss
            : u.Path.Contains(@"\terran\", StringComparison.OrdinalIgnoreCase) ? Faction.Terran
            : u.Path.Contains(@"\zerg\", StringComparison.OrdinalIgnoreCase) ? Faction.Zerg
            : Faction.Any;

        static readonly Dictionary<(EntityKind, Faction), EntityStyle> _styleCache = [];

        /// <summary>The style for a kind / faction in the active profile: the faction's override when it has one.</summary>
        public static EntityStyle StyleFor(EntityKind kind, Faction faction)
        {
            var p = Active;
            if (_styleCache.TryGetValue((kind, faction), out var s)) return s;
            s = (faction != Faction.Any ? p.Styles.GetValueOrDefault(StyleKey(kind, faction)) : null)
                ?? p.Styles.GetValueOrDefault(StyleKey(kind))
                ?? DefaultStyles()[StyleKey(kind)];
            _styleCache[(kind, faction)] = s;
            return s;
        }

        /// <summary>A unit's style: its custom style when one matches, else its kind / race style.</summary>
        public static EntityStyle StyleFor(in UnitEntry u) => CustomFor(in u)?.Style ?? StyleFor(KindOf(in u), FactionOf(in u));

        // per (name, type): the custom styles that could match - by name for good, buff patterns re-checked per unit
        static readonly Dictionary<(string, string), (CustomStyle Rule, bool Dynamic)[]> _customCache = [];

        /// <summary>The first enabled custom style of the active profile matching this unit, or null.</summary>
        public static CustomStyle? CustomFor(in UnitEntry u)
        {
            var p = Active;
            if (p.CustomStyles.Count == 0) return null;
            var key = (u.Name, u.TypeName);
            if (!_customCache.TryGetValue(key, out var rules))
            {
                var list = new List<(CustomStyle, bool)>();
                foreach (var c in p.CustomStyles)
                {
                    if (!c.Enabled) continue;
                    bool dynamic = false, hit = false;
                    foreach (var pat in c.Patterns)
                    {
                        if (UnitFilter.IsDynamicPattern(pat)) { dynamic = true; continue; }
                        if (UnitFilter.PatternMatches(pat, in u) || u.TypeName.Length > 0 && TypeMatches(pat, u.TypeName)) { hit = true; break; }
                    }
                    if (hit) list.Add((c, false));
                    else if (dynamic) list.Add((c, true));
                }
                _customCache[key] = rules = [.. list];
            }
            foreach (var (c, dynamic) in rules)
            {
                if (!dynamic) return c;
                foreach (var pat in c.Patterns)
                    if (UnitFilter.IsDynamicPattern(pat) && UnitFilter.PatternMatches(pat, in u)) return c;
            }
            return null;
        }

        // per (name, type): the enabled markers whose pattern matches, in order; the health condition is checked per unit
        static readonly Dictionary<(string, string), MarkerRule[]> _markerCache = [];

        /// <summary>The first enabled marker of the active profile matching this unit (pattern, and health when the rule
        /// has a "below" limit), or null.</summary>
        public static MarkerRule? MarkerFor(in UnitEntry u)
        {
            var p = Active;
            if (p.Markers.Count == 0) return null;
            var key = (u.Name, u.TypeName);
            if (!_markerCache.TryGetValue(key, out var rules))
            {
                var list = new List<MarkerRule>();
                foreach (var r in p.Markers)
                    if (r.Enabled && (string.IsNullOrEmpty(r.Category) || !p.HiddenCategories.Contains(r.Category)) && (UnitFilter.IsDynamicPattern(r.Pattern) || UnitFilter.PatternMatches(r.Pattern, in u) ||
                                      u.TypeName.Length > 0 && TypeMatches(r.Pattern, u.TypeName)))
                        list.Add(r);
                _markerCache[key] = rules = [.. list];
            }
            foreach (var r in rules)
                if ((r.HealthBelow <= 0 || u.HpFrac >= 0f && u.HpFrac * 100f < r.HealthBelow) &&
                    (!UnitFilter.IsDynamicPattern(r.Pattern) || UnitFilter.PatternMatches(r.Pattern, in u))) return r;   // buffs come and go: per unit
            return null;
        }

        /// <summary>Patterns also match the game's type name ("Hero*" against "HeroMarine").</summary>
        static bool TypeMatches(string pattern, string type)
        {
            string p = pattern.Trim();
            if (p.Length == 0) return false;
            if (p[0] == '=') return string.Equals(type, p[1..], StringComparison.OrdinalIgnoreCase);
            if (p.Contains('*'))
                return System.Text.RegularExpressions.Regex.IsMatch(type, "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*") + "$",
                                                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return type.Contains(p, StringComparison.OrdinalIgnoreCase);
        }

        // ── editing ──────────────────────────────────────────────────────────

        /// <summary>Call after any edit: drops the caches and saves shortly after.</summary>
        public static void Changed()
        {
            Version++;
            _styleCache.Clear(); _markerCache.Clear(); _customCache.Clear();
            SaveDebounced();
        }

        public static RadarProfile AddProfile(string name, RadarProfile? copyOf = null)
        {
            string unique = name;
            for (int i = 2; S.Profiles.Any(p => p.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); i++) unique = $"{name} {i}";
            var src = copyOf ?? Default;
            var p = new RadarProfile
            {
                Name = unique,
                MapMatch = [],
                Styles = src.Styles.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
                Markers = [.. src.Markers.Select(m => new MarkerRule
                {
                    Pattern = m.Pattern, Enabled = m.Enabled, Label = m.Label, Shape = m.Shape, Color = m.Color,
                    Size = m.Size, Glow = m.Glow, AlwaysLabel = m.AlwaysLabel, Category = m.Category, HealthBelow = m.HealthBelow, Alert = m.Alert,
                })],
                CustomStyles = [.. src.CustomStyles.Select(c => new CustomStyle { Name = c.Name, Pattern = c.Pattern, Enabled = c.Enabled, Style = c.Style.Clone() })],
                HiddenCategories = new(src.HiddenCategories, StringComparer.OrdinalIgnoreCase),
                HiddenWidgets = new(src.HiddenWidgets, StringComparer.OrdinalIgnoreCase),
                LayoutPreset = src.LayoutPreset,
                Hide = [.. src.Hide],
                Highlights = [.. src.Highlights.Select(h => new HighlightRule { Pattern = h.Pattern, Color = h.Color, Enabled = h.Enabled })],
            };
            S.Profiles.Add(p);
            Changed();
            return p;
        }

        public static void RemoveProfile(RadarProfile p)
        {
            if (p.Name == "Default") return;
            S.Profiles.Remove(p);
            if (S.Forced == p.Name) S.Forced = "";
            Changed();
        }


        public static string FilePath => File_;

        // ── sharing: one profile per file in an exports folder ──────────────

        public static string ExportFolder => Path.Combine(Program.DataDir, "profile-exports");

        /// <summary>Write a profile to "&lt;exports&gt;\&lt;name&gt;.json"; returns the path, null on failure.</summary>
        public static string? Export(RadarProfile p)
        {
            try
            {
                Directory.CreateDirectory(ExportFolder);
                string safe = string.Concat(p.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                string path = Path.Combine(ExportFolder, safe + ".json");
                File.WriteAllText(path, JsonSerializer.Serialize(p, Json));
                return path;
            }
            catch { return null; }
        }

        /// <summary>The profile files in the exports folder (yours, or ones a friend sent), by name.</summary>
        public static string[] ExportFiles() =>
            Directory.Exists(ExportFolder) ? [.. Directory.GetFiles(ExportFolder, "*.json").Order()] : [];

        /// <summary>Add the profile in a file (renamed if the name is taken); null when the file isn't a profile.</summary>
        public static RadarProfile? Import(string path)
        {
            try
            {
                if (JsonSerializer.Deserialize<RadarProfile>(File.ReadAllText(path), Json) is not { } p) return null;
                p.Styles ??= DefaultStyles();
                foreach (var (k, v) in DefaultStyles()) p.Styles.TryAdd(k, v);
                p.MapMatch ??= []; p.Markers ??= []; p.HiddenCategories = new(p.HiddenCategories ?? [], StringComparer.OrdinalIgnoreCase); p.HiddenWidgets = new(p.HiddenWidgets ?? [], StringComparer.OrdinalIgnoreCase); p.LayoutPreset ??= ""; p.Hide ??= []; p.Highlights ??= [];
                if (string.IsNullOrWhiteSpace(p.Name) || p.Name == "Default") p.Name = Path.GetFileNameWithoutExtension(path);
                string unique = p.Name;
                for (int i = 2; S.Profiles.Any(x => x.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); i++) unique = $"{p.Name} {i}";
                p.Name = unique;
                S.Profiles.Add(p);
                Changed();
                return p;
            }
            catch { return null; }
        }

        /// <summary>Why the profiles file would fail to load (research / --profiles), or its path when it loads.</summary>
        public static string LoadProblem()
        {
            try { var s = JsonSerializer.Deserialize<State>(File.ReadAllText(File_), Json); return $"{File_}: loads, " + string.Join("; ", s!.Profiles.Select(p => $"{p.Name}: {p.Markers.Count} markers")); }
            catch (Exception ex) { return $"{File_}: {ex.GetType().Name}: {ex.Message}"; }
        }

        static State Load()
        {
            try
            {
                if (File.Exists(File_) && JsonSerializer.Deserialize<State>(File.ReadAllText(File_), Json) is { } s)
                {
                    s.Profiles ??= [];
                    if (!s.Profiles.Any(p => p.Name == "Default")) s.Profiles.Insert(0, new RadarProfile());
                    foreach (var p in s.Profiles)
                    {
                        p.Styles ??= DefaultStyles();
                        foreach (var (k, v) in DefaultStyles()) p.Styles.TryAdd(k, v);   // kinds added since the file was saved
                        p.MapMatch ??= []; p.Markers ??= []; p.HiddenCategories = new(p.HiddenCategories ?? [], StringComparer.OrdinalIgnoreCase); p.HiddenWidgets = new(p.HiddenWidgets ?? [], StringComparer.OrdinalIgnoreCase); p.LayoutPreset ??= ""; p.Hide ??= []; p.Highlights ??= [];
                    }
                    return s;
                }
            }
            catch (Exception ex) { XMLogging.WriteLine($"[Profiles] {File_} didn't load, using defaults: {ex.Message}"); }
            return new();
        }

        static System.Threading.Timer? _saveTimer;
        static void SaveDebounced()
        {
            string json;   // serialised here, on the thread that changed the data, not on the timer's thread
            try { json = JsonSerializer.Serialize(S, Json); } catch { return; }
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
