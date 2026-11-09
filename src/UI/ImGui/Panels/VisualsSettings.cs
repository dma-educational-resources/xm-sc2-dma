using System.Numerics;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Radar;

namespace SC2Radar.UI.ImGui.Panels
{
    /// <summary>
    /// Settings ▸ Visuals (laid out like the EFT radar's Visuals ▸ Entity Info): per-type marker styles, game-mode markers
    /// and the profiles that hold them (RadarProfiles). The profile picked at the top is the one being edited - set up the
    /// Special Forces profile from a melee game - and the Profiles sub-tab says which one the radar uses right now.
    /// </summary>
    internal static class VisualsSettings
    {
        static int _sub, _kind, _faction, _edit = -1, _addFromMap;
        static string _newMarker = "", _rename = "", _mapMatch = "";
        static RadarProfile? _renameFor;
        static readonly string[] Subs = ["Entity Info", "Markers", "Profiles"];
        static readonly string[] Kinds = ["Army", "Workers", "Buildings", "Minerals", "Geysers", "Neutral", "Custom"];
        static readonly EntityKind[] KindValues = [EntityKind.Army, EntityKind.Worker, EntityKind.Building, EntityKind.Mineral, EntityKind.Geyser, EntityKind.Neutral];
        static readonly string[] Factions = ["All", "Terran", "Protoss", "Zerg"];
        static readonly string[] Shapes = Enum.GetNames<MarkerShape>();
        static readonly string[] Bars = ["Never", "When damaged", "Always"];

        static float W => UI.GroupInnerWidth;

        /// <summary>The open sub-tab (Settings search walks them all to index their settings).</summary>
        internal static int SubTab { get => _sub; set => _sub = value; }
        internal static int SubTabCount => Subs.Length;

        public static void Draw()
        {
            var st = RadarProfiles.S;
            if (_edit < 0 || _edit >= st.Profiles.Count)
                _edit = Math.Max(0, st.Profiles.IndexOf(RadarProfiles.Active));
            var p = st.Profiles[_edit];

            // Pull the sub-tab bar up tight against the panel's inner padding so it reads as
            // part of the parent tab strip rather than floating in empty space.
            ImGuiNET.ImGui.SetCursorPosY(MathF.Max(0f, ImGuiNET.ImGui.GetCursorPosY() - 4f));
            UI.SegmentedBar("##vis_sub", Subs, ref _sub, width: W, stretch: true);
            // SegmentedBar does not pad below itself; without this gap the next group's
            // label gets clipped against the pills (the "Profile" title visibly ran under
            // the bar on first load).
            ImGuiNET.ImGui.Dummy(new Vector2(0f, 8f));

            // The Profile picker only makes sense on the sub-tabs that edit a profile's
            // contents (Entity Info, Markers). The Profiles sub-tab manages the profile list
            // itself, so showing an "Editing" chooser above it would be redundant and
            // visually crowd the real picker inside that tab.
            if (_sub != 2)
            {
                UI.BeginGroup("Profile");
                int edit = _edit;
                if (UI.Combo("Editing##vis_edit", ref edit, [.. st.Profiles.Select(x => x == RadarProfiles.Active ? $"{x.Name}  (in use)" : x.Name)],
                        width: W, hint: "The profile these settings belong to. Each game mode can have its own - see the Profiles tab.", localizeItems: false))
                    _edit = edit;
                UI.EndGroup();
            }

            switch (_sub)
            {
                case 0: DrawEntityInfo(p); break;
                case 1: DrawMarkers(p);    break;
                case 2: DrawProfiles();    break;
            }
        }

        // ── Entity Info ──────────────────────────────────────────────────────

