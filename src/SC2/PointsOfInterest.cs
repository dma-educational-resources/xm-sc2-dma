using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SC2Radar.UI.ImGui;

namespace SC2Radar.SC2
{
    /// <summary>One point of interest: a named spot on a map (map units, as the radar's X / Y).</summary>
    public sealed class POIEntry
    {
        public string Id          { get; set; } = Guid.NewGuid().ToString("N");
        public string Name        { get; set; } = "New POI";
        public string Description { get; set; } = "";
        public bool   Enabled     { get; set; } = true;
        public float  X           { get; set; }
        public float  Y           { get; set; }
    }

    /// <summary>
    /// A named collection of per-map POI lists ("Special Forces boxes" with the box spots on each of its maps). Maps are
    /// keyed by the map's name as the game shows it (SC2Memory.MapName) - custom maps have no other stable id.
    /// </summary>
    public sealed class POIGroup
    {
        public string Id      { get; set; } = Guid.NewGuid().ToString("N");
        public string Name    { get; set; } = "New POI Group";
        public bool   Enabled { get; set; } = true;

        public Dictionary<string, List<POIEntry>> Maps
        {
            get => _maps;
            set => _maps = new(value ?? [], StringComparer.OrdinalIgnoreCase);   // JSON hands back a case-sensitive one
        }
        Dictionary<string, List<POIEntry>> _maps = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Maps whose POIs are hidden even while the group is on.</summary>
        public HashSet<string> DisabledMaps
        {
            get => _disabled;
            set => _disabled = new(value ?? [], StringComparer.OrdinalIgnoreCase);
        }
        HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

        [JsonIgnore] public int TotalCount => Maps.Values.Sum(l => l.Count);

        public int CountForMap(string map) => map.Length > 0 && Maps.TryGetValue(map, out var l) ? l.Count : 0;

        public bool IsMapEnabled(string map) => map.Length == 0 || !DisabledMaps.Contains(map);

        public void ToggleMapEnabled(string map)
        {
            if (map.Length == 0) return;
            if (!DisabledMaps.Remove(map)) DisabledMaps.Add(map);
        }

        /// <summary>The POI list for <paramref name="map"/>, created on first use.</summary>
        public List<POIEntry> GetOrCreateMapList(string map)
        {
            if (map.Length == 0) map = "_global";
            if (!Maps.TryGetValue(map, out var list)) Maps[map] = list = [];
            return list;
        }
    }

    /// <summary>
    /// Points of interest (the EFT radar's PoiManager): every group, saved to points_of_interest.json in the data folder,
    /// apart from the settings and the radar profiles. Edited in the Points of Interest panel and from the radar's
    /// right-click menu; drawn on the radar (Config.ShowPOIs).
    /// </summary>
    public static class PoiManager
    {
        public static string FilePath => Path.Combine(Program.DataDir, "points_of_interest.json");

        static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true, PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static List<POIGroup> Groups { get; private set; } = Load();

        static List<POIGroup> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<List<POIGroup>>(File.ReadAllText(FilePath), Json) ?? [];
            }
            catch (Exception ex) { XMLogging.WriteLine($"[POI] Load failed: {ex.Message}"); }
            return [];
        }

        static System.Threading.Timer? _saveTimer;
        public static void SaveDebounced()
        {
            string json;   // serialised here, on the thread that changed the data, not on the timer's thread
            try { json = JsonSerializer.Serialize(Groups, Json); } catch (Exception ex) { XMLogging.WriteLine($"[POI] Save failed: {ex.Message}"); return; }
            _saveTimer?.Dispose();
            _saveTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    if (!SettingsBackup.BeforeSave(FilePath)) return;
                    File.WriteAllText(FilePath, json);
                }
                catch (Exception ex) { XMLogging.WriteLine($"[POI] Save failed: {ex.Message}"); }
            }, null, 1000, -1);
        }

        /// <summary>Every enabled POI on <paramref name="map"/> across the enabled groups.</summary>
        public static IEnumerable<POIEntry> EnabledForMap(string map)
        {
            if (map.Length == 0) yield break;
            foreach (var g in Groups)
                if (g.Enabled && g.IsMapEnabled(map) && g.Maps.TryGetValue(map, out var list))
                    foreach (var p in list)
                        if (p.Enabled) yield return p;
        }

        /// <summary>The group a POI belongs to (and the map it is on), or null.</summary>
        public static (POIGroup Group, string Map)? Owner(POIEntry poi)
        {
            foreach (var g in Groups)
                foreach (var (map, list) in g.Maps)
                    if (list.Contains(poi)) return (g, map);
            return null;
        }

        /// <summary>Removes a POI from whichever group holds it.</summary>
        public static void Remove(POIEntry poi)
        {
            if (Owner(poi) is not { } o) return;
            o.Group.Maps[o.Map].Remove(poi);
            SaveDebounced();
        }

        public static string ExportJson(object groupOrGroups) => JsonSerializer.Serialize(groupOrGroups, Json);

        /// <summary>A group object or an array of groups; each gets a new id. "" on success, else what went wrong.</summary>
        public static string Import(string json, bool merge)
        {
            if (string.IsNullOrWhiteSpace(json)) return "Nothing to import - paste JSON first.";
            try
            {
                string t = json.TrimStart();
                List<POIGroup>? groups = t.StartsWith('{') ? JsonSerializer.Deserialize<POIGroup>(json, Json) is { } one ? [one] : null
                                       : t.StartsWith('[') ? JsonSerializer.Deserialize<List<POIGroup>>(json, Json)
                                       : null;
                if (groups == null) return "Unrecognised format - expected a JSON object ({...}) or array ([...]).";
                if (groups.Count == 0 || groups.All(g => g.TotalCount == 0)) return "The JSON contained no POIs.";
                if (!merge) Groups.Clear();
                foreach (var g in groups) { g.Id = Guid.NewGuid().ToString("N"); Groups.Add(g); }
                SaveDebounced();
                int total = groups.Sum(g => g.TotalCount);
                ToastManager.Show("Imported", $"{groups.Count} group{(groups.Count == 1 ? "" : "s")} with {total} POI{(total == 1 ? "" : "s")}.", ToastStyle.Success);
                return "";
            }
            catch (JsonException ex) { return $"JSON parse error: {ex.Message}"; }
            catch (Exception ex)     { return $"Import failed: {ex.Message}"; }
        }
    }
}
