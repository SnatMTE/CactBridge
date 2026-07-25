using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PuppeteerSharp;

namespace CactBridge.Services;

/// <summary>
/// Manages an embedded headless Chromium browser that loads the Cactbot
/// raidboss overlay in the background. Supports a primary (alerts) page
/// and an optional secondary (timeline) page sharing the same browser process.
///
/// On first use, Chromium is downloaded once to <c>%APPDATA%/CactBridge/chromium/</c>
/// (~150 MB).  Subsequent starts reuse the cached copy.  The persistent
/// location survives Dalamud plugin updates that wipe the plugin directory.
/// No external browser installation is required - works on Windows and Steam
/// Deck (Proton/Wine).
/// </summary>
public sealed class BrowserService : IDisposable
{
    // -----------------------------------------------------------------------
    // State machine
    // -----------------------------------------------------------------------
    public enum BrowserState { Idle, Downloading, Launching, Running, Error }

    /// <summary>Fires whenever the browser state changes.</summary>
    public event Action<BrowserState>? StateChanged;

    private BrowserState state = BrowserState.Idle;

    public BrowserState State
    {
        get => state;
        private set
        {
            state = value;
            StateChanged?.Invoke(value);
        }
    }
    public int          DownloadPct    { get; private set; }   // 0-100 while downloading (not available in this version)
    public bool         IsRunning      => State == BrowserState.Running;

    public string Status => State switch
    {
        BrowserState.Idle        => "Idle",
        BrowserState.Downloading => $"Downloading Chromium… {DownloadPct}%",
        BrowserState.Launching   => "Launching…",
        BrowserState.Running     => "Running",
        BrowserState.Error       => "Error - check /xllog",
        _                        => "Unknown",
    };

    /// <summary>Status string for the timeline page specifically.</summary>
    public string TimelineStatus { get; private set; } = "Idle";