        static void DrawEntityInfo(RadarProfile p)
        {
            UI.BeginGroup("Entity Type");
            UI.SegmentedBar("##vis_kind", Kinds, ref _kind, width: W, stretch: true);
            if (_kind == KindValues.Length) { UI.EndGroup(); DrawCustomStyles(p); return; }
            var kind = KindValues[_kind];
            bool racial = kind is EntityKind.Army or EntityKind.Worker or EntityKind.Building;
            if (racial)
                UI.SegmentedBar("##vis_faction", Factions, ref _faction, width: W, stretch: true,
                    hint: "All: every race. Pick a race to give its units their own look - e.g. Protoss shields always shown.");
            var faction = racial ? (Faction)_faction : Faction.Any;
            string baseKey = RadarProfiles.StyleKey(kind), key = RadarProfiles.StyleKey(kind, faction);
            if (faction != Faction.Any)
            {
                bool own = p.Styles.ContainsKey(key);
                if (UI.Toggle($"Customise for {faction}##vis_own", ref own, W,
                        hint: $"Off: {faction} {Kinds[_kind].ToLowerInvariant()} use the All settings. On: their own, starting from a copy."))
                {
                    if (own) p.Styles[key] = (p.Styles.GetValueOrDefault(baseKey) ?? RadarProfiles.DefaultStyles()[baseKey]).Clone();
                    else p.Styles.Remove(key);
                    RadarProfiles.Changed();
                }
                if (!own) { UI.Caption($"Using the All settings for {faction}."); UI.EndGroup(); return; }
            }
            UI.EndGroup();

            if (!p.Styles.TryGetValue(key, out var s)) p.Styles[key] = s = RadarProfiles.DefaultStyles()[baseKey].Clone();
            if (DrawStyleBody(s, kind, faction)) RadarProfiles.Changed();
        }

        /// <summary>The style editor: Radar Display, Vitals and a preview. Returns whether anything changed.</summary>
        static bool DrawStyleBody(EntityStyle s, EntityKind kind, Faction faction)
        {
            bool changed = false;

            UI.BeginGroup("Radar Display");
            bool show = s.Show;
            if (UI.Toggle("Show##vis_show", ref show, W, hint: "Draw this type at all (game-mode markers still show).")) { s.Show = show; changed = true; }
            UI.BeginDisabled(s.Show ? null : "This type is hidden");
            UI.BeginSliderGroup("Shape", "Size");
            int shape = (int)s.Shape;
            if (UI.Combo("Shape##vis_shape", ref shape, Shapes, width: W, inlineLabel: true, localizeItems: false)) { s.Shape = (MarkerShape)shape; changed = true; }
            float size = s.Size;
            if (UI.SliderFloat("Size##vis_size", ref size, 1f, 12f, "%.1f", suffix: " px", width: W, smallChange: 0.5f,
                    hint: "Marker radius, before Display ▸ Marker Scale.")) { s.Size = size; changed = true; }
            UI.EndSliderGroup();
            UI.DrawGrid(W,
            [
                new(w => { bool v = s.TeamColor; if (UI.Toggle("Team Colour##vis_team", ref v, w, hint: "The owner's colour (Colours ▸ alliance colours apply). Off: the colour below.")) { s.TeamColor = v; changed = true; } }),
                new(w => { bool v = s.Outline;   if (UI.Toggle("Outline##vis_outline", ref v, w, hint: "A thin dark edge so markers stand out on terrain and creep."))                     { s.Outline = v;   changed = true; } }),
                new(w => { bool v = s.Label;     if (UI.Toggle("Name Label##vis_label", ref v, w, hint: "The unit's name once zoomed in (Display ▸ labels must be on)."))                 { s.Label = v;     changed = true; } }),
            ]);
            if (!s.TeamColor)
            {
                var c = ToRgba(s.Color);
                if (UI.ColorEdit("Colour##vis_col", ref c, W)) { s.Color = FromRgba(c); changed = true; }
            }
            UI.EndDisabled();
            UI.EndGroup();

            UI.BeginGroup("Vitals", disabled: !s.Show, disabledReason: "This type is hidden");
            if (kind is EntityKind.Mineral or EntityKind.Geyser)
            {
                bool amount = s.Amount;
                if (UI.Toggle("Amount Left##vis_amount", ref amount, W, hint: "Minerals / gas left, once zoomed in.")) { s.Amount = amount; changed = true; }
            }
            else
            {
                UI.BeginSliderGroup("Health Bar", "Shield Bar");
                int hp = (int)s.Health;
                if (UI.Combo("Health Bar##vis_hp", ref hp, Bars, width: W, inlineLabel: true, localizeItems: false)) { s.Health = (BarMode)hp; changed = true; }
                int sh = (int)s.Shields;
                if (UI.Combo("Shield Bar##vis_sh", ref sh, Bars, width: W, inlineLabel: true, localizeItems: false,
                        hint: "Protoss shields, above the health bar.")) { s.Shields = (BarMode)sh; changed = true; }
                UI.EndSliderGroup();
                bool energy = s.Energy;
                if (UI.Toggle("Energy##vis_energy", ref energy, W, hint: "Energy figure next to the bars once zoomed in.")) { s.Energy = energy; changed = true; }
            }
            UI.EndGroup();

            UI.BeginGroup("Preview");
            DrawPreview(s, kind, faction);
            UI.EndGroup();

            return changed;
        }

