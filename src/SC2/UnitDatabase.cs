namespace SC2Radar.SC2
{
    public enum Race { Terran, Zerg, Protoss, Neutral }

    /// <summary>Scenery / Other / Effect are render-actor categories (doodads; decals, skybox; impacts, missiles, blood).
    /// Resource: mineral fields and vespene geysers (models under assets\buildings\resources).</summary>
    public enum UnitCategory { Unit, Building, Worker, Hero, Critter, Scenery, Other, Effect, Resource }

    public sealed record UnitDef(
        int       TypeId,
        string    Name,
        Race      Race,
        UnitCategory Category,
        uint      Color  // ABGR packed for ImGui
    );

    /// <summary>
    /// SC2 unit type database.  TypeId values are the integer stored in the unit
    /// slot's type field (offset TBD via research - start with owner coloring while
    /// type field is still being mapped).
    /// </summary>
    public static class UnitDatabase
    {
        // ── Terran units ──────────────────────────────────────────────────────
        static readonly UnitDef[] _terranUnits =
        [
            new(0x0001, "Marine",          Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x0002, "Reaper",          Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x0003, "Ghost",           Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x0004, "Marauder",        Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x0005, "Siege Tank",      Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x0006, "Hellion",         Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x0007, "Hellbat",         Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x0008, "Cyclone",         Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x0009, "Widow Mine",      Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x000A, "Thor",            Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x000B, "Viking",          Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x000C, "Liberator",       Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x000D, "Raven",           Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x000E, "Banshee",         Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x000F, "Battlecruiser",   Race.Terran, UnitCategory.Unit,     0xFF4080FF),
            new(0x0010, "SCV",             Race.Terran, UnitCategory.Worker,   0xFF80C0FF),
            new(0x0011, "Medivac",         Race.Terran, UnitCategory.Unit,     0xFF4080FF),
        ];

        static readonly UnitDef[] _terranBuildings =
        [
            new(0x0100, "Command Center",  Race.Terran, UnitCategory.Building, 0xFF204060),
            new(0x0101, "Supply Depot",    Race.Terran, UnitCategory.Building, 0xFF204060),
            new(0x0102, "Refinery",        Race.Terran, UnitCategory.Building, 0xFF204060),
            new(0x0103, "Barracks",        Race.Terran, UnitCategory.Building, 0xFF204060),
            new(0x0104, "Engineering Bay", Race.Terran, UnitCategory.Building, 0xFF204060),
            new(0x0105, "Factory",         Race.Terran, UnitCategory.Building, 0xFF204060),
            new(0x0106, "Starport",        Race.Terran, UnitCategory.Building, 0xFF204060),
            new(0x0107, "Missile Turret",  Race.Terran, UnitCategory.Building, 0xFF204060),
            new(0x0108, "Bunker",          Race.Terran, UnitCategory.Building, 0xFF204060),
            new(0x0109, "Armory",          Race.Terran, UnitCategory.Building, 0xFF204060),
            new(0x010A, "Fusion Core",     Race.Terran, UnitCategory.Building, 0xFF204060),
        ];

        // ── Zerg units ────────────────────────────────────────────────────────
        static readonly UnitDef[] _zergUnits =
        [
            new(0x0200, "Drone",           Race.Zerg, UnitCategory.Worker,   0xFF40C060),
            new(0x0201, "Zergling",        Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x0202, "Baneling",        Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x0203, "Roach",           Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x0204, "Ravager",         Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x0205, "Hydralisk",       Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x0206, "Lurker",          Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x0207, "Infestor",        Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x0208, "Swarm Host",      Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x0209, "Ultralisk",       Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x020A, "Mutalisk",        Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x020B, "Corruptor",       Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x020C, "Brood Lord",      Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x020D, "Viper",           Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x020E, "Queen",           Race.Zerg, UnitCategory.Unit,     0xFF20C040),
            new(0x020F, "Overlord",        Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
            new(0x0210, "Overseer",        Race.Zerg, UnitCategory.Unit,     0xFF40FF40),
        ];

        static readonly UnitDef[] _zergBuildings =
        [
            new(0x0300, "Hatchery",        Race.Zerg, UnitCategory.Building, 0xFF204030),
            new(0x0301, "Lair",            Race.Zerg, UnitCategory.Building, 0xFF204030),
            new(0x0302, "Hive",            Race.Zerg, UnitCategory.Building, 0xFF204030),
            new(0x0303, "Spawning Pool",   Race.Zerg, UnitCategory.Building, 0xFF204030),
            new(0x0304, "Roach Warren",    Race.Zerg, UnitCategory.Building, 0xFF204030),
            new(0x0305, "Hydralisk Den",   Race.Zerg, UnitCategory.Building, 0xFF204030),
            new(0x0306, "Spire",           Race.Zerg, UnitCategory.Building, 0xFF204030),
            new(0x0307, "Ultralisk Cavern",Race.Zerg, UnitCategory.Building, 0xFF204030),
            new(0x0308, "Infestation Pit", Race.Zerg, UnitCategory.Building, 0xFF204030),
            new(0x0309, "Evolution Chamber",Race.Zerg,UnitCategory.Building, 0xFF204030),
            new(0x030A, "Spine Crawler",   Race.Zerg, UnitCategory.Building, 0xFF204030),
            new(0x030B, "Spore Crawler",   Race.Zerg, UnitCategory.Building, 0xFF204030),
        ];

        // ── Protoss units ─────────────────────────────────────────────────────
        static readonly UnitDef[] _protossUnits =
        [
            new(0x0400, "Probe",           Race.Protoss, UnitCategory.Worker,   0xFFFFD040),
            new(0x0401, "Zealot",          Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x0402, "Adept",           Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x0403, "Stalker",         Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x0404, "Sentry",          Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x0405, "High Templar",    Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x0406, "Dark Templar",    Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x0407, "Archon",          Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x0408, "Immortal",        Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x0409, "Colossus",        Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x040A, "Disruptor",       Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x040B, "Phoenix",         Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x040C, "Oracle",          Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x040D, "Void Ray",        Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x040E, "Carrier",         Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x040F, "Tempest",         Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
            new(0x0410, "Mothership",      Race.Protoss, UnitCategory.Unit,     0xFFFFFF00),
        ];

        static readonly UnitDef[] _protossBuildings =
        [
            new(0x0500, "Nexus",           Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x0501, "Gateway",         Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x0502, "Warp Gate",       Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x0503, "Assimilator",     Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x0504, "Cybernetics Core",Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x0505, "Twilight Council",Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x0506, "Templar Archives",Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x0507, "Dark Shrine",     Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x0508, "Stargate",        Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x0509, "Fleet Beacon",    Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x050A, "Robotics Facility",Race.Protoss,UnitCategory.Building, 0xFF806000),
            new(0x050B, "Robotics Bay",    Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x050C, "Forge",           Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x050D, "Photon Cannon",   Race.Protoss, UnitCategory.Building, 0xFF806000),
            new(0x050E, "Shield Battery",  Race.Protoss, UnitCategory.Building, 0xFF806000),
        ];

        // ── Lookup table ──────────────────────────────────────────────────────
        static readonly Dictionary<int, UnitDef> _byId;

        static UnitDatabase()
        {
            _byId = new();
            foreach (var arr in new[] {
                _terranUnits, _terranBuildings,
                _zergUnits, _zergBuildings,
                _protossUnits, _protossBuildings })
                foreach (var d in arr)
                    _byId[d.TypeId] = d;
        }

        public static UnitDef? Get(int typeId) =>
            _byId.TryGetValue(typeId, out var d) ? d : null;

        // Player-index colors for when unit type is unknown (1-indexed players).
        public static readonly uint[] PlayerColors =
        [
            0xFF606060, // 0  neutral/unknown
            0xFF4040FF, // 1  Red
            0xFFFF4020, // 2  Blue
            0xFF60D060, // 3  Teal
            0xFFD040D0, // 4  Purple
            0xFF20E0E0, // 5  Yellow
            0xFF2080FF, // 6  Orange
            0xFFC080FF, // 7  Pink
            0xFFE0E0E0, // 8  White
        ];

        public static uint ColorForPlayer(byte owner) =>
            owner < PlayerColors.Length ? PlayerColors[owner] : 0xFF606060;
    }
}
