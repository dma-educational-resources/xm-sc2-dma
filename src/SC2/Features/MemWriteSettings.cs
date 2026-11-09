using System.IO;
using System.Text.Json;

namespace SC2Radar.SC2.Features
{
    /// <summary>Settings of the memory-write features (Config.MemWrites). The toggles themselves are not saved: no write
    /// turns itself on at launch.</summary>
    public sealed class MemWriteSettings
    {
        public CameraZoomSettings      CameraZoom  { get; set; } = new();
        public UltrawideSettings       Ultrawide   { get; set; } = new();
        public UnitSightSettings       UnitSight   { get; set; } = new();
        public ShareEnemyVisionSettings EnemyVision { get; set; } = new();
        /// <summary>The per-feature files of older versions were read in once.</summary>
        public bool LegacyFilesMigrated { get; set; }

        /// <summary>Older versions kept each feature's settings in its own file next to the config: read them in once.</summary>
        public bool MigrateLegacyFiles()
        {
            if (LegacyFilesMigrated) return false;
            LegacyFilesMigrated = true;
            CameraZoom  = Legacy<CameraZoomSettings>("zoom.json")         ?? CameraZoom;
            Ultrawide   = Legacy<UltrawideSettings>("ultrawide.json")    ?? Ultrawide;
            UnitSight   = Legacy<UnitSightSettings>("unit_sight.json")   ?? UnitSight;
            EnemyVision = Legacy<ShareEnemyVisionSettings>("enemy_vision.json") ?? EnemyVision;
            return true;
        }

        static TSettings? Legacy<TSettings>(string file) where TSettings : class
        {
            try
            {
                string path = Path.Combine(Program.DataDir, file);
                return File.Exists(path) ? JsonSerializer.Deserialize<TSettings>(File.ReadAllText(path)) : null;
            }
            catch { return null; }
        }
    }

    public sealed class CameraZoomSettings
    {
        public float Distance { get; set; } = 50f;
        public bool  SetPitch { get; set; } = false;
        public float Pitch    { get; set; } = 56f;     // the game's farthest step: 56 degrees
        public bool  SetFov   { get; set; } = false;
        public float Fov      { get; set; } = 27.8f;   // the game's default
    }

    public sealed class UltrawideSettings
    {
        public bool KeepConsole { get; set; } = true;
        public int  TimeoutSec  { get; set; } = 60;
    }

    public sealed class UnitSightSettings
    {
        public float Sight     { get; set; } = 20f;    // the game caps sight at about 32 (measured: no gain past 31-32)
        public bool  Detection { get; set; } = true;
    }

    public sealed class ShareEnemyVisionSettings
    {
        public bool IncludeNeutral { get; set; } = true;
    }
}
