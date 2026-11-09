global using SC2Radar;
global using SC2Radar.Common;
global using SC2Radar.Common.Misc;
global using SC2Radar.Misc;
global using System.Reflection;
global using SC2Radar.Common.Misc.Config;
global using System.Buffers;
global using System.Buffers.Binary;
global using System.Collections.Concurrent;
global using System.Diagnostics;
global using System.Numerics;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Serialization;

using System.IO;
using System.Runtime.Versioning;
using SC2Radar.SC2;
using SC2Radar.Misc;
using SC2Radar.UI.ImGui;
using SC2Radar.Common.UI;

[assembly: AssemblyTitle(Program.Name)]
[assembly: AssemblyProduct(Program.Name)]
[assembly: AssemblyCompany("xm-dma")]
[assembly: AssemblyDescription("SC2 DMA Radar")]
[assembly: SupportedOSPlatform("Windows")]

namespace SC2Radar
{
    internal static class Program
    {
        internal const string Name       = "SC2 DMA Radar";
        internal const string Version    = "1.0.0";
        internal const string SoftwareId = "sc2_dma_radar";

        public static string DataDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "xmLauncher", "software_settings", SoftwareId);

        public static DirectoryInfo LogsPath { get; } = new DirectoryInfo(
            Path.Combine(DataDir, "logs"));

        public static SC2.Config Config { get; internal set; } = new();

        [STAThread]
        static void Main()
        {
            // Load config early so ShowDebugConsole / LogToFile are available before the splash
            try { Config = SC2.Config.Load(); }
            catch (Exception ex) { XMLogging.WriteLine($"[Program] Config load failed, using defaults: {ex.Message}"); }

            // Restore console and file-logging state before any other logs fire
            if (Config.ShowDebugConsole) XMLogging.SetConsoleVisible(true);
            if (Config.LogToFile)        XMLogging.SetFileLoggingEnabled(true);

            var splash = LoadingWindow.Create(visible: !Config.ShowDebugConsole);
            bool started = false;
            try
            {
                SharedProgram.Initialize();
                splash.UpdateStatus("Connecting to DMA device...", 10);
                Memory.Start(Config);   // DMA initialization failed: Safe Mode instead of a fatal error
                if (Memory.SafeMode)
                {
                    XMLogging.WriteLine("Starting application in Safe Mode - DMA functionality disabled");
                    _pendingDmaInitFailureMessage = Memory.ConnectError;
                }
                splash.UpdateStatus(Memory.SafeMode ? "Safe Mode Ready" : "Loading Completed!", 100);
                Thread.Sleep(300);
                started = true;
            }
            catch (Exception ex)
            {
                // any other startup error: offer Safe Mode (as the EFT radar does)
                XMLogging.WriteLine($"[StartupException] {ex}");
                splash.Close();
                var answer = System.Windows.Forms.MessageBox.Show(
                    $"Startup error: {ex.Message}\n\nWould you like to start in Safe Mode?", "Startup Error",
                    System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Error);
                if (answer == System.Windows.Forms.DialogResult.Yes)
                {
                    XMLogging.WriteLine("Starting application in Safe Mode - DMA functionality disabled");
                    Memory.StartSafeMode(Config);
                    started = true;
                }
            }
            splash.Close();
            if (!started) return;   // no save: an aborted startup mustn't overwrite the settings file
            if (Config.WebRadar.AutoStart) Web.WebRadarServer.Start();

            try
            {
                ImGuiApp.Run();
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    ex.Message, "SC2 Radar - Fatal Error",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
            finally
            {
                if (Web.WebRadarServer.IsRunning) Web.WebRadarServer.StopAsync().Wait(3000);
                Memory.Stop();
                Config.Save();
            }
        }

        /// <summary>Set when startup fell back to Safe Mode because DMA initialization failed; consumed once by
        /// <see cref="PollDmaInitFailureModal"/>.</summary>
        static string? _pendingDmaInitFailureMessage;

        /// <summary>
        /// Called each frame before the modals draw: once after a startup that fell back to Safe Mode because DMA
        /// initialization failed, says why and offers a retry (the Safe Mode badge in the top bar retries too, any time).
        /// </summary>
        internal static void PollDmaInitFailureModal()
        {
            if (_pendingDmaInitFailureMessage == null || ConfirmModal.IsOpen) return;
            var reason = _pendingDmaInitFailureMessage;
            _pendingDmaInitFailureMessage = null;
            ConfirmModal.Open(
                "DMA Initialization Failed",
                "Started in Safe Mode because DMA initialization failed:\n\n" +
                $"{reason}\n\n" +
                "Retry now?",
                onConfirm:             Memory.RetryConnect,
                confirmLabel:          "Retry",
                confirmStyle:          ButtonStyle.Accent,
                cancelLabel:           "Not Now",
                requireExplicitChoice: true);
        }
    }
}
