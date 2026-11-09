using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Open.Nat;
using SC2Radar.Misc;
using SkiaSharp;
using SC2Radar.DMA;
using SC2Radar.SC2;
using SC2Radar.UI.ImGui.Radar;
using Timer = System.Threading.Timer;

namespace SC2Radar.Web
{
    /// <summary>
    /// The local web radar, as the EFT radar hosts it: a Kestrel server on this PC that serves the web viewer (wwwroot,
    /// built from web/) and the radar data. A worker thread builds one JSON tick every TickRateMs and every browser's
    /// /api/radar poll reads those same bytes. /api/terrain.png is the radar's terrain picture, built once per map.
    /// UPnP can forward the port on the router for viewers outside the network.
    /// </summary>
    public static class WebRadarServer
    {
        static WebApplication? _app;
        static CancellationTokenSource? _cts;
        static Thread? _worker;
        static int _upnpPort = -1;
        static Timer? _upnpRenewalTimer;

        static volatile bool _isRunning;
        public static bool IsRunning => _isRunning;
        /// <summary>The port the running server listens on.</summary>
        public static int Port { get; private set; }
        /// <summary>The last start failure (port in use, ...), null after a good start.</summary>
        public static string? LastError { get; private set; }

        // built once per tick, read by every HTTP client; swapped atomically
        static volatile byte[] _latestJson = "{\"v\":0,\"inGame\":false}"u8.ToArray();
        static int _latestVersion;

        public static async Task StartAsync(int port, int tickRateMs, bool enableUpnp, bool openBrowser)
        {
            await StopAsync();
            try
            {
                ThrowIfInvalidPort(port);

                if (enableUpnp)
                {
                    bool ok = await TryConfigureUPnPAsync(port);
                    XMLogging.WriteLine(ok
                        ? $"[WebRadar] UPnP port mapped: TCP {port}"
                        : "[WebRadar] UPnP failed (router may not support it / CGNAT). Continuing without UPnP.");
                    // renew an hour before the 24-hour lease runs out
                    if (ok) _upnpRenewalTimer = new Timer(async _ => await TryConfigureUPnPAsync(port), null, TimeSpan.FromHours(23), TimeSpan.FromHours(23));
                }

                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
                builder.Logging.ClearProviders();
                builder.WebHost.UseKestrel(o =>
                {
                    o.Listen(IPAddress.Any, port);
                    // keep connections alive across polls: no handshake per poll, no "disconnected" flashes
                    o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(3);
                    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
                    o.Limits.MaxConcurrentConnections = 100;
                });
                builder.Services.AddResponseCompression(o =>
                {
                    o.EnableForHttps = false;
                    o.Providers.Add<GzipCompressionProvider>();
                    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
                });
                builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

                var app = builder.Build();
                app.UseResponseCompression();
                // the viewer's dev server (vite, another origin) polls this too
                app.Use(async (ctx, next) => { ctx.Response.Headers["Access-Control-Allow-Origin"] = "*"; await next(); });

                string root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                if (Directory.Exists(root))
                {
                    var files = new PhysicalFileProvider(root);
                    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
                    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
                }
                else
                    XMLogging.WriteLine($"[WebRadar] {root} is missing - only the /api routes will answer.");

                app.MapGet("/api/radar", async ctx =>
                {
                    var bytes = _latestJson;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    await ctx.Response.Body.WriteAsync(bytes);
                });
                app.MapGet("/api/terrain.png", async ctx =>
                {
                    var png = await TerrainPngAsync();
                    if (png == null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
                    ctx.Response.ContentType = "image/png";
                    // the page asks for it with the terrain key in the query, so a new map is a new URL
                    ctx.Response.Headers["Cache-Control"] = "public, max-age=86400";
                    await ctx.Response.Body.WriteAsync(png);
                });
                app.MapGet("/health", () => "OK");

                await app.StartAsync();
                _app = app;
                Port = port;
                LastError = null;
                _isRunning = true;
                StartWorker(tickRateMs);
                if (openBrowser) Process.Start(new ProcessStartInfo { FileName = $"http://localhost:{port}/", UseShellExecute = true });
                XMLogging.WriteLine($"[WebRadar] HTTP server running on port {port}");
            }
            catch (Exception ex)
            {
                LastError = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } ? $"Port {port} is already in use." : ex.Message;
                XMLogging.WriteLine($"[WebRadar] Start failed: {ex}");
                await StopAsync();
                LastError ??= "Start failed.";
                throw;
            }
        }

