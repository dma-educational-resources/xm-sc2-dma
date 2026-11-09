using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SC2Radar.UI.ImGui
{
    /// <summary>
    /// Central registry for the Search Features panel.
    /// Panels register open/navigate callbacks once at startup; the search panel
    /// queries LocManager for key locations and calls the right callbacks on click.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public static class SearchRegistry
    {
        public record Result(
            string Key,
            string Panel,
            IReadOnlyList<string> TabPath);

        // Panel display-name → (open the panel, navigate to tab path inside it)
        private static readonly Dictionary<string, (Action Open, Action<IReadOnlyList<string>> Navigate)>
            _panels = new(StringComparer.Ordinal);

        // ── Highlight state ───────────────────────────────────────────────────
        private static string _highlightKey;
        private static float  _highlightTime;
        // Guard: prevent Tick() from cancelling a highlight set on the same frame via NavigateTo.
        private static bool   _navigatedThisFrame;

        // ── One-shot popover open ─────────────────────────────────────────────
        // Set by NavigateTo when the target key lives inside a ToggleWithPopover.
        // Consumed (cleared) on the first frame that the matching popover checks it,
        // so clicking outside to close the popup is not fought by a re-open every frame.
        private static string _pendingOpenPopover;

        /// <summary>Key currently being highlighted, or null if expired.</summary>
        public static string HighlightKey  => _highlightTime > 0f ? _highlightKey : null;

        /// <summary>Seconds remaining on the active highlight (0 = no highlight).</summary>
        public static float  HighlightTime => _highlightTime;

        /// <summary>
        /// Called each frame by ToggleWithPopover controls.  If <paramref name="name"/>
        /// matches the pending one-shot open request, clears it and returns true
        /// (so the caller calls OpenPopup exactly once).  Returns false otherwise.
        /// </summary>
        public static bool TakeOpenPopover(string name)
        {
            if (_pendingOpenPopover == null || _pendingOpenPopover != name) return false;
            _pendingOpenPopover = null;
            return true;
        }

        // ── Registration ──────────────────────────────────────────────────────

        /// <summary>
        /// Called once per searchable panel at app startup (in MenuManager).
        /// <paramref name="name"/> must match the panel's PanelTitle / LocManager key.
        /// </summary>
        public static void RegisterPanel(
            string name,
            Action open,
            Action<IReadOnlyList<string>> navigate)
            => _panels[name] = (open, navigate);

        // ── Per-frame tick ────────────────────────────────────────────────────

        /// <summary>Call once per frame from the main render loop to tick the highlight timer.</summary>
        public static void Tick()
        {
            _navigatedThisFrame = false;
            if (_highlightTime > 0f)
            {
                _highlightTime -= ImGuiNET.ImGui.GetIO().DeltaTime;
                // Any mouse click cancels the highlight - unless navigation just fired this frame.
                if (!_navigatedThisFrame &&
                    (ImGuiNET.ImGui.IsMouseClicked(ImGuiNET.ImGuiMouseButton.Left) ||
                     ImGuiNET.ImGui.IsMouseClicked(ImGuiNET.ImGuiMouseButton.Right)))
                    _highlightTime = 0f;
            }
        }

        // ── Search ────────────────────────────────────────────────────────────

        public static IReadOnlyList<Result> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return Array.Empty<Result>();
            // Match against the translated value first (so a Chinese user can search in
            // Chinese), falling back to the raw English key (so "chams" still works even
            // on a non-English locale, since the key itself is always English). LocManager.All
            // already holds the translated value per key (identity fallback when untranslated),
            // so this is a pure lookup - no extra Get() calls needed. Value matches are ranked
            // above key-only matches so an intended-language hit surfaces first.
            return LocManager.All
                .Select(kv => (
                    kv.Key,
                    ValueMatch: kv.Value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0,
                    KeyMatch:   kv.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                .Where(t => t.ValueMatch || t.KeyMatch)
                .Select(t => (t.ValueMatch, Result: new Result(t.Key, LocManager.GetPanelForKey(t.Key), LocManager.GetTabPathForKey(t.Key))))
                .Where(t => t.Result.Panel != null
                         && !string.Equals(t.Result.Panel, "Search Features",   StringComparison.Ordinal)
                         && !string.Equals(t.Result.Panel, "Localisation Editor", StringComparison.Ordinal))
                .OrderByDescending(t => t.ValueMatch)
                .ThenBy(t => t.Result.Panel,   StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Result.TabPath.Count > 0 ? t.Result.TabPath[0] : "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Result.Key,      StringComparer.OrdinalIgnoreCase)
                .Select(t => t.Result)
                .ToList();
        }

        /// <summary>
        /// Redirect the active highlight to a different key (e.g. a popover parent
        /// when the child control can't be shown because the toggle is off).
        /// </summary>
        public static void SetHighlight(string key, float duration = 3f)
        {
            _highlightKey  = key;
            _highlightTime = duration;
        }

        // ── Navigation ────────────────────────────────────────────────────────

        /// <summary>
        /// Opens the panel the key lives in, navigates to the correct tab(s),
        /// and starts the highlight animation on the key's control row.
        /// </summary>
        public static void NavigateTo(string key)
        {
            string panel = LocManager.GetPanelForKey(key);
            if (panel == null) return;

            var tabs = LocManager.GetTabPathForKey(key);
            if (_panels.TryGetValue(panel, out var actions))
            {
                actions.Open();
                if (tabs.Count > 0)
                    actions.Navigate(tabs);
                else
                    // Key may itself be a tab label - try using the key as the tab name.
                    // NavigateTo implementations ignore names that don't match, so this is safe.
                    actions.Navigate(new[] { key });
            }

            _highlightKey       = key;
            _highlightTime      = 3f;
            _navigatedThisFrame = true;

            // If this key lives inside a ToggleWithPopover, request a one-shot open.
            // ToggleWithPopover will consume (clear) this on the first frame it sees it,
            // so the popup opens exactly once and can be dismissed normally afterwards.
            string popoverParent = LocManager.GetPopoverParentForKey(key);
            if (popoverParent != null)
                _pendingOpenPopover = popoverParent;
        }
    }
}
