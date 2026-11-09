using System.Numerics;
using ImGuiNET;
using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// The radar's hover card for units (same look as the EFT radar's entity tooltip): the unit's name in its player's
    /// colour, owner and kind underneath, then aligned label / value rows - vitals, production, range, cost, ... - and the
    /// other unit types under the cursor at the bottom. Held Shift lifts the width limit.
    /// </summary>
    internal static class UnitHoverCard
    {
        const float LabelColX = 78f, MaxW = 320f;

        public static void Draw(List<(UnitEntry First, int Count)> under, Vector2 mouse)
        {
            if (under.Count == 0) return;
            var io = ImGuiNET.ImGui.GetIO();
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, UI.ToV4(UI.Alpha(ImGuiStyle.PanelBg, 250)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border,  UI.ToV4(UI.Alpha(ImGuiStyle.Border, 180)));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,   new Vector2(10f, 8f));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,   6f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
            ImGuiNET.ImGui.SetNextWindowSizeConstraints(Vector2.Zero, new Vector2(io.KeyShift ? float.MaxValue : MaxW, 600f));
            ImGuiNET.ImGui.SetNextWindowSize(Vector2.Zero);   // auto-size to the content every frame
            ImGuiNET.ImGui.SetNextWindowPos(mouse + new Vector2(24f, 24f), ImGuiCond.Always);
            ImGuiNET.ImGui.BeginTooltip();
            try { Content(under); }
            finally
            {
                ImGuiNET.ImGui.EndTooltip();
                ImGuiNET.ImGui.PopStyleVar(3);
                ImGuiNET.ImGui.PopStyleColor(2);
            }
        }

        static void Content(List<(UnitEntry First, int Count)> under)
        {
            var (u, n) = under[0];

            // header: name in the player's colour, owner and kind underneath
            ImGuiNET.ImGui.PushFont(ImGuiStyle.FontBold);
            ImGuiNET.ImGui.TextColored(UI.ToV4(PlayerColors.For(in u) | 0xFF000000), n > 1 ? $"{u.DisplayName} ×{n}" : u.DisplayName);
            ImGuiNET.ImGui.PopFont();
            SubLabel($"{RadarWindow.OwnerLabel(in u)}  ·  {u.Category.ToString().ToLowerInvariant()}");
            Separator();

            if (u.Category == UnitCategory.Resource)
            {
                if (u.Resources >= 0) Row("Left", RadarWindow.ResourceText(u.Resources));
            }
            else
            {
                // vitals
                if (u.MaxHp > 0f)
                {
                    float hp = u.MaxHp - u.Damage, f = hp / u.MaxHp;
                    Row("Health", $"{hp:0} / {u.MaxHp:0}", f > 0.6f ? 0xFF50D050u : f > 0.3f ? 0xFF30CCCCu : 0xFF5050FFu);
                }
                else if (u.Damage > 0f) Row("Damage", $"{u.Damage:0} taken", 0xFF5050FFu);
                if (u.MaxShields > 0f) Row("Shields", $"{u.MaxShields - u.ShieldDamage:0} / {u.MaxShields:0}", 0xFFFF9050u);
                if (u.MaxEnergy > 0f || u.Energy >= 0.5f) Row("Energy", u.MaxEnergy > 0f ? $"{u.Energy:0} / {u.MaxEnergy:0}" : $"{u.Energy:0}", 0xFFFF60C0u);

                // production
                string Left() => u.TimeLeft >= 0f ? $"  ·  {SC2Memory.FormatClock(u.TimeLeft)} left" : "";
                if (u.Progress >= 0f)
                    Row("Morphing", $"{(ProductionWidget.MorphTarget(in u) is { } to ? to + "  " : "")}{u.Progress * 100f:0}%{Left()}", 0xFF40E0FFu);
                else if (u.QueueProgress >= 0f)
                {
                    Row("Producing", $"{(u.QueueProduct.Length > 0 ? u.QueueProduct + "  " : "")}{u.QueueProgress * 100f:0}%{Left()}", 0xFFFFD040u);
                    if (u.QueueCount > 1) Row("Queued", $"{u.QueueCount - 1} more");
                }
                else if (u.CanQueue && u.IsBuilding) Row("Producing", "nothing (idle)", ImGuiStyle.TextDim);

                // combat and vision
                if (u.Range > 0f) Row("Range", $"{u.Range:0.##}");
                // the radius is the melee value for the type, else a guess: say so rather than pass it off as read from the game
                if (RadarWindow.DetectionRadius(in u) is { } det) Row("Detects", RadarWindow.DetectionRadiusKnown(in u) ? $"{det:0.##}" : $"~{det:0.##} (est.)", 0xFFFFC060u);
                if (u.Sight > 0f) Row("Sight", $"{u.Sight:0.#}");

                // cost
                if (u.Minerals > 0 || u.Gas > 0) Row("Cost", $"{u.Minerals:N0} / {u.Gas:N0}");
                if (u.Food < 0f) Row("Supply", $"{-u.Food:0.#}");
                else if (u.Food > 0f) Row("Provides", $"{u.Food:0} supply");
            }
            Row("Position", $"{u.X:0.0}, {u.Y:0.0}");
            if (u.Burrowed || u.Cloaked) Row("Status", u.Burrowed ? "burrowed" : "cloaked", 0xFFFFFFFFu);
            if (CargoInfo.Describe(u.Record) is { Length: > 0 } cargo) Row($"Carrying {CargoInfo.Count(u.Record)}", cargo, 0xFFFFFFFFu);
            if (OrderInfo.Describe(u.Record) is { Length: > 0 } orders) Row("Orders", orders, 0xFFFFFFFFu);
            if (u.Record != 0 && DMA.Memory.Instance?.UnitBuffs.TryGetValue(u.Record, out var buffs) == true)
                Row("Buffs", string.Join(", ", buffs!.Select(PrettyNames.Get)), 0xFFC0E0FFu);

            // everything else under the cursor
            if (under.Count > 1)
            {
                Separator();
                SubLabel("Also here: " + string.Join(", ", under.Skip(1).Select(g => g.Count > 1 ? $"{g.First.DisplayName} ×{g.Count}" : g.First.DisplayName)));
            }
            Separator();
            SubLabel("Right-click for options  ·  Shift: full width");
        }

        static void Separator()
        {
            var pos = ImGuiNET.ImGui.GetCursorScreenPos();
            float w = MathF.Max(ImGuiNET.ImGui.GetContentRegionAvail().X, 1f);
            ImGuiNET.ImGui.GetWindowDrawList().AddRectFilled(new Vector2(pos.X, pos.Y + 3f), new Vector2(pos.X + w, pos.Y + 4f), UI.Alpha(ImGuiStyle.Border, 90));
            ImGuiNET.ImGui.Dummy(new Vector2(w, 7f));
        }

        static void SubLabel(string text)
        {
            ImGuiNET.ImGui.PushFont(ImGuiStyle.FontSmall);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, UI.ToV4(ImGuiStyle.TextDim));
            ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetIO().KeyShift ? 0f : ImGuiNET.ImGui.GetCursorPosX() + MaxW - 20f);
            ImGuiNET.ImGui.TextUnformatted(text);
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopFont();
        }

        /// <summary>A dim label and its value at a fixed column, so the rows line up.</summary>
        static void Row(string label, string value, uint valueCol = 0)
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, UI.ToV4(ImGuiStyle.TextDim));
            ImGuiNET.ImGui.TextUnformatted(label);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.SameLine(LabelColX);
            if (valueCol != 0) ImGuiNET.ImGui.TextColored(UI.ToV4(valueCol), value);
            else ImGuiNET.ImGui.TextUnformatted(value);
        }
    }
}