    /// <summary>
    /// Returns <c>true</c> if the Chromium browser engine has already been
    /// downloaded to the persistent cache directory.  Used to avoid showing
    /// a misleading "Installing: Browser…" notification on subsequent runs.
    /// </summary>
    public bool IsChromiumDownloaded
    {
        get
        {
            try
            {
                var dir = new DirectoryInfo(chromiumPath);
                return dir.Exists && dir.EnumerateFileSystemEntries().Any();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Fired when a page (alerts or timeline) broadcasts data via the native
    /// bridge (bypasses OverlayPlugin WebSocket).  The argument is a JSON
    /// string with shape <c>{ type, text, time?, ... }</c>.
    /// </summary>
    public Action<string>? OnPageBroadcast { get; set; }

    // -----------------------------------------------------------------------
    // Private state
    // -----------------------------------------------------------------------
    private readonly IPluginLog log;
    private readonly string     overlayUrl;
    private readonly string     timelineUrl;
    private readonly string     chromiumPath;
    private IBrowser?           browser;
    private IPage?              alertsPage;
    private IPage?              timelinePage;
    private CancellationTokenSource cts = new();
    private bool                disposed;

    // Pending zone-change data – used to replay the latest zone to a page
    // that hasn't finished initialising yet (the 8‑second callOverlayHandler
    // timeout means Cactbot subscribers aren't ready immediately).
    private int    pendingZoneId;
    private string pendingZoneName = string.Empty;
    private bool   pendingZoneForwarded;

    public BrowserService(IPluginLog log, string pluginDirectory, string overlayUrl, string timelineUrl)
    {
        this.log          = log;
        this.overlayUrl   = overlayUrl;
        this.timelineUrl  = timelineUrl;
        // Store Chromium in %APPDATA%/CactBridge so it survives plugin updates
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        this.chromiumPath = Path.Combine(appData, "CactBridge", "chromium");
        _ = Task.Run(StartAsync);
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public void Restart()
    {
        cts.Cancel();
        cts = new CancellationTokenSource();
        _ = StopBrowserAsync().ContinueWith(_ => StartAsync());
    }

    /// <summary>
    /// Dispatch an OverlayPlugin-style event into the given page via
    /// <c>window.dispatchOverlayEvent</c>.  Used to forward events like
    /// <c>ChangeZone</c> that Cactbot needs to load timelines.
    /// </summary>
    private static Task ForwardEventToPage(IPage page, string json)
    {
        return page.EvaluateFunctionAsync(@"(json) => {
            try {
                var ev = window.dispatchOverlayEvent;
                if (typeof ev !== 'function') {
                    console.warn('CactBridge: dispatchOverlayEvent not ready');
                    return;
                }
                ev(JSON.parse(json));
            } catch(e) {
                console.error('CactBridge event forward error:', e);
            }
        }", json);
    }

    /// <summary>
    /// Forward a raw ACT log line into the timeline page's Cactbot event system
    /// via <c>window.dispatchOverlayEvent</c>.  This bypasses the headless
    /// browser's own WebSocket connection to OverlayPlugin, ensuring the
    /// timeline controller always receives log data.
    /// </summary>
    public void ForwardLogLine(string rawLine)
    {
        var page = timelinePage;
        if (page == null || page.IsClosed || State != BrowserState.Running)
            return;

        try
        {
            // Split rawLine by | for Cactbot's "line" array (it splits on |).
            var parts = rawLine.Split('|');
            var partsJson = string.Join(",", parts.Select(p => JsonEncode(p)));

            _ = ForwardEventToPage(page,
                    $"{{\"type\":\"LogLine\",\"line\":[{partsJson}],\"rawLine\":{JsonEncode(rawLine)}}}")
                .ContinueWith(t =>
                {
                    if (!t.IsFaulted) return;
                    log.Verbose($"[CactBridge] Log forward failed: {t.Exception?.InnerException?.Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch
        {
            // fire-and-forget
        }
    }

    /// <summary>
    /// Forward a <c>ChangeZone</c> event to the timeline page so Cactbot
    /// loads the correct timeline data for the current zone.  Also forwards
    /// to the alerts page so both overlays know the current zone.
    ///
    /// The zone is saved as "pending" in case Cactbot hasn't finished its
    /// 8‑second callOverlayHandler timeout yet.  When the page signals
    /// readiness it will replay the latest zone.
    /// </summary>
    public void ForwardChangeZone(int zoneId, string zoneName)
    {
        lock (this)
        {
            pendingZoneId = zoneId;
            pendingZoneName = zoneName ?? string.Empty;
            pendingZoneForwarded = false;
        }

        if (State != BrowserState.Running)
        {
            // Not launched yet – will be forwarded when the page is ready.
            return;
        }

        var json = $"{{\"type\":\"ChangeZone\",\"zoneID\":{zoneId},\"zoneName\":{JsonEncode(zoneName)}}}";
        DispatchZoneChange(json, immediate: true);
    }

    /// <summary>
    /// Internal helper that dispatches a ChangeZone JSON to both pages and
    /// optionally schedules a retry ~12 s later (after Cactbot's timeout).
    /// </summary>
    private void DispatchZoneChange(string json, bool immediate)
    {
        var sends = 0;
        if (alertsPage is { IsClosed: false })
        {
            _ = ForwardEventToPage(alertsPage, json)
                .ContinueWith(t => { }, TaskContinuationOptions.OnlyOnFaulted);
            sends++;
        }
        if (timelinePage is { IsClosed: false })
        {
            _ = ForwardEventToPage(timelinePage, json)
                .ContinueWith(t => { }, TaskContinuationOptions.OnlyOnFaulted);
            sends++;
        }

        if (immediate && sends > 0)
        {
            // Cactbot's callOverlayHandler patch waits 8 s before timing out.
            // Schedule another dispatch after ~14 s so the ChangeZone event
            // reaches the subscribers that were registered in the meantime.
            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(14_000, token);
                    if (token.IsCancellationRequested) return;
                    // Only re-dispatch if the zone hasn't been updated since.
                    lock (this)
                    {
                        if (pendingZoneForwarded) return;
                        pendingZoneForwarded = true;
                    }
                    var retryJson = json;
                    if (alertsPage is { IsClosed: false })
                        _ = ForwardEventToPage(alertsPage, retryJson);
                    if (timelinePage is { IsClosed: false })
                        _ = ForwardEventToPage(timelinePage, retryJson);
                }
                catch (OperationCanceledException) { /* normal */ }
            }, token);
        }
    }

    /// <summary>Simple JSON string encoder (avoids adding a dependency).</summary>
    private static string JsonEncode(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    }

    /// <summary>Restart only the timeline page (navigate to the timeline URL).</summary>
    public void RestartTimeline()
    {
        var page = timelinePage;
        if (page != null && !page.IsClosed)
            _ = page.GoToAsync(timelineUrl).ContinueWith(t =>
            {
                if (t.Exception != null)
                    log.Warning($"[CactBridge] Failed to reload timeline page: {t.Exception.Message}");
            });
    }

    // -----------------------------------------------------------------------
    // Internal lifecycle
    // -----------------------------------------------------------------------

    private async Task StartAsync()
    {
        var ct = cts.Token;
        try
        {
            // ------------------------------------------------------------------
            // 1. Ensure Chromium is present (downloads ~150 MB on first run)
            // ------------------------------------------------------------------
            State = BrowserState.Downloading;
            log.Information("[CactBridge] Checking embedded Chromium…");

            var fetcher = new BrowserFetcher(new BrowserFetcherOptions
            {
                Path = chromiumPath,
                // Stream the download manually so we can track progress
                CustomFileDownload = async (url, filePath) =>
                {
                    using var http = new HttpClient();
                    http.Timeout = TimeSpan.FromMinutes(20);
                    using var response = await http.GetAsync(
                        url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength ?? -1L;
                    using var downloadStream = await response.Content.ReadAsStreamAsync(ct);
                    using var fileStream     = File.Create(filePath);

                    var buf  = new byte[65536];
                    long done = 0;
                    int  read;
                    while ((read = await downloadStream.ReadAsync(buf, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buf.AsMemory(0, read), ct);
                        done += read;
                        if (total > 0)
                            DownloadPct = (int)(done * 100L / total);
                    }
                },
            });

            log.Information("[CactBridge] Ensuring Chromium is present (~150 MB, downloaded once)…");
            var revisionInfo = await fetcher.DownloadAsync();
            var executablePath = revisionInfo.GetExecutablePath();
            log.Information($"[CactBridge] Chromium ready: {executablePath}");

            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // 2. Launch headless browser
            // ------------------------------------------------------------------
            State = BrowserState.Launching;

            browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless         = true,
                ExecutablePath   = executablePath,
                Args             = new[]
                {
                    "--no-sandbox",
                    "--disable-gpu",
                    "--disable-dev-shm-usage",
                    "--log-level=3",
                },
            });

            if (ct.IsCancellationRequested) { await StopBrowserAsync(); return; }

            // ------------------------------------------------------------------
            // 3. Navigate to Cactbot overlay (alerts page)
            // ------------------------------------------------------------------
            alertsPage = await browser.NewPageAsync();
            alertsPage.Console += (_, msg) =>
                log.Debug($"[CactBridge] [alerts] {msg.Message.Text}");
            await alertsPage.GoToAsync(overlayUrl);
            log.Information($"[CactBridge] Alerts page → {overlayUrl}");

            // Expose a native bridge so the relay JS can send data back to us
            // without needing the OverlayPlugin WebSocket.
            await alertsPage.ExposeFunctionAsync("__cactbridgeBroadcast", (string jsonData) =>
            {
                OnPageBroadcast?.Invoke(jsonData);
                return Task.CompletedTask;
            });

            if (ct.IsCancellationRequested) { await StopBrowserAsync(); return; }

            // ------------------------------------------------------------------
            // 4. Navigate to Cactbot overlay (timeline page)
            // ------------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(timelineUrl))
            {
                TimelineStatus = "Launching…";
                timelinePage = await browser.NewPageAsync();
                timelinePage.Console += (_, msg) =>
                    log.Debug($"[CactBridge] [timeline] {msg.Message.Text}");
                await timelinePage.GoToAsync(timelineUrl);
                TimelineStatus = "Running";
                log.Information($"[CactBridge] Timeline page → {timelineUrl}");

                await timelinePage.ExposeFunctionAsync("__cactbridgeBroadcast", (string jsonData) =>
                {
                    OnPageBroadcast?.Invoke(jsonData);
                    return Task.CompletedTask;
                });
            }
            else
            {
                TimelineStatus = "Not configured";
            }

            State = BrowserState.Running;

            // Forward any zone change that arrived while the browser was
            // still starting up, and schedule a retry after the Cactbot
            // callOverlayHandler timeout would have elapsed.
            lock (this)
            {
                if (!string.IsNullOrEmpty(pendingZoneName) && !pendingZoneForwarded)
                {
                    var json = $"{{\"type\":\"ChangeZone\",\"zoneID\":{pendingZoneId},\"zoneName\":{JsonEncode(pendingZoneName)}}}";
                    DispatchZoneChange(json, immediate: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            State = BrowserState.Idle;
            TimelineStatus = "Idle";
        }
        catch (Exception ex)
        {
            State = BrowserState.Error;
            TimelineStatus = "Error";
            log.Error($"[CactBridge] BrowserService error: {ex}");
        }
    }

    private async Task StopBrowserAsync()
    {
        try
        {
            if (timelinePage is not null)
            {
                await timelinePage.CloseAsync();
                timelinePage.Dispose();
                timelinePage = null;
            }
            if (alertsPage is not null)
            {
                await alertsPage.CloseAsync();
                alertsPage.Dispose();
                alertsPage = null;
            }
            if (browser is not null)
            {
                await browser.CloseAsync();
                browser.Dispose();
                browser = null;
            }
        }
        catch { /* best-effort */ }
        State = BrowserState.Idle;
        TimelineStatus = "Idle";
    }

    // -----------------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cts.Cancel();
        try { browser?.CloseAsync().GetAwaiter().GetResult(); } catch { }
        browser?.Dispose();
        cts.Dispose();
        log.Debug("[CactBridge] BrowserService disposed.");
    }
}
