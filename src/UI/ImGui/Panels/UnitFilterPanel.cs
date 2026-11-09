using System.Numerics;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Panels;

namespace SC2Radar.UI.ImGui
{
    /// <summary>
    /// Unit Filter: search unit names (comma-separated terms, e.g. "tychus, artifact"), toggle teams,
    /// and hide / focus / centre individual unit types. State lives in <see cref="UnitFilter"/>.
    /// </summary>
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public sealed class UnitFilterPanel : ChromePanel
    {
        protected override Vector2 InitSize   => new(460f, 560f);
        protected override string  PanelTitle => "Unit Filter";

        sealed record Row(string Name, uint Color, UnitCategory Category, string Path, int Count, Vector2 First, int Burrowed = 0, int Cloaked = 0);

        int  _selRow = -1;
        int  _sortCol = 4;
        bool _sortDesc = true;
        readonly Dictionary<(string, uint), int> _cycle = [];   // centre-on cycles through instances

        int _tab;
        static readonly string[] Tabs = ["Filter", "Rules"];

        protected override void DrawPanelContent()
        {
            var units = Memory.Instance?.Snapshot.Units ?? [];
            DrawTabHeader(Tabs, ref _tab);
            if (_tab == 1) { DrawRulesTab(units); return; }
            DrawFilterTab(units);
        }

        void DrawFilterTab(UnitEntry[] units)
        {
            var S = UnitFilter.S;

            // ── Search ───────────────────────────────────────────────────────
            UI.BeginGroup("Search");
            string search = S.Search;
            if (UI.InputText("Name##uf_search", ref search, 128, placeholder: "e.g. tychus, artifact, buff:Red",
                    hint: "Comma-separated parts of names. 'buff:Red' finds units with that buff / debuff (see Buffs below)."))
            {
                S.Search = search;
                UnitFilter.SaveDebounced();
            }
            UI.DrawGrid(UI.GroupInnerWidth,
            [
                FilterToggle("Dim non-matches instead of hiding", () => S.DimNonMatches, v => S.DimNonMatches = v,
                    "By default a search hides everything that doesn't match. Turn this on to keep the rest on the radar, faded."),
                FilterToggle("Search model paths too", () => S.MatchPath, v => S.MatchPath = v,
                    @"Also match the full model path, e.g. assets\doodads\...\name.m3 - handy for oddly named props."),
            ]);
            if (UnitFilter.HasSearch)
            {
                int m = 0;
                foreach (var u in units) if (UnitFilter.IsMatch(in u)) m++;
                UI.Caption($"{m} matching object{(m == 1 ? "" : "s")} on the map" +
                           (!S.ShowScenery || !S.ShowOther ? " (turn on Scenery / Other below to search those too)" : ""));
            }
            UI.EndGroup();

            // ── Categories ───────────────────────────────────────────────────
            UI.BeginGroup("Show");
            var cfg = Program.Config;
            bool[] cats = [cfg.ShowUnits, cfg.ShowWorkers, cfg.ShowBuildings, S.ShowResources, S.ShowScenery, S.ShowEffects, S.ShowOther, S.ShowRemains];
            if (UI.SegmentedBar("##uf_cats", ["Units", "Workers", "Buildings", "Resources", "Scenery", "Effects", "Other", "Remains"], cats, stretch: true,
                    hint: "Resources = mineral fields and vespene geysers. Scenery = rocks, trees, props. Effects = impacts, missiles, blood, " +
                          "weapon flashes. Other = decals, skybox and the rest. Remains = dead units' death and ragdoll models (part of Other: needs Other on). " +
                          "Scenery, Effects, Other and Remains are off by default; turn them on to hunt for hidden or oddly named objects. " +
                          "What they leave out, and decoration you hide or blacklist, isn't read from the game at all."))
            {
                cfg.ShowUnits = cats[0]; cfg.ShowWorkers = cats[1]; cfg.ShowBuildings = cats[2];
                S.ShowResources = cats[3]; S.ShowScenery = cats[4]; S.ShowEffects = cats[5]; S.ShowOther = cats[6]; S.ShowRemains = cats[7];
                cfg.SaveDebounced(); UnitFilter.SaveDebounced();
            }
            int nb = 0, nc = 0, nd = 0;
            foreach (var u in units) { if (u.Burrowed) nb++; if (u.Cloaked) nc++; if (u.IsDamaged) nd++; }
            UI.DrawGrid(UI.GroupInnerWidth,
            [
                FilterToggle($"Burrowed units only ({nb} on the map)##uf_burrow", () => S.OnlyBurrowed, v => S.OnlyBurrowed = v,
                    "Show only burrowed units. Burrowed units are always drawn as a hollow ring on the radar."),
                FilterToggle($"Cloaked units only ({nc} on the map)##uf_cloak", () => S.OnlyCloaked, v => S.OnlyCloaked = v,
                    "Show only cloaked units (banshee, ghost, dark templar, observer, ...). With Burrowed also on, shows both."),
            ]);
            UI.EndGroup();

            // ── Vitals ───────────────────────────────────────────────────────
            UI.BeginGroup("Vitals");
            UI.DrawGrid(UI.GroupInnerWidth,
            [
                FilterToggle($"Health bars ({nd} damaged)##uf_dmg", () => S.ShowDamage, v => S.ShowDamage = v,
                    "Green-to-red HP bar under damaged units (a red dot when max HP is unknown)."),
                FilterToggle("Shield bars##uf_shields", () => S.ShowShields, v => S.ShowShields = v,
                    "Blue shield bar above the HP bar on units whose shields are down."),
                FilterToggle("Energy##uf_energy", () => S.ShowEnergy, v => S.ShowEnergy = v,
                    "Energy / max next to casters when zoomed in."),
                FilterToggle("Numbers when zoomed in##uf_vitaltext", () => S.ShowVitalText, v => S.ShowVitalText = v,
                    "HP, shield and energy figures next to units once zoomed in far enough for labels. Off: bars only."),
            ]);
            UI.EndGroup();

            int rulesActive = S.Blacklist.Count(p => !S.BlacklistOff.Contains(p)) + S.Highlights.Count(h => h.Enabled) + S.HiddenNames.Count + S.HiddenTeams.Count;
            if (rulesActive > 0)
                UI.Caption($"{rulesActive} active rule{(rulesActive == 1 ? "" : "s")} (blacklist, highlights, hidden types/teams) - see the Rules tab.");

            // ── Teams ────────────────────────────────────────────────────────
            UI.BeginGroup("Teams");
            var teams = units.GroupBy(u => u.Color).Select(g => (Color: g.Key, Count: g.Count()))
                             .OrderBy(t => t.Color == UnitFilter.NeutralColor).ThenByDescending(t => t.Count).ToList();
            if (teams.Count == 0) UI.Caption("No units yet. Load into a mission.");
            foreach (var (color, count) in teams)
            {
                // colour swatch, then the toggle; the cog renames the team
                var dl = ImGuiNET.ImGui.GetWindowDrawList();
                var p  = ImGuiNET.ImGui.GetCursorScreenPos();
                dl.AddRectFilled(p + new Vector2(0, 5), p + new Vector2(12, 17), color, 3f);
                ImGuiNET.ImGui.SetCursorScreenPos(p + new Vector2(18, 0));

                bool visible = !S.HiddenTeams.Contains(color);
                uint c = color;
                if (UI.ToggleWithPopover($"{UnitFilter.TeamLabel(color)}  ({count})##uf_team_{color:X8}", ref visible,
                        () => DrawTeamPopover(c), width: UI.GroupInnerWidth - 18f))
                {
                    if (visible) S.HiddenTeams.Remove(color); else S.HiddenTeams.Add(color);
                    UnitFilter.SaveDebounced();
                }
            }
            UI.EndGroup();

            // ── Unit types ───────────────────────────────────────────────────
            UI.BeginGroup("Unit types");
            if (UI.IsGroupCollapsed)   // the table is its own child window and would ignore the collapse
            {
                UI.EndGroup();
                return;
            }
            var rows = units.Where(u => UnitFilter.CategoryShown(in u) && !UnitFilter.IsBlacklisted(in u) && UnitFilter.PassesHiddenFilter(in u)).GroupBy(u => (u.Name, u.Color))
                .Where(g => !UnitFilter.HasSearch || g.Any(u => UnitFilter.IsMatch(in u)))
                .Select(g => new Row(g.Key.Name, g.Key.Color, g.First().Category, g.First().Path, g.Count(), new Vector2(g.First().X, g.First().Y), g.Count(u => u.Burrowed), g.Count(u => u.Cloaked)))
                .ToList();
            // hidden names stay listed (dimmed) even when no longer on the radar, so they can be un-hidden
            foreach (var hidden in S.HiddenNames)
                if (!rows.Any(r => string.Equals(r.Name, hidden, StringComparison.OrdinalIgnoreCase)))
                    rows.Add(new Row(hidden, UnitFilter.NeutralColor, UnitCategory.Other, "", 0, default));
            rows = (_sortCol switch
            {
                1 => _sortDesc ? rows.OrderByDescending(r => r.Name) : rows.OrderBy(r => r.Name),
                2 => _sortDesc ? rows.OrderByDescending(r => UnitFilter.TeamLabel(r.Color)) : rows.OrderBy(r => UnitFilter.TeamLabel(r.Color)),
                3 => _sortDesc ? rows.OrderByDescending(r => r.Category) : rows.OrderBy(r => r.Category),
                _ => _sortDesc ? rows.OrderByDescending(r => r.Count) : rows.OrderBy(r => r.Count),
            }).ThenBy(r => r.Name).ToList();

            if (S.HiddenNames.Count > 0 && UI.Button($"Show all hidden ({S.HiddenNames.Count})##uf_unhide", style: ButtonStyle.Ghost))
            {
                S.HiddenNames.Clear();
                UnitFilter.SaveDebounced();
            }
            UI.Caption("Click a row to focus it on the radar. Right-click for hide / centre.");

            var font  = ImGuiStyle.FontNormal;
            float fs  = font.FontSize > 0f ? font.FontSize : ImGuiNET.ImGui.GetTextLineHeight();
            const float RowH = 24f;
            int before = _selRow;
            UI.Table("##uf_types",
                [new UI.TableColumn("", 18f), new UI.TableColumn("Name"), new UI.TableColumn("Team", 110f),
                 new UI.TableColumn("Type", 130f), new UI.TableColumn("Count", 50f)],
                rows.Count,
                (i, x, w) =>
                {
                    var r   = rows[i];
                    var dl  = ImGuiNET.ImGui.GetWindowDrawList();
                    float y = ImGuiNET.ImGui.GetCursorScreenPos().Y;
                    float ty = y + (RowH - fs) * 0.5f;
                    bool hidden = S.HiddenNames.Contains(r.Name);
                    uint txt = hidden ? 0x80FFFFFF : 0xFFFFFFFF;
                    dl.AddCircleFilled(new Vector2(x[0] + 7f, y + RowH * 0.5f), 5f, hidden ? (r.Color & 0x60FFFFFF) : r.Color);
                    dl.AddText(new Vector2(x[1], ty), txt, hidden ? $"{r.Name}  (hidden)" : r.Name);
                    dl.AddText(new Vector2(x[2], ty), txt, UnitFilter.TeamLabel(r.Color));
                    dl.AddText(new Vector2(x[3], ty), txt, r.Category.ToString().ToLowerInvariant() + (r.Burrowed > 0 ? $" ({r.Burrowed} burrowed)" : "") + (r.Cloaked > 0 ? $" ({r.Cloaked} cloaked)" : ""));
                    dl.AddText(new Vector2(x[4], ty), txt, r.Count.ToString());
                },
                ref _selRow,
                itemHeight: RowH,
                rowContextMenu: i =>
                {
                    var r = rows[i];
                    bool hidden = S.HiddenNames.Contains(r.Name);
                    if (UI.MenuItem(hidden ? $"Show \"{r.Name}\"" : $"Hide \"{r.Name}\""))
                    {
                        if (hidden) S.HiddenNames.Remove(r.Name); else S.HiddenNames.Add(r.Name);
                        UnitFilter.SaveDebounced();
                    }
                    bool hl = UnitFilter.FindHighlight("=" + r.Name) != null;
                    if (UI.MenuItem(hl ? $"Remove highlight" : $"Highlight \"{r.Name}\""))
                    { if (hl) UnitFilter.RemoveHighlight("=" + r.Name); else UnitFilter.AddHighlight("=" + r.Name); }
                    if (UI.MenuItem($"Blacklist \"{r.Name}\""))
                        UnitFilter.AddToBlacklist("=" + r.Name);
                    if (r.Count > 0 && UI.MenuItem("Centre radar on it (repeat to cycle)"))
                        CentreOn(r, units);
                    if (UI.MenuItem($"Search for \"{r.Name}\""))
                    {
                        S.Search = r.Name;
                        UnitFilter.SaveDebounced();
                    }
                },
                sortColumn: _sortCol, sortDescending: _sortDesc,
                onSortColumn: c =>
                {
                    if (c == 0) return;
                    if (c == _sortCol) _sortDesc = !_sortDesc; else { _sortCol = c; _sortDesc = c == 4; }
                });

            if (_selRow != before)
                UnitFilter.FocusName = _selRow >= 0 && _selRow < rows.Count ? rows[_selRow].Name : null;
            UI.EndGroup();
        }

        // ── Rules tab ─────────────────────────────────────────────────────────
        // Everything that changes what the radar shows, in one place: add, switch off, or remove.

        string _blInput = "", _hlInput = "";

        void DrawRulesTab(UnitEntry[] units)
        {
            DrawHighlights(units);
            DrawBlacklist(units);
            DrawHiddenTypes(units);
            DrawHiddenTeams(units);
            DrawOtherFilters();
        }

        /// <summary>Text box + Add button; Enter also adds. Returns the comma-separated entries to add, or null.</summary>
        /// <summary>A unit filter toggle for a two-column grid: saves the filter when flipped.</summary>
        static UI.GridItem FilterToggle(string label, Func<bool> get, Action<bool> set, string hint) =>
            new(w =>
            {
                bool v = get();
                if (UI.Toggle(label, ref v, w, hint: hint)) { set(v); UnitFilter.SaveDebounced(); }
            });

        static string[]? AddRow(string id, ref string input, string placeholder)
        {
            float bw = 70f;
            UI.InputText($"Pattern##{id}_in", ref input, 64, placeholder: placeholder, width: UI.GroupInnerWidth - bw - 4f);
            bool enter = ImGuiNET.ImGui.IsItemFocused() && ImGuiNET.ImGui.IsKeyPressed(ImGuiNET.ImGuiKey.Enter);
            ImGuiNET.ImGui.SameLine(0f, 4f);
            bool add = UI.Button($"Add##{id}_add", new Vector2(bw, 0f)) || enter;
            if (!add || input.Trim().Length == 0) return null;
            var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            input = "";
            return parts;
        }

        static bool RemoveButton(string id) =>
            UI.Button($"x##{id}", new Vector2(24f, 0f), ButtonStyle.Ghost, hint: "Remove");

        static int CountMatching(UnitEntry[] units, string pattern)
        {
            int n = 0;
            foreach (var u in units) if (UnitFilter.PatternMatches(pattern, in u)) n++;
            return n;
        }

        void DrawHighlights(UnitEntry[] units)
        {
            var S = UnitFilter.S;
            UI.BeginGroup("Highlights (ringed on the radar)");
            if (AddRow("uf_hl", ref _hlInput, "e.g. =ghost, tychus, *templar") is { } add)
                foreach (var p in add) UnitFilter.AddHighlight(p);
            UI.Caption("Same patterns as the blacklist; =name matches one exact type. Right-click a dot on the radar > Highlight also adds here.");

            if (S.Highlights.Count == 0) UI.Caption("No highlights yet.");
            for (int i = 0; i < S.Highlights.Count; i++)
            {
                var h = S.Highlights[i];
                if (RemoveButton($"uf_hl_rm{i}")) { S.Highlights.RemoveAt(i); UnitFilter.SaveDebounced(); break; }
                ImGuiNET.ImGui.SameLine(0f, 6f);
                var p0 = ImGuiNET.ImGui.GetCursorScreenPos();
                ImGuiNET.ImGui.GetWindowDrawList().AddCircle(p0 + new Vector2(7, 11), 6f, h.Color, 16, 2.5f);
                ImGuiNET.ImGui.SetCursorScreenPos(p0 + new Vector2(20, 0));
                bool on = h.Enabled;
                var rule = h;
                if (UI.ToggleWithPopover($"{h.Pattern}   ({CountMatching(units, h.Pattern)} on the map)##uf_hl_t{i}", ref on,
                        () => DrawHighlightPopover(rule, i), width: UI.GroupInnerWidth - 50f))
                { h.Enabled = on; UnitFilter.SaveDebounced(); }
            }
            if (S.Highlights.Count > 1 && UI.Button("Clear all highlights##uf_hl_clear", style: ButtonStyle.Ghost))
            { S.Highlights.Clear(); UnitFilter.SaveDebounced(); }
            UI.EndGroup();
        }

        void DrawHighlightPopover(HighlightRule h, int i)
        {
            uint c = h.Color;
            var rgba = new Vector4((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, 1f);
            if (UI.ColorEdit($"Ring colour##uf_hl_c{i}", ref rgba))
            {
                static uint B(float v) => (uint)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
                h.Color = 0xFF000000u | (B(rgba.Z) << 16) | (B(rgba.Y) << 8) | B(rgba.X);
                UnitFilter.SaveDebounced();
            }
            string pat = h.Pattern;
            if (UI.InputText($"Pattern##uf_hl_p{i}", ref pat, 64) && pat.Trim().Length > 0)
            { h.Pattern = pat.Trim(); UnitFilter.SaveDebounced(); }
        }

        void DrawBlacklist(UnitEntry[] units)
        {
            var S = UnitFilter.S;
            UI.BeginGroup("Blacklist (never drawn)");
            if (AddRow("uf_bl", ref _blInput, "e.g. physicsimpact*, *_impact, blood") is { } add)
                foreach (var p in add) UnitFilter.AddToBlacklist(p);
            UI.Caption("Matches the name or model path. * is a wildcard (physicsimpact*), =name is one exact type, plain text is 'contains'.");

            if (UI.Button("Add common effect noise##uf_bl_preset", style: ButtonStyle.Ghost,
                    hint: string.Join(", ", UnitFilter.EffectNoisePreset)))
                foreach (var p in UnitFilter.EffectNoisePreset) UnitFilter.AddToBlacklist(p);

            if (S.Blacklist.Count == 0) UI.Caption("Nothing blacklisted.");
            for (int i = 0; i < S.Blacklist.Count; i++)
            {
                string p = S.Blacklist[i];
                if (RemoveButton($"uf_bl_rm{i}")) { S.Blacklist.RemoveAt(i); S.BlacklistOff.Remove(p); UnitFilter.SaveDebounced(); break; }
                ImGuiNET.ImGui.SameLine(0f, 6f);
                bool on = !S.BlacklistOff.Contains(p);
                if (UI.Toggle($"{p}   ({CountMatching(units, p)} on the map)##uf_bl_t{i}", ref on, UI.GroupInnerWidth - 30f,
                        hint: "Switch the rule off without deleting it."))
                {
                    if (on) S.BlacklistOff.Remove(p); else S.BlacklistOff.Add(p);
                    UnitFilter.SaveDebounced();
                }
            }
            if (S.Blacklist.Count > 1 && UI.Button("Clear blacklist##uf_bl_clear", style: ButtonStyle.Ghost))
            { S.Blacklist.Clear(); S.BlacklistOff.Clear(); UnitFilter.SaveDebounced(); }
            UI.Caption("Counts only include categories that are switched on (Filter > Show).");
            UI.EndGroup();
        }

        void DrawHiddenTypes(UnitEntry[] units)
        {
            var S = UnitFilter.S;
            UI.BeginGroup("Hidden types");
            if (S.HiddenNames.Count == 0) UI.Caption("None. Right-click a dot or a Unit types row > Hide to add one.");
            foreach (var name in S.HiddenNames.OrderBy(n => n).ToList())
            {
                if (RemoveButton($"uf_hn_{name}")) { S.HiddenNames.Remove(name); UnitFilter.SaveDebounced(); break; }
                ImGuiNET.ImGui.SameLine(0f, 6f);
                int n = units.Count(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
                ImGuiNET.ImGui.TextUnformatted($"{name}   ({n} on the map)");
            }
            if (S.HiddenNames.Count > 1 && UI.Button("Show all hidden types##uf_hn_clear", style: ButtonStyle.Ghost))
            { S.HiddenNames.Clear(); UnitFilter.SaveDebounced(); }
            UI.EndGroup();
        }

        void DrawHiddenTeams(UnitEntry[] units)
        {
            var S = UnitFilter.S;
            UI.BeginGroup("Hidden teams");
            if (S.HiddenTeams.Count == 0) UI.Caption("None.");
            foreach (var c in S.HiddenTeams.ToList())
            {
                if (RemoveButton($"uf_ht_{c:X8}")) { S.HiddenTeams.Remove(c); UnitFilter.SaveDebounced(); break; }
                ImGuiNET.ImGui.SameLine(0f, 6f);
                var p0 = ImGuiNET.ImGui.GetCursorScreenPos();
                ImGuiNET.ImGui.GetWindowDrawList().AddRectFilled(p0 + new Vector2(0, 5), p0 + new Vector2(12, 17), c, 3f);
                ImGuiNET.ImGui.SetCursorScreenPos(p0 + new Vector2(18, 0));
                ImGuiNET.ImGui.TextUnformatted($"{UnitFilter.TeamLabel(c)}   ({units.Count(u => u.Color == c)} on the map)");
            }
            if (S.HiddenTeams.Count > 1 && UI.Button("Show all teams##uf_ht_clear", style: ButtonStyle.Ghost))
            { S.HiddenTeams.Clear(); UnitFilter.SaveDebounced(); }
            UI.EndGroup();
        }

        void DrawOtherFilters()
        {
            var S = UnitFilter.S;
            var cfg = Program.Config;
            UI.BeginGroup("Other active filters");
            int shown = 0;
            void Row(string text, string button, Action undo)
            {
                shown++;
                if (UI.Button($"{button}##uf_of_{shown}", style: ButtonStyle.Ghost)) { undo(); UnitFilter.SaveDebounced(); cfg.SaveDebounced(); }
                ImGuiNET.ImGui.SameLine(0f, 8f);
                ImGuiNET.ImGui.TextUnformatted(text);
            }
            if (UnitFilter.HasSearch) Row($"Search: \"{S.Search}\" ({(S.DimNonMatches ? "dimming" : "hiding")} the rest)", "Clear", () => S.Search = "");
            if (S.OnlyBurrowed) Row("Burrowed units only", "Turn off", () => S.OnlyBurrowed = false);
            if (S.OnlyCloaked)  Row("Cloaked units only", "Turn off", () => S.OnlyCloaked = false);
            if (!cfg.ShowUnits)     Row("Units category off", "Show", () => cfg.ShowUnits = true);
            if (!cfg.ShowWorkers)   Row("Workers category off", "Show", () => cfg.ShowWorkers = true);
            if (!cfg.ShowBuildings) Row("Buildings category off", "Show", () => cfg.ShowBuildings = true);
            if (shown == 0) UI.Caption("None. (Scenery / Effects / Other are off by default - Filter > Show.)");
            UI.EndGroup();
        }

        void DrawTeamPopover(uint color)
        {
            var S = UnitFilter.S;
            string label = S.TeamLabels.GetValueOrDefault(color, "");
            if (UI.InputText($"Label##uf_tl_{color:X8}", ref label, 32, placeholder: UnitFilter.TeamLabel(color)))
            {
                if (string.IsNullOrWhiteSpace(label)) S.TeamLabels.Remove(color); else S.TeamLabels[color] = label;
                UnitFilter.SaveDebounced();
            }
            UI.Caption("e.g. Me, Zerg AI, Ally, Neutral");
        }

        void CentreOn(Row r, UnitEntry[] units)
        {
            var matches = units.Where(u => u.Name == r.Name && u.Color == r.Color).ToList();
            if (matches.Count == 0) return;
            int k = _cycle.GetValueOrDefault((r.Name, r.Color));
            var u = matches[k % matches.Count];
            _cycle[(r.Name, r.Color)] = k + 1;
            UnitFilter.CenterRequest = new Vector2(u.X, u.Y);
            UnitFilter.FocusName = r.Name;
        }
    }
}