        public static async Task StopAsync()
        {
            _isRunning = false;
            _cts?.Cancel();
            _worker?.Join(2000);
            _cts = null;
            _worker = null;

            _upnpRenewalTimer?.Dispose();
            _upnpRenewalTimer = null;

            if (_app != null)
            {
                try { await _app.StopAsync(); await _app.DisposeAsync(); } catch { }
                _app = null;
            }
            if (_upnpPort > 0)
            {
                await CleanupUPnPAsync(_upnpPort);
                _upnpPort = -1;
            }
        }

        /// <summary>Start with the saved settings (Settings ▸ Web Radar); a failure shows a toast.</summary>
        public static void Start(bool openBrowser = false)
        {
            var cfg = Program.Config.WebRadar;
            _ = Task.Run(async () =>
            {
                try { await StartAsync(cfg.Port, cfg.TickRateMs, cfg.UPnP, openBrowser); }
                catch { UI.ImGui.ToastManager.Show("Web Radar", LastError ?? "Start failed.", UI.ImGui.ToastStyle.Danger, 6f); }
            });
        }

        public static void Stop() => _ = StopAsync();

        // ── Worker ────────────────────────────────────────────────────────────

        static void StartWorker(int tickRateMs)
        {
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _worker = new Thread(() => Worker(ct)) { IsBackground = true, Name = "WebRadarWorker" };
            _worker.Start();
        }

