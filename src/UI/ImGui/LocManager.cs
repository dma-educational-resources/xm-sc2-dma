using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace SC2Radar.UI.ImGui
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public static class LocManager
    {
        // ── Main string table ─────────────────────────────────────────────────
        private static Dictionary<string, string> _strings = new(StringComparer.Ordinal);

        // ── Session tracking ──────────────────────────────────────────────────
        private static readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        // ── Panel-scoped key tracking (ordered) ───────────────────────────────
        // _panelOrder stores keys in the exact order they were first rendered,
        // which mirrors the visual top-to-bottom flow of each panel's UI.
        private static readonly Dictionary<string, List<string>>    _panelOrder = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, HashSet<string>> _panelHash  = new(StringComparer.Ordinal);
        private static string _currentPanel = null;

        // ── Tab-scope tracking ────────────────────────────────────────────────
        // Records the full tab path (e.g. ["Chams", "PMC"]) for each key the
        // first time it is registered.  Used by SearchRegistry and LocEditor.
        private static readonly Stack<string>                        _tabStack     = new();
        private static readonly Dictionary<string, string>           _keyPanel     = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string[]>         _keyTabs      = new(StringComparer.Ordinal);
        // Per-panel list of distinct tab paths, in first-seen order.
        private static readonly Dictionary<string, List<string[]>>   _panelTabPaths = new(StringComparer.Ordinal);

        // ── Popover-scope tracking ────────────────────────────────────────────
        // Records which ToggleWithPopover's content each key was first seen inside.
        // Enables SearchRegistry to auto-open the popover when navigating to a child.
        private static readonly Stack<string>                        _popoverStack     = new();
        private static readonly Dictionary<string, string>           _keyPopoverParent = new(StringComparer.Ordinal);

        // ── Paths ─────────────────────────────────────────────────────────────
        private static string _currentLocale = "en";
        private static readonly string _localesDir;
        private static string ManifestPath => Path.Combine(_localesDir, "_manifest.json");

        // ── Public API ────────────────────────────────────────────────────────
        public static IReadOnlyDictionary<string, string> All          => _strings;
        public static string                              CurrentLocale => _currentLocale;
        public static string                              LocalesDir    => _localesDir;

        // True once any translation value has been edited since the last Save/Load/
        // SwitchLocale. Cleared by all three, since each of those establishes a new
        // on-disk-equivalent baseline.
        public static bool IsDirty { get; private set; }

        static LocManager()
        {
            // Locale files used to live next to the EXE, which gets wiped on every app
            // update/reinstall - silently resetting everyone's translations any time a new
            // build shipped. Program.DataDir (%AppData%\...) survives updates, same as
            // Config/POIs/custom overrides/etc.
            var exeDir       = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            var legacyLocalesDir = Path.Combine(exeDir, "locales");
            _localesDir = Path.Combine(Program.DataDir, "locales");
            Directory.CreateDirectory(_localesDir);
            MigrateLegacyLocales(legacyLocalesDir);
            Load("en");
            LoadManifest();
        }

        /// <summary>
        /// One-time migration from the old exe-adjacent locales folder (see static ctor
        /// comment) into the stable AppData location, so whoever is already mid-translation
        /// when this fix ships doesn't lose it on the very update that fixes the bug.
        /// </summary>
        private static void MigrateLegacyLocales(string legacyDir)
        {
            try
            {
                if (!Directory.Exists(legacyDir)) return;
                if (Directory.GetFileSystemEntries(_localesDir).Length > 0) return; // already migrated / already populated

                foreach (var file in Directory.GetFiles(legacyDir, "*.json"))
                    File.Copy(file, Path.Combine(_localesDir, Path.GetFileName(file)), overwrite: false);
            }
            catch { }
        }

        /// <summary>
        /// Re-applies the locale the user was last actively editing/using, if one was
        /// saved and its file still exists. Call once at startup after Program.Config
        /// is available - the static constructor above always establishes "en" first
        /// as the key-fallback baseline, independent of config load order.
        /// </summary>
        public static void RestoreActiveLocale()
        {
            var saved = Program.Config?.UILocale;
            if (string.IsNullOrEmpty(saved) || saved == "en") return;
            if (File.Exists(LocalePath(saved)))
                SwitchLocale(saved);
        }

        // Returns the translation for key, or the key itself as fallback.
        // Auto-registers unseen keys into _strings, current panel scope, and tab scope.
        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            _seen.Add(key);

            if (_currentPanel != null)
            {
                // Panel-order tracking (render order within panel)
                if (!_panelHash.TryGetValue(_currentPanel, out var hash))
                {
                    _panelHash[_currentPanel]  = hash = new HashSet<string>(StringComparer.Ordinal);
                    _panelOrder[_currentPanel] = new List<string>();
                }
                if (hash.Add(key))
                    _panelOrder[_currentPanel].Add(key);

                // Popover-scope tracking: record on first registration only
                if (_popoverStack.Count > 0 && !_keyPopoverParent.ContainsKey(key))
                    _keyPopoverParent[key] = _popoverStack.Peek();

                // Tab-scope tracking: record on first registration only
                if (!_keyPanel.ContainsKey(key))
                {
                    _keyPanel[key] = _currentPanel;
                    var path = _tabStack.Count > 0
                        ? _tabStack.Reverse().ToArray()
                        : Array.Empty<string>();
                    _keyTabs[key] = path;

                    if (path.Length > 0)
                    {
                        if (!_panelTabPaths.TryGetValue(_currentPanel, out var tabPaths))
                            _panelTabPaths[_currentPanel] = tabPaths = new List<string[]>();
                        if (!tabPaths.Any(p => p.SequenceEqual(path, StringComparer.Ordinal)))
                            tabPaths.Add(path);
                    }
                }
            }

            if (_strings.TryGetValue(key, out var val)) return val;
            _strings[key] = key;
            return key;
        }

        public static void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (_strings.TryGetValue(key, out var existing) && existing == value) return;
            _strings[key] = value;
            IsDirty = true;
        }

        // ── Panel scope ───────────────────────────────────────────────────────

        public static void PushPanel(string panelName)
        {
            _currentPanel = panelName;
            _tabStack.Clear();
            if (!string.IsNullOrEmpty(panelName) && !_panelHash.ContainsKey(panelName))
            {
                _panelHash[panelName]  = new HashSet<string>(StringComparer.Ordinal);
                _panelOrder[panelName] = new List<string>();
            }
        }

        public static void PopPanel()
        {
            _currentPanel = null;
            _tabStack.Clear();
        }

        // ── Tab scope ─────────────────────────────────────────────────────────

        /// <summary>
        /// Call immediately before rendering a tab's content so all controls
        /// inside are tagged with the correct tab path.
        /// </summary>
        public static void PushTab(string tabName)
        {
            if (!string.IsNullOrEmpty(tabName)) _tabStack.Push(tabName);
        }

        public static void PopTab()
        {
            if (_tabStack.Count > 0) _tabStack.Pop();
        }

        // ── Popover scope ─────────────────────────────────────────────────────

        /// <summary>
        /// Call before invoking a ToggleWithPopover's content action so that
        /// keys registered inside are tagged with their popover parent label.
        /// </summary>
        public static void PushPopover(string parentKey)
        {
            if (!string.IsNullOrEmpty(parentKey)) _popoverStack.Push(parentKey);
        }

        public static void PopPopover()
        {
            if (_popoverStack.Count > 0) _popoverStack.Pop();
        }

        /// <summary>Returns the label of the ToggleWithPopover that contains this key, or null.</summary>
        public static string GetPopoverParentForKey(string key)
            => _keyPopoverParent.TryGetValue(key, out var p) ? p : null;

        // ── Location queries ──────────────────────────────────────────────────

        /// <summary>Panel the key was first rendered inside, or null if unknown.</summary>
        public static string GetPanelForKey(string key)
            => _keyPanel.TryGetValue(key, out var p) ? p : null;

        /// <summary>Tab path (e.g. ["Chams", "PMC"]) for the key, empty if at panel root.</summary>
        public static IReadOnlyList<string> GetTabPathForKey(string key)
            => _keyTabs.TryGetValue(key, out var t) ? t : Array.Empty<string>();

        /// <summary>Distinct top-level tab names registered for a panel, in first-seen order.</summary>
        public static string[] GetTopLevelTabsForPanel(string panel)
        {
            if (!_panelTabPaths.TryGetValue(panel, out var paths)) return Array.Empty<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var p in paths)
                if (p.Length > 0 && seen.Add(p[0]))
                    result.Add(p[0]);
            return result.ToArray();
        }

        /// <summary>Keys for a panel that are tagged to a specific top-level tab.</summary>
        public static IEnumerable<string> GetKeysForPanelTab(string panel, string tabName)
            => GetKeysForPanel(panel)
                .Where(k => _keyTabs.TryGetValue(k, out var tabs)
                         && tabs.Length > 0
                         && string.Equals(tabs[0], tabName, StringComparison.Ordinal));

        // Panel names that have at least one registered key, alphabetical.
        public static string[] RegisteredPanels()
            => _panelOrder.Keys
                .Where(k => _panelOrder[k].Count > 0)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        // Keys for a panel in the order they were first rendered (= UI top-to-bottom flow).
        public static IReadOnlyList<string> GetKeysForPanel(string panel)
            => _panelOrder.TryGetValue(panel, out var list)
               ? (IReadOnlyList<string>)list
               : Array.Empty<string>();

        // ── Locale loading / saving ───────────────────────────────────────────

        public static void Load(string locale)
        {
            _currentLocale = locale;
            IsDirty = false;
            string path = LocalePath(locale);
            if (!File.Exists(path)) { _strings.Clear(); return; }
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var next = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name.StartsWith('_')) continue;
                    next[prop.Name] = prop.Value.GetString() ?? prop.Name;
                }
                _strings = next;
            }
            catch { _strings.Clear(); }
        }

        public static void SwitchLocale(string locale)
        {
            var snapshot = new HashSet<string>(_seen, StringComparer.Ordinal);
            Load(locale);
            foreach (var key in snapshot)
                if (!_strings.ContainsKey(key))
                    _strings[key] = key;

            // Remember the locale the user actively selected/created so it's restored
            // automatically next launch instead of always defaulting back to English.
            if (Program.Config != null)
            {
                Program.Config.UILocale = locale;
                Program.Config.SaveDebounced();
            }
        }

        public static void Save() => Save(_currentLocale);

        public static void Save(string locale)
        {
            IsDirty = false;
            Directory.CreateDirectory(_localesDir);
            var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["_locale"] = locale };
            foreach (var kvp in _strings) sorted[kvp.Key] = kvp.Value;
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(LocalePath(locale), JsonSerializer.Serialize(sorted, opts));
            SaveManifest();
        }

        public static string[] AvailableLocales()
        {
            if (!Directory.Exists(_localesDir)) return new[] { "en" };
            return Directory.GetFiles(_localesDir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f)!)
                .Where(n => !n.StartsWith('_'))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }

        public static IEnumerable<string> OrphanKeys()
            => _strings.Keys.Where(k => !_seen.Contains(k));

        // ── Manifest ──────────────────────────────────────────────────────────
        // Extended format stores key → {panel, tabs} so SearchRegistry has
        // navigation data without requiring every tab to be opened first.

        private static void LoadManifest()
        {
            if (!File.Exists(ManifestPath)) return;
            try
            {
                var json = File.ReadAllText(ManifestPath);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // Current format: { "keys": [...], "locations": { key: { panel, tabs } } }
                    if (doc.RootElement.TryGetProperty("keys", out var keysEl))
                    {
                        foreach (var k in keysEl.EnumerateArray())
                        {
                            var key = k.GetString();
                            if (key == null) continue;
                            _seen.Add(key);
                            if (!_strings.ContainsKey(key)) _strings[key] = key;
                        }
                    }
                    if (doc.RootElement.TryGetProperty("locations", out var locsEl))
                    {
                        foreach (var prop in locsEl.EnumerateObject())
                        {
                            var key = prop.Name;
                            if (!prop.Value.TryGetProperty("panel", out var panelEl)) continue;
                            var panel = panelEl.GetString();
                            if (panel == null || _keyPanel.ContainsKey(key)) continue;

                            _keyPanel[key] = panel;

                            string[] tabs = Array.Empty<string>();
                            if (prop.Value.TryGetProperty("tabs", out var tabsEl))
                            {
                                tabs = tabsEl.EnumerateArray()
                                    .Select(t => t.GetString())
                                    .Where(t => t != null)
                                    .ToArray();
                            }
                            _keyTabs[key] = tabs;

                            if (tabs.Length > 0)
                            {
                                if (!_panelTabPaths.TryGetValue(panel, out var pathList))
                                    _panelTabPaths[panel] = pathList = new List<string[]>();
                                if (!pathList.Any(p => p.SequenceEqual(tabs, StringComparer.Ordinal)))
                                    pathList.Add(tabs);
                            }
                        }
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    // Legacy format: plain array of keys
                    foreach (var k in doc.RootElement.EnumerateArray())
                    {
                        var key = k.GetString();
                        if (key == null) continue;
                        _seen.Add(key);
                        if (!_strings.ContainsKey(key)) _strings[key] = key;
                    }
                }
            }
            catch { }
        }

        public static void SaveManifest()
        {
            // Build location entries sorted by key
            var locations = _keyPanel.Keys
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    k => k,
                    k => new
                    {
                        panel = _keyPanel[k],
                        tabs  = _keyTabs.TryGetValue(k, out var t) ? t : Array.Empty<string>()
                    },
                    StringComparer.Ordinal);

            var manifest = new
            {
                keys      = _seen.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(),
                locations = locations
            };
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, opts));
        }

        private static string LocalePath(string locale)
            => Path.Combine(_localesDir, $"{locale}.json");
    }
}