        // ── Custom styles ────────────────────────────────────────────────────

        static int _custom, _customFromMap;
        static string _newCustomName = "", _newCustomPattern = "";

        /// <summary>
        /// Named styles for specific units (RadarProfile.CustomStyles), over the kind / race styles: e.g. "Static defence" for
        /// Spine Crawler, Spore Crawler and Primal Sunken Colony as small dots without labels. A new one starts as a copy of
        /// the style its first matching unit uses now, so only what you change differs.
        /// </summary>
        static void DrawCustomStyles(RadarProfile p)
        {
            var units = Memory.Instance?.Snapshot.Units ?? [];

            UI.BeginGroup("Add Custom Style");
            UI.Caption("Give specific units their own look, over their type's (Army, Buildings, ...). The first matching style wins.");
            UI.InputText("Name##vis_cs_new_name", ref _newCustomName, placeholder: "Static defence", width: W);
            UI.InputText("Units##vis_cs_new_pat", ref _newCustomPattern, placeholder: "=SpineCrawler, =SporeCrawler, buff:Red", width: W,
                hint: "Comma-separated: a part of the name, '=' an exact name or type, '*' a wildcard, 'buff:Red' units with that buff / debuff.");
            if (UI.Button("Add##vis_cs_add", new Vector2(W, 0f)) && _newCustomPattern.Trim().Length > 0)
            {
                AddCustom(p, _newCustomName.Trim().Length > 0 ? _newCustomName.Trim() : _newCustomPattern.Trim(), _newCustomPattern.Trim(), units);
                _newCustomName = _newCustomPattern = "";
            }
            var onMap = units.Where(u => u.TypeName.Length > 0).Select(u => u.TypeName).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            if (onMap.Length > 0)
            {
                _customFromMap = Math.Clamp(_customFromMap, 0, onMap.Length - 1);
                UI.Combo("From the map##vis_cs_from_map", ref _customFromMap, onMap, width: W, hint: "Unit types on the map right now.",
                    localizeItems: false, maxVisibleItems: 14);
                if (UI.Button("New style for this type##vis_cs_add_type", new Vector2(W, 0f), ButtonStyle.Ghost))
                    AddCustom(p, PrettyNames.Get(onMap[_customFromMap]), "=" + onMap[_customFromMap], units);
            }
            UI.EndGroup();

            UI.BeginGroup($"Custom Styles in {p.Name}");
            if (p.CustomStyles.Count == 0) { UI.Caption("None yet."); UI.EndGroup(); return; }
            _custom = Math.Clamp(_custom, 0, p.CustomStyles.Count - 1);
            UI.Combo("Editing##vis_cs_pick", ref _custom, [.. p.CustomStyles.Select(c => c.Enabled ? c.Name : $"{c.Name}  (off)")], width: W, localizeItems: false);
            var cs = p.CustomStyles[_custom];
            bool changed = false;
            bool on = cs.Enabled;
            if (UI.Toggle("Enabled##vis_cs_on", ref on, W)) { cs.Enabled = on; changed = true; }
            string name = cs.Name;
            if (UI.InputText("Name##vis_cs_name", ref name, width: W) && name.Trim().Length > 0) { cs.Name = name.Trim(); changed = true; }
            string pat = cs.Pattern;
            if (UI.InputText("Units##vis_cs_pat", ref pat, width: W,
                    hint: "Comma-separated: a part of the name, '=' an exact name or type, '*' a wildcard, 'buff:Red' units with that buff / debuff."))
            { cs.Pattern = pat; changed = true; }
            int matching = 0;
            foreach (ref readonly var u in units.AsSpan()) if (RadarProfiles.CustomFor(in u) == cs) matching++;
            UI.Caption(ReferenceEquals(p, RadarProfiles.Active) ? $"{matching} on the map use this style." : "This profile isn't in use, so nothing on the map uses it now.");
            float half = (W - 6f) * 0.5f;
            if (UI.Button("Move up##vis_cs_up", new Vector2(half, 0f), ButtonStyle.Ghost, hint: "Earlier styles win when several match.") && _custom > 0)
            { (p.CustomStyles[_custom - 1], p.CustomStyles[_custom]) = (cs, p.CustomStyles[_custom - 1]); _custom--; changed = true; }
            ImGuiNET.ImGui.SameLine(0f, 6f);
            if (UI.Button("Remove##vis_cs_del", new Vector2(half, 0f), ButtonStyle.Danger))
            { p.CustomStyles.Remove(cs); RadarProfiles.Changed(); UI.EndGroup(); return; }
            UI.EndGroup();

            // the preview and the bar options follow the first unit using it (else Army)
            var sample = units.FirstOrDefault(u => RadarProfiles.CustomFor(in u) == cs);
            var kind = sample.TypeName != null && RadarProfiles.CustomFor(in sample) == cs ? RadarProfiles.KindOf(in sample) : EntityKind.Army;
            var faction = sample.TypeName != null && RadarProfiles.CustomFor(in sample) == cs ? RadarProfiles.FactionOf(in sample) : Faction.Any;
            if (DrawStyleBody(cs.Style, kind, faction)) changed = true;
            if (changed) RadarProfiles.Changed();
        }

