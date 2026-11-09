using System.IO;
using System.IO.Compression;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Rolling copies of the radar's settings files, taken just before each is overwritten: at most one per file per hour,
    /// the newest <see cref="Keep"/> kept, in a "backups" folder next to the file ("config.json" -> "config-20261023-1415.json").
    /// Restoring copies one back over the live file; the radar reads it on its next start.
    /// </summary>
    public static class SettingsBackup
    {
        const int Keep = 15;
        static readonly TimeSpan Every = TimeSpan.FromHours(1);
        static readonly object Lock = new();

        static string Folder(string file) => Path.Combine(Path.GetDirectoryName(file)!, "backups");

        static readonly HashSet<string> Frozen = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Call right before writing <paramref name="file"/>: copies the current one if the last copy is old enough. False
        /// when the file was restored this session - the caller must not write it, or the radar's in-memory settings would
        /// overwrite the restored copy before the restart that loads it.
        /// </summary>
        public static bool BeforeSave(string file)
        {
            lock (Lock) if (Frozen.Contains(file)) return false;
            try
            {
                lock (Lock)
                {
                    if (!File.Exists(file)) return true;
                    string dir = Folder(file), stem = Path.GetFileNameWithoutExtension(file);
                    Directory.CreateDirectory(dir);
                    var copies = List(file);
                    if (copies.Count > 0 && DateTime.Now - File.GetLastWriteTime(copies[0]) < Every) return true;
                    File.Copy(file, Path.Combine(dir, $"{stem}-{DateTime.Now:yyyyMMdd-HHmm}.json"), overwrite: true);
                    foreach (var old in List(file).Skip(Keep)) File.Delete(old);
                }
            }
            catch { }
            return true;
        }

        /// <summary>The copies of a settings file, newest first.</summary>
        public static List<string> List(string file)
        {
            string dir = Folder(file), stem = Path.GetFileNameWithoutExtension(file);
            if (!Directory.Exists(dir)) return [];
            return [.. Directory.GetFiles(dir, $"{stem}-*.json").OrderByDescending(File.GetLastWriteTime)];
        }

        /// <summary>Put a copy back over the live file (the current one is backed up first). False on failure.</summary>
        public static bool Restore(string file, string copy)
        {
            try
            {
                lock (Lock)
                {
                    if (File.Exists(file))
                        File.Copy(file, Path.Combine(Folder(file), $"{Path.GetFileNameWithoutExtension(file)}-before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
                    File.Copy(copy, file, overwrite: true);
                    Frozen.Add(file);
                }
                return true;
            }
            catch { return false; }
        }

        // ── share: every settings file as one zip ───────────────────────────

        public static string ShareFolder => Path.Combine(Program.DataDir, "settings-exports");

        /// <summary>The radar's settings files: every .json in the data folder (settings, profiles, filter, POIs, writes).</summary>
        static string[] SettingsFiles() =>
            Directory.Exists(Program.DataDir) ? Directory.GetFiles(Program.DataDir, "*.json") : [];

        /// <summary>Zips every settings file into the share folder; the zip's path, or null on failure.</summary>
        public static string? ExportAll()
        {
            try
            {
                Directory.CreateDirectory(ShareFolder);
                string zip = Path.Combine(ShareFolder, $"sc2-radar-settings-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                using var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
                lock (Lock)
                    foreach (var f in SettingsFiles())
                        archive.CreateEntryFromFile(f, Path.GetFileName(f));
                return zip;
            }
            catch { return null; }
        }

        /// <summary>The settings zips in the share folder, newest first.</summary>
        public static List<string> ListExports() =>
            Directory.Exists(ShareFolder) ? [.. Directory.GetFiles(ShareFolder, "*.zip").OrderByDescending(File.GetLastWriteTime)] : [];

        /// <summary>
        /// Puts every settings file from a zip back (each through <see cref="Restore"/>: the current one is backed up, and
        /// it isn't overwritten before the restart that loads it). Only plain .json names that parse as JSON are taken.
        /// Returns how many files were restored, and what went wrong if anything.
        /// </summary>
        public static (int Restored, string? Error) ImportAll(string zip)
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"sc2-radar-import-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tmp);
                int n = 0;
                using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
                foreach (var e in archive.Entries)
                {
                    string name = Path.GetFileName(e.FullName);
                    if (name != e.FullName || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;   // no folders, no other files
                    string staged = Path.Combine(tmp, name);
                    e.ExtractToFile(staged, overwrite: true);
                    try { using var _ = JsonDocument.Parse(File.ReadAllText(staged)); } catch { continue; }   // not JSON: skip
                    if (Restore(Path.Combine(Program.DataDir, name), staged)) n++;
                }
                return n > 0 ? (n, null) : (0, "No settings files in that zip.");
            }
            catch (Exception ex) { return (0, ex.Message); }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }
}
