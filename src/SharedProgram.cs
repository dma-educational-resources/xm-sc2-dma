using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using SC2Radar.Common.Misc.Config;
using SC2Radar.Common.Misc;
using SC2Radar.Misc;

namespace SC2Radar
{
    public static class SharedProgram
    {
        private const string _mutexID = "o6qcdAUExTV1p1VCTQB2sl4XJW004kbq7YP4OzBs934=";
#pragma warning disable IDE0052
        private static Mutex _mutex = null!;
#pragma warning restore IDE0052

        public static HttpClient HttpClient { get; private set; } = null!;

        public static SC2.Config Config => Program.Config;

        public static void Initialize()
        {
            Memory.DisconnectOtherVmmConnections();

            _mutex = new Mutex(true, _mutexID, out bool singleton);
            if (!singleton)
                throw new ApplicationException("SC2 Radar is already running!");

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Program.Config.Save();
            SetHighPerformanceMode();
            SetupHttpClient();
        }

        static void SetupHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect       = true,
                SslProtocols            = System.Security.Authentication.SslProtocols.Tls12 |
                                          System.Security.Authentication.SslProtocols.Tls13,
                AutomaticDecompression  = DecompressionMethods.Deflate | DecompressionMethods.GZip,
            };
            var c = new HttpClient(handler);
            c.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            c.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            c.DefaultRequestHeaders.Add("User-Agent", "xmRadar/1.0");
            HttpClient = c;
        }

        static void SetHighPerformanceMode()
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            Native.SetThreadExecutionState(
                Native.EXECUTION_STATE.ES_CONTINUOUS |
                Native.EXECUTION_STATE.ES_SYSTEM_REQUIRED |
                Native.EXECUTION_STATE.ES_DISPLAY_REQUIRED);
            var guid = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
            Native.PowerSetActiveScheme(IntPtr.Zero, ref guid);
        }
    }
}