        static void AddCustom(RadarProfile p, string name, string pattern, UnitEntry[] units)
        {
            var cs = new CustomStyle { Name = name, Pattern = pattern };
            // start from the look its first matching unit has now
            cs.Style = units.Where(u => cs.Patterns.Any(x => UnitFilter.PatternMatches(x, in u) || u.TypeName.Length > 0 && string.Equals(x.TrimStart('='), u.TypeName, StringComparison.OrdinalIgnoreCase)))
                            .Select(u => RadarProfiles.StyleFor(in u).Clone()).FirstOrDefault() ?? RadarProfiles.StyleFor(EntityKind.Army, Faction.Any).Clone();
            p.CustomStyles.Add(cs);
            _custom = p.CustomStyles.Count - 1;
            RadarProfiles.Changed();
        }

        /// <summary>A sample of the style as the radar draws it: marker, bars and label pill.</summary>
        static void DrawPreview(EntityStyle s, EntityKind kind, Faction faction)
        {
            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            var tl = ImGuiNET.ImGui.GetCursorScreenPos();
            float h = 56f;
            ImGuiNET.ImGui.Dummy(new Vector2(W, h));
            dl.AddRectFilled(tl, tl + new Vector2(W, h), 0xFF2A2624, 4f);   // radar-ish ground
            if (!s.Show) { dl.AddText(tl + new Vector2(10f, h * 0.5f - 7f), 0x90FFFFFF, "Hidden"); return; }
            uint col = s.TeamColor ? (faction == Faction.Protoss ? 0xFFE8A040u : 0xFF4060E8u) : s.Color;
            float r = s.Size * Program.Config.UnitDotRadius / 3.5f;
            var pos = tl + new Vector2(28f, h * 0.5f - 4f);
            RadarDraw.DrawMarker(dl, pos, s.Shape, r, col, s.Outline);
            float bw = MathF.Max(10f, r * 2.6f), bh = MathF.Max(2f, r * 0.42f);
            var bl = pos + new Vector2(-bw * 0.5f, r + 3f);
            if (kind is not (EntityKind.Mineral or EntityKind.Geyser))
            {
                if (s.Shields != BarMode.Never && faction is Faction.Protoss or Faction.Any) { RadarDraw.DrawBar(dl, bl, bw, bh, 0.55f, 0xFFFFA050u); bl.Y += bh + 2f; }
                if (s.Health != BarMode.Never) RadarDraw.DrawBar(dl, bl, bw, bh, 0.7f, 0xFF40D060u);
            }
            if (s.Label)
                RadarDraw.DrawPill(dl, pos + new Vector2(r + 4f, -8f), kind switch
                {
                    EntityKind.Army => faction == Faction.Protoss ? "Stalker" : "Marine",
                    EntityKind.Worker => faction == Faction.Protoss ? "Probe" : "SCV",
                    EntityKind.Building => faction == Faction.Protoss ? "Gateway" : "Barracks",
                    EntityKind.Mineral => "Mineral Field", EntityKind.Geyser => "Vespene Geyser", _ => "Critter",
                }, 0xF0F0F0F0u);
        }