        static void Worker(CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(64 * 1024);
            using var writer = new Utf8JsonWriter(buffer);
            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    buffer.ResetWrittenCount();
                    writer.Reset(buffer);
                    WriteTick(writer, ++_latestVersion);
                    writer.Flush();
                    _latestJson = buffer.WrittenSpan.ToArray();
                }
                catch (Exception ex)
                {
                    XMLogging.WriteLine($"[WebRadar] Worker error: {ex.Message}");
                }
                // the setting applies live
                int wait = Math.Clamp(Program.Config.WebRadar.TickRateMs, 25, 2000) - (int)sw.ElapsedMilliseconds;
                if (wait > 0) ct.WaitHandle.WaitOne(wait);
            }
        }

        /// <summary>RGB as a 0xRRGGBB number from an ImGui ABGR colour.</summary>
        static int Rgb(uint abgr) => (int)(((abgr & 0xFF) << 16) | (abgr & 0xFF00) | ((abgr >> 16) & 0xFF));

        /// <summary>
        /// One tick. Units are arrays to keep the payload small - [x, y, rgb, owner, category, name index, hp 0..1 (-1
        /// unknown), shields 0..1 (-1 none), flags, progress 0..1 (-1 none), id] - with the names in their own table. Flags: 1
        /// burrowed, 2 cloaked, 4 detector, 8 warping in, 16 building.
        /// </summary>
        static void WriteTick(Utf8JsonWriter w, int version)
        {
            var cfg = Program.Config.WebRadar;
            var mem = Memory.Instance;
            w.WriteStartObject();
            w.WriteNumber("v", version);
            w.WriteNumber("tickMs", cfg.TickRateMs);

            string status = mem == null ? "No DMA connection (safe mode)"
                          : !Memory.IsConnected ? "Connecting to the game PC..."
                          : !Memory.InGame ? "Waiting for StarCraft II..."
                          : mem.Snapshot.Units.Length == 0 ? "Waiting for a match..." : "";
            bool inGame = mem != null && status.Length == 0;
            w.WriteBoolean("inGame", inGame);
            w.WriteString("status", status);
            if (!inGame) { w.WriteEndObject(); return; }

            var units = mem!.Snapshot.Units;
            int local = mem.LocalPlayer;
            w.WriteNumber("time", MathF.Round(mem.GameTime, 1));
            w.WriteNumber("local", local);
            w.WriteBoolean("allianceColors", Program.Config.AllianceColors);   // the desktop radar's mode, the page's default

            // map
            w.WriteStartObject("map");
            w.WriteString("name", mem.MapName);
            if (mem.Terrain is { } t) { w.WriteNumber("w", t.W); w.WriteNumber("h", t.H); }
            if (mem.Pathing?.Playable is { } pb && pb.Max.X > pb.Min.X)
            {
                w.WriteStartArray("playable");
                w.WriteNumberValue(pb.Min.X); w.WriteNumberValue(pb.Min.Y); w.WriteNumberValue(pb.Max.X); w.WriteNumberValue(pb.Max.Y);
                w.WriteEndArray();
            }
            if (TerrainLayer.CurrentKey() is { } key) w.WriteString("terrain", key);
            w.WriteEndObject();

            // per player: units and army from the snapshot (a unit drawn with several actors counts once)
            Span<int> army = stackalloc int[16], workers = stackalloc int[16], value = stackalloc int[16], seenOwner = stackalloc int[16];
            var seen = new HashSet<ulong>();
            foreach (ref readonly var u in units.AsSpan())
            {
                if (u.Owner is 0 or >= 16 || u.Record == 0 || !seen.Add(u.Record)) continue;
                seenOwner[u.Owner] = 1;
                if (u.Category == UnitCategory.Worker) workers[u.Owner]++;
                else if (u.Category == UnitCategory.Unit) { army[u.Owner]++; value[u.Owner] += u.Minerals + u.Gas; }
            }

            var size = SC2.Features.MemoryWrites.Ultrawide.DisplayMonitor.GameSize;
            float aspect = size.W > 0 && size.H > 0 ? (float)size.W / size.H : 16f / 9f;
            (Vector2, Vector2)? playable = mem.Pathing?.Playable is { } pa && pa.Max.X > pa.Min.X ? pa : null;
            float console = Program.Config.CameraConsoleHeight / 100f;

            w.WriteStartArray("players");
            for (int p = 1; p < 16; p++)
            {
                string name = mem.PlayerDisplayName(p);
                if (name.Length == 0 && seenOwner[p] == 0) continue;
                w.WriteStartObject();
                w.WriteNumber("id", p);
                w.WriteString("name", name.Length > 0 ? name : $"Player {p}");
                w.WriteBoolean("human", mem.PlayerName(p).Length > 0);
                w.WriteNumber("color", Rgb(VisionOverlay.PlayerColor(p, alliance: false)));   // the page applies alliance colours itself, from "team"
                w.WriteString("team", p == local ? "self" : local > 0 && mem.HasAllianceTable ? (mem.IsAlly(p, local) ? "ally" : "enemy") : "");
                w.WriteNumber("army", army[p]);
                w.WriteNumber("workers", workers[p]);
                w.WriteNumber("armyValue", value[p]);
                if (cfg.SendStats && mem.GetPlayerStats(p) is { } s)
                {
                    w.WriteNumber("minerals", s.Minerals);
                    w.WriteNumber("gas", s.Gas);
                    w.WriteNumber("supply", MathF.Round(s.SupplyUsed, 1));
                    w.WriteNumber("supplyCap", MathF.Round(s.SupplyCap, 1));
                }
                if (cfg.SendCameras)
                {
                    Vector2[]? area = null;
                    if (p == local && mem.LocalCameraView is { } v)
                        area = RadarWindow.CameraFootprint(v.Target, v.Distance, v.Pitch, v.Fov, aspect, console);
                    else if (mem.CameraTarget(p) is { } target)
                        area = RadarWindow.CameraFootprint(target, mem.CameraDistance(p), 56f, 27.8f, aspect, console);
                    if (area != null && playable is var (min, max)) area = RadarWindow.ClipToRect(area, min, max);
                    if (area is { Length: >= 3 })
                    {
                        w.WriteStartArray("camera");
                        foreach (var c in area) { w.WriteNumberValue(MathF.Round(c.X, 2)); w.WriteNumberValue(MathF.Round(c.Y, 2)); }
                        w.WriteEndArray();
                    }
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // units
            var names = new Dictionary<string, int>();
            seen.Clear();
            w.WriteStartArray("units");
            bool cargo = Program.Config.TrackCargo;
            foreach (ref readonly var u in units.AsSpan())
            {
                bool neutral = u.Owner is 0 or >= 16 || u.Record == 0;
                if (neutral ? !cfg.SendNeutral : !cfg.SendUnits) continue;
                if (u.Record != 0 && !seen.Add(u.Record)) continue;   // one marker per unit
                if (cargo && CargoInfo.IsCarried(u.Record)) continue;  // inside a transport
                string display = u.DisplayName;
                if (!names.TryGetValue(display, out int ni)) names[display] = ni = names.Count;
                int flags = (u.Burrowed ? 1 : 0) | (u.Cloaked ? 2 : 0) | (u.Detector ? 4 : 0) | (u.WarpingIn ? 8 : 0) | (u.IsBuilding ? 16 : 0);
                float progress = u.Progress >= 0f ? u.Progress : u.QueueProgress;
                w.WriteStartArray();
                w.WriteNumberValue(MathF.Round(u.X, 2));
                w.WriteNumberValue(MathF.Round(u.Y, 2));
                w.WriteNumberValue(Rgb(PlayerColors.For(in u, alliance: false)));
                w.WriteNumberValue(neutral ? 0 : u.Owner);
                w.WriteNumberValue((int)u.Category);
                w.WriteNumberValue(ni);
                w.WriteNumberValue(MathF.Round(u.HpFrac, 2));
                w.WriteNumberValue(MathF.Round(u.ShieldFrac, 2));
                w.WriteNumberValue(flags);
                w.WriteNumberValue(MathF.Round(progress, 2));
                w.WriteNumberValue(u.Record != 0 ? u.Record : u.Addr);   // identity across ticks (below 2^53: exact in JS)
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteStartArray("names");
            foreach (var n in names.OrderBy(kv => kv.Value)) w.WriteStringValue(n.Key);
            w.WriteEndArray();

            // live events: the newest commands, as the Live Events widget words them
            if (Program.Config.TrackEvents && mem.EventFeedStatus.StartsWith("Live"))
            {
                var byRecord = new Dictionary<ulong, string>();
                foreach (ref readonly var u in units.AsSpan()) if (u.Record != 0) byRecord.TryAdd(u.Record, u.DisplayName);
                var tags = mem.TagRecords;
                string UnitName(uint tag) => tags.TryGetValue(tag, out var r) && byRecord.TryGetValue(r, out var n) ? n : "a unit";
                var now = DateTime.UtcNow;
                w.WriteStartArray("events");
                foreach (var c in mem.RecentCommands.Reverse().Where(c => c.Ability != "smart").Take(60))
                {
                    string what = EventsWidget.PrettyAbility(c.Ability, c.CmdIndex);
                    string target = c.TargetTag != 0 ? $" on {UnitName(c.TargetTag)}" : c.Point is { } pt ? $" at ({pt.X:0}, {pt.Y:0})" : "";
                    w.WriteStartObject();
                    w.WriteNumber("ago", Math.Round((now - c.Time).TotalSeconds, 1));
                    w.WriteNumber("p", c.Player);
                    w.WriteString("text", what + target + (c.Queued ? " (queued)" : ""));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        // ── Terrain picture ───────────────────────────────────────────────────

        static readonly SemaphoreSlim _terrainLock = new(1, 1);
        static (string Key, byte[] Png)? _terrainPng;

        /// <summary>The terrain picture the radar draws, as PNG; built once per terrain key.</summary>
        static async Task<byte[]?> TerrainPngAsync()
        {
            string? key = TerrainLayer.CurrentKey();
            if (key == null) return null;
            if (_terrainPng is { } c && c.Key == key) return c.Png;
            await _terrainLock.WaitAsync();
            try
            {
                if (_terrainPng is { } c2 && c2.Key == key) return c2.Png;
                var built = await Task.Run(TerrainLayer.BuildCurrent);
                if (built is not { } b) return null;
                var px = b.Pixels;
                using var bmp = new SKBitmap(new SKImageInfo(px.W, px.H, SKColorType.Rgba8888, SKAlphaType.Opaque));
                Marshal.Copy(px.Data, 0, bmp.GetPixels(), px.Data.Length);
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                var png = data.ToArray();
                _terrainPng = (b.Key, png);
                return png;
            }
            finally { _terrainLock.Release(); }
        }

        // ── UPnP ─────────────────────────────────────────────────────────────

        static async Task<NatDevice?> TryDiscoverNatAsync()
        {
            // UPnP first, then NAT-PMP
            foreach (var proto in new[] { PortMapper.Upnp, PortMapper.Pmp })
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    return await new NatDiscoverer().DiscoverDeviceAsync(proto, cts);
                }
                catch { /* try the next */ }
            }
            return null;
        }

        static async Task<bool> TryConfigureUPnPAsync(int port)
        {
            try
            {
                var nat = await TryDiscoverNatAsync();
                if (nat == null) return false;
                await nat.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port, 86400, "XM SC2 WebRadar"));
                _upnpPort = port;
                return true;
            }
            catch (Exception ex)
            {
                XMLogging.WriteLine($"[WebRadar] UPnP map error: {ex.Message}");
                return false;
            }
        }

        static async Task CleanupUPnPAsync(int port)
        {
            try
            {
                var nat = await TryDiscoverNatAsync();
                if (nat != null) await nat.DeletePortMapAsync(new Mapping(Protocol.Tcp, port, port));
            }
            catch { /* best effort */ }
        }

        // ── Network helpers ───────────────────────────────────────────────────

        static void ThrowIfInvalidPort(int port)
        {
            if (port is < 1024 or > 65535) throw new ArgumentException("The port must be between 1024 and 65535.");
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Bind(new IPEndPoint(IPAddress.Any, port));   // in use: throws AddressAlreadyInUse
        }

        static string? _externalIp;
        /// <summary>This network's public address (UPnP, else a what's-my-IP service), cached; null until known.</summary>
        public static string? ExternalIP
        {
            get
            {
                if (_externalIp == null && !_externalIpPending)
                {
                    _externalIpPending = true;
                    _ = Task.Run(async () => { try { _externalIp = await GetExternalIPAsync(); } catch { } });
                }
                return _externalIp;
            }
        }
        static volatile bool _externalIpPending;

        static async Task<string> GetExternalIPAsync()
        {
            try
            {
                var nat = await TryDiscoverNatAsync();
                if (nat != null && await nat.GetExternalIPAsync() is { } ip) return ip.ToString();
            }
            catch { }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            foreach (var svc in new[] { "https://api.ipify.org", "https://icanhazip.com", "https://ifconfig.me/ip" })
            {
                try
                {
                    var raw = (await http.GetStringAsync(svc)).Trim();
                    if (IPAddress.TryParse(raw, out _)) return raw;
                }
                catch { }
            }
            throw new Exception("Failed to obtain the external IP address from any source.");
        }

        static string? _localIp;
        /// <summary>This PC's address on the local network (a private range when there is one).</summary>
        public static string? LocalIP => _localIp ??= GetLocalIPAddress();

        static string? GetLocalIPAddress()
        {
            try
            {
                var addrs = Dns.GetHostEntry(Dns.GetHostName()).AddressList
                               .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)).ToArray();
                foreach (var ip in addrs)
                {
                    var b = ip.GetAddressBytes();
                    if (b[0] == 192 && b[1] == 168 || b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31) return ip.ToString();
                }
                return addrs.FirstOrDefault()?.ToString();
            }
            catch { return null; }
        }
    }
}
