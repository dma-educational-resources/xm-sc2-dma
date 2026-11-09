using SC2Radar.SC2;

namespace SC2Radar.UI.ImGui.Radar
{
    /// <summary>
    /// Protoss power fields, like the game shows while placing a building: the area each Pylon (and phasing Warp Prism)
    /// powers, where buildings work and units can warp in. One merged shape per player in its colour, so overlapping
    /// pylons don't stack; a pylon still warping in shows the field it will have, dimmed.
    /// </summary>
    public static class PowerFieldLayer
    {
        static Config Config => Program.Config;

        // power field radius (LotV values): every pylon variant ("Pylon", campaign / skin ones like "Pylon_Taldarim"),
        // and a Warp Prism in phasing mode
        static float Radius(string type) =>
            type.StartsWith("Pylon", StringComparison.OrdinalIgnoreCase) ? 6.5f
            : type.Equals("WarpPrismPhasing", StringComparison.OrdinalIgnoreCase) ? 3.75f : 0f;

        public static void Draw(in RadarFrameCtx ctx, UnitEntry[] units)
        {
            if (!Config.ShowPowerFields) return;
            var byOwner = units.Where(u => u.Owner is > 0 and < 16 && Radius(u.TypeName) > 0f && UnitFilter.IsVisible(in u))
                               .DistinctBy(u => u.Record != 0 ? u.Record : u.Addr)
                               .GroupBy(u => u.Owner);
            float alpha = Math.Clamp(Config.PowerFieldAlpha, 0f, 1f);
            foreach (var g in byOwner)
            {
                uint rgb = VisionOverlay.PlayerColor(g.Key) & 0x00FFFFFF;
                // pylons still warping in power nothing yet: the field they will have, dimmed, under the working ones
                VisionOverlay.DrawUnion(in ctx, g.Where(u => u.WarpingIn).Select(u => (u.X, u.Y, Radius(u.TypeName))),
                                        rgb | ((uint)(alpha * 0.4f * 255f) << 24), rgb | 0x50000000);
                VisionOverlay.DrawUnion(in ctx, g.Where(u => !u.WarpingIn).Select(u => (u.X, u.Y, Radius(u.TypeName))),
                                        rgb | ((uint)(alpha * 255f) << 24), rgb | 0xA0000000);
            }
        }
    }
}