        // ── Markers ──────────────────────────────────────────────────────────

        static void DrawMarkers(RadarProfile p)
        {
            UI.BeginGroup("Add Marker");
            UI.Caption("Mark specific units in this profile's game modes: they get their own shape, colour and a glow, over their type's style.");
            UI.InputText("##vis_new_marker", ref _newMarker, placeholder: "Name or pattern: ReptileCrate, Hero*, =ghost", width: W,
                hint: "Matched against the unit's name, type and model path. '*' is a wildcard, '=' an exact name; otherwise a part of the name.");
            if (UI.Button("Add##vis_add_marker", new Vector2(W, 0f)) && _newMarker.Trim().Length > 0)
            {
                p.Markers.Add(new MarkerRule { Pattern = _newMarker.Trim(), Color = UnitFilter.HighlightPalette[p.Markers.Count % UnitFilter.HighlightPalette.Length] });
                _newMarker = "";
                RadarProfiles.Changed();
            }
            var onMap = (Memory.Instance?.Snapshot.Units ?? []).Where(u => u.TypeName.Length > 0).Select(u => u.TypeName)
                                                               .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            if (onMap.Length > 0)
            {
                _addFromMap = Math.Clamp(_addFromMap, 0, onMap.Length - 1);
                UI.Combo("From the map##vis_from_map", ref _addFromMap, onMap, width: W, hint: "Unit types on the map right now.",
                    localizeItems: false, maxVisibleItems: 14);
                if (UI.Button("Mark this type##vis_mark_type", new Vector2(W, 0f), ButtonStyle.Ghost))
                {
                    p.Markers.Add(new MarkerRule { Pattern = "=" + onMap[_addFromMap], Color = UnitFilter.HighlightPalette[p.Markers.Count % UnitFilter.HighlightPalette.Length] });
                    RadarProfiles.Changed();
                }
            }
            UI.EndGroup();

            // categories: one switch each for a whole group of markers ("Loot", "Bosses", ...)
            var categories = p.Markers.Select(m => m.Category ?? "").Where(c => c.Length > 0)
                                      .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
            if (categories.Count > 0)
            {
                UI.BeginGroup("Categories");
                UI.DrawGrid(W, [.. categories.Select(c => new UI.GridItem(w =>
                {
                    bool on = !p.HiddenCategories.Contains(c);
                    int n = p.Markers.Count(m => string.Equals(m.Category, c, StringComparison.OrdinalIgnoreCase));
                    if (UI.Toggle($"{c}  ({n})##vis_cat_{c}", ref on, w, hint: $"Off: the {n} marker{(n == 1 ? "" : "s")} in {c} don't apply."))
                    {
                        if (on) p.HiddenCategories.Remove(c); else p.HiddenCategories.Add(c);
                        RadarProfiles.Changed();
                    }
                }))]);
                UI.EndGroup();
            }

            UI.BeginGroup($"Markers in {p.Name}");
            if (p.Markers.Count == 0) UI.Caption("None yet.");
            // grouped by category (uncategorised first), in the order they were added within each
            var order = p.Markers.Select((m, i) => (m, i)).OrderBy(x => (x.m.Category ?? "").Length > 0).ThenBy(x => x.m.Category ?? "", StringComparer.OrdinalIgnoreCase).ToList();
            string? shownCat = null;
            foreach (var (m, i) in order)
            {
                string cat = m.Category ?? "";
                if (cat.Length > 0 && !string.Equals(cat, shownCat, StringComparison.OrdinalIgnoreCase)) { UI.Caption(cat); shownCat = cat; }
                bool on = m.Enabled;
                string title = m.Label.Length > 0 ? $"{m.Label}  ({m.Pattern})" : m.Pattern;
                if (UI.ToggleWithPopover($"{title}##vis_m{i}", ref on, () => DrawMarkerPopover(p, m), width: W))
                { m.Enabled = on; RadarProfiles.Changed(); }
            }
            UI.EndGroup();

            DrawProfileFilterRules(p);
        }

        static string _newRule = "";

        /// <summary>
        /// Unit Filter rules for this profile only (on top of the Unit Filter panel's global ones): patterns never drawn
        /// while the profile is in use, and highlight rings - e.g. hide a mode's decorative units, ring its objectives.
        /// </summary>
        static void DrawProfileFilterRules(RadarProfile p)
        {
            UI.BeginGroup($"Hide / Highlight in {p.Name}");
            UI.Caption("Unit Filter rules that only apply while this profile is in use, on top of the global ones in the Unit Filter panel.");
            UI.InputText("##vis_new_rule", ref _newRule, placeholder: "Name or pattern: *Doodad*, =Beacon", width: W,
                hint: "Same patterns as markers: '*' is a wildcard, '=' an exact name; otherwise a part of the name or model path.");
            string pat = _newRule.Trim();
            float half = (W - 6f) * 0.5f;
            if (UI.Button("Hide##vis_rule_hide", new Vector2(half, 0f), ButtonStyle.Ghost) && pat.Length > 0 && !p.Hide.Contains(pat, StringComparer.OrdinalIgnoreCase))
            { p.Hide.Add(pat); _newRule = ""; RadarProfiles.Changed(); }
            ImGuiNET.ImGui.SameLine(0f, 6f);
            if (UI.Button("Highlight##vis_rule_hl", new Vector2(half, 0f)) && pat.Length > 0)
            {
                p.Highlights.Add(new HighlightRule { Pattern = pat, Color = UnitFilter.HighlightPalette[p.Highlights.Count % UnitFilter.HighlightPalette.Length] });
                _newRule = "";
                RadarProfiles.Changed();
            }

            if (p.Hide.Count == 0 && p.Highlights.Count == 0) UI.Caption("None yet.");
            for (int i = 0; i < p.Hide.Count; i++)
            {
                if (UI.Button($"Hidden: {p.Hide[i]}   ×##vis_rh{i}", new Vector2(W, 0f), ButtonStyle.Ghost, hint: "Click to remove this rule."))
                { p.Hide.RemoveAt(i); RadarProfiles.Changed(); break; }
            }
            for (int i = 0; i < p.Highlights.Count; i++)
            {
                var h = p.Highlights[i];
                bool on = h.Enabled;
                if (UI.ToggleWithPopover($"Ring: {h.Pattern}##vis_rr{i}", ref on, () =>
                {
                    var c = ToRgba(h.Color);
                    if (UI.ColorEdit("Colour##vis_rrc", ref c, 260f)) { h.Color = FromRgba(c); RadarProfiles.Changed(); }
                    if (UI.Button("Remove##vis_rrx", new Vector2(260f, 0f), ButtonStyle.Danger)) { p.Highlights.Remove(h); RadarProfiles.Changed(); }
                }, width: W))
                { h.Enabled = on; RadarProfiles.Changed(); }
            }
            UI.EndGroup();
        }

        static void DrawMarkerPopover(RadarProfile p, MarkerRule m)
        {
            bool changed = false;
            string pattern = m.Pattern;
            if (UI.InputText("Pattern##vis_mp", ref pattern, width: 260f)) { m.Pattern = pattern; changed = true; }
            string label = m.Label;
            if (UI.InputText("Label##vis_ml", ref label, placeholder: "the unit's own name", width: 260f)) { m.Label = label; changed = true; }
            string category = m.Category ?? "";
            if (UI.InputText("Category##vis_mcat", ref category, placeholder: "Loot, Bosses, ... (optional)", width: 260f,
                    hint: "Markers in the same category get one switch together in the Categories group."))
            { m.Category = category.Trim(); changed = true; }
            int shape = (int)m.Shape;
            if (UI.Combo("Shape##vis_ms", ref shape, Shapes, width: 260f, inlineLabel: true, localizeItems: false)) { m.Shape = (MarkerShape)shape; changed = true; }
            var c = ToRgba(m.Color);
            if (UI.ColorEdit("Colour##vis_mc", ref c, 260f)) { m.Color = FromRgba(c); changed = true; }
            float size = m.Size;
            if (UI.SliderFloat("Size##vis_mz", ref size, 2f, 14f, "%.1f", suffix: " px", width: 260f, smallChange: 0.5f)) { m.Size = size; changed = true; }
            bool glow = m.Glow;
            if (UI.Toggle("Glow##vis_mg", ref glow, 260f)) { m.Glow = glow; changed = true; }
            bool always = m.AlwaysLabel;
            if (UI.Toggle("Label at any zoom##vis_ma", ref always, 260f)) { m.AlwaysLabel = always; changed = true; }
            int below = m.HealthBelow;
            if (UI.SliderInt("Only under health##vis_mhb", ref below, 0, 100, width: 260f,
                    hint: "Mark the unit only while its health is under this % (0 = at any health) - e.g. a boss getting low."))
            { m.HealthBelow = below; changed = true; }
            int alert = m.Alert;
            if (UI.SegmentedBar("Alert##vis_mal", ["Off", "Toast", "Toast + sound"], ref alert, 260f, stretch: true,
                    hint: "When a unit starts matching (it appears, or drops under the health limit): a notification, and optionally the system sound."))
            { m.Alert = alert; changed = true; }
            if (UI.Button("Remove marker##vis_mr", new Vector2(260f, 0f), ButtonStyle.Danger)) { p.Markers.Remove(m); changed = true; }
            if (changed) RadarProfiles.Changed();
        }

        // ── Profiles ─────────────────────────────────────────────────────────

        static void DrawProfiles()
        {
            var st = RadarProfiles.S;
            var active = RadarProfiles.Active;
            string map = Memory.Instance?.MapName ?? "";

            UI.BeginGroup("In Use");
            UI.KVRow("Map", map.Length > 0 ? map : "-");
            UI.KVRow("Profile", st.Forced.Length > 0 ? $"{active.Name} (chosen)" : $"{active.Name} (by map)");
            string[] uses = ["Pick by map", .. st.Profiles.Select(x => x.Name)];
            int use = st.Forced.Length == 0 ? 0 : Math.Max(0, st.Profiles.FindIndex(x => x.Name == st.Forced) + 1);
            if (UI.Combo("Use##vis_use", ref use, uses, width: W, localizeItems: false,
                    hint: "Pick by map: the first profile whose map names match the current map, else Default."))
            { st.Forced = use == 0 ? "" : st.Profiles[use - 1].Name; RadarProfiles.Changed(); }
            UI.EndGroup();

            var p = st.Profiles[Math.Clamp(_edit, 0, st.Profiles.Count - 1)];
            UI.BeginGroup($"Profile: {p.Name}");
            if (_renameFor != p) { _renameFor = p; _rename = p.Name; _mapMatch = string.Join(", ", p.MapMatch); }
            if (p.Name != "Default")
            {
                UI.InputText("Name##vis_pname", ref _rename, width: W);
                if (_rename.Trim().Length > 0 && _rename.Trim() != p.Name && !st.Profiles.Any(x => x != p && x.Name.Equals(_rename.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                    UI.Button("Rename##vis_prename", new Vector2(W, 0f), ButtonStyle.Ghost))
                {
                    if (st.Forced == p.Name) st.Forced = _rename.Trim();
                    p.Name = _rename.Trim();
                    RadarProfiles.Changed();
                }
                if (UI.InputText("Map names##vis_pmaps", ref _mapMatch, placeholder: "Special Forces, Elite", width: W,
                        hint: "Comma-separated parts of map names this profile is for. The first matching profile is used."))
                {
                    p.MapMatch = [.. _mapMatch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    RadarProfiles.Changed();
                }
                if (map.Length > 0 && UI.Button($"Use for this map ({map})##vis_pthis", new Vector2(W, 0f), ButtonStyle.Ghost))
                {
                    if (!p.MapMatch.Contains(map, StringComparer.OrdinalIgnoreCase)) p.MapMatch.Add(map);
                    _mapMatch = string.Join(", ", p.MapMatch);
                    RadarProfiles.Changed();
                }
            }
            else UI.Caption("Default is used when no other profile matches the map. It can't be renamed or removed.");
            if (UI.Button("New profile (copy of this one)##vis_pnew", new Vector2(W, 0f)))
            {
                var n = RadarProfiles.AddProfile(map.Length > 0 ? map : "New profile", p);
                if (map.Length > 0) n.MapMatch.Add(map);
                _edit = st.Profiles.IndexOf(n);
                RadarProfiles.Changed();
            }
            if (p.Name != "Default" && UI.Button("Delete this profile##vis_pdel", new Vector2(W, 0f), ButtonStyle.Danger))
            {
                RadarProfiles.RemoveProfile(p);
                _edit = 0;
            }
            UI.EndGroup();

            // C6 / A4: which widgets this profile keeps closed, and the layout it brings with it
            UI.BeginGroup("Widgets & Layout");
            var presets = Program.Config.LayoutPresets;
            string[] layouts = ["Leave the layout alone", .. presets.Select(x => x.Name)];
            int li = p.LayoutPreset.Length == 0 ? 0 : Math.Max(0, presets.FindIndex(x => x.Name == p.LayoutPreset) + 1);
            if (UI.Combo("Layout##vis_playout", ref li, layouts, width: W, localizeItems: false,
                    hint: "A layout preset (Settings ▸ Widgets ▸ Layout Presets) put in place whenever this profile takes over."))
            { p.LayoutPreset = li == 0 ? "" : presets[li - 1].Name; RadarProfiles.Changed(); }
            if (presets.Count == 0) UI.Caption("Save a layout in Settings ▸ Widgets ▸ Layout Presets to pick it here.");
            UI.Caption("Widgets shown with this profile (off: kept closed while it is in use):");
            var titles = ImGuiApp.Menus?.Widgets?.WidgetTitles.ToList() ?? [];
            UI.DrawGrid(W, [.. titles.Select(t => new UI.GridItem(w =>
            {
                bool on = !p.HiddenWidgets.Contains(t);
                if (UI.Toggle($"{t}##vis_pw_{t}", ref on, w))
                {
                    if (on) p.HiddenWidgets.Remove(t); else p.HiddenWidgets.Add(t);
                    RadarProfiles.Changed();
                }
            }))]);
            UI.EndGroup();

            UI.BeginGroup("Share");
            UI.Caption("Export writes this profile to a file in the exports folder. Put a profile file someone sent you in that folder and import it.");
            if (UI.Button($"Export {p.Name}##vis_pexp", new Vector2(W, 0f)))
                _shareStatus = RadarProfiles.Export(p) is { } path ? $"Saved {System.IO.Path.GetFileName(path)}" : "Export failed";
            var files = RadarProfiles.ExportFiles();
            if (files.Length > 0)
            {
                _importIdx = Math.Clamp(_importIdx, 0, files.Length - 1);
                UI.Combo("File##vis_pimpf", ref _importIdx, [.. files.Select(System.IO.Path.GetFileNameWithoutExtension)], width: W, localizeItems: false);
                if (UI.Button("Import as a new profile##vis_pimp", new Vector2(W, 0f), ButtonStyle.Ghost))
                {
                    var n = RadarProfiles.Import(files[_importIdx]);
                    _shareStatus = n != null ? $"Imported as {n.Name}" : "That file isn't a radar profile";
                    if (n != null) _edit = st.Profiles.IndexOf(n);
                }
            }
            if (UI.Button("Open exports folder##vis_pfolder", new Vector2(W, 0f), ButtonStyle.Ghost))
            {
                System.IO.Directory.CreateDirectory(RadarProfiles.ExportFolder);
                System.Diagnostics.Process.Start("explorer.exe", RadarProfiles.ExportFolder);
            }
            if (_shareStatus.Length > 0) UI.Caption(_shareStatus);
            UI.EndGroup();
        }

        static int _importIdx;
        static string _shareStatus = "";

        static Vector4 ToRgba(uint abgr) => RadarDraw.AbgrToRgba(abgr);
        static uint FromRgba(Vector4 c) => RadarDraw.RgbaToAbgr(c);
    }
}
