using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace CactBridge.Services;

/// <summary>
/// Text-to-speech service that downloads and uses the eSpeak NG engine
/// on first launch.  Caches the binaries in <c>%APPDATA%/CactBridge/espeak-ng/</c>
/// so they survive plugin updates.
///
/// Works on both Windows (espeak-ng.exe) and Steam Deck / Linux
/// (espeak-ng.exe via Wine/Proton on SteamOS).
///
/// Speech requests are fire-and-forget via a background task so they
/// never block the game thread.
/// </summary>
public sealed class TtsService : IDisposable
{
    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    // Pin a known-good release. Update this when newer versions are desired.
    private const string EspeakVersion = "1.52.0";
    private static readonly string DownloadUrl =
        $"https://github.com/espeak-ng/espeak-ng/releases/download/{EspeakVersion}/espeak-ng-{EspeakVersion}-win64.zip";

    // Subdirectory inside the zip that contains the binaries
    private const string ZipSubDir = "espeak-ng-" + EspeakVersion + "-win64";

    // -----------------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------------

    /// <summary>Human-readable status shown in the config window.</summary>
    public string Status { get; private set; } = "Idle";

    /// <summary>Fires when <see cref="Status"/> changes.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>True once the eSpeak NG binary is ready to use.</summary>
    public bool IsReady { get; private set; }

    // -----------------------------------------------------------------------
    // Private state
    // -----------------------------------------------------------------------

    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly string storagePath;
    private readonly string espeakPath;
    private readonly CancellationTokenSource cts = new();
    private bool disposed;

    public TtsService(IPluginLog log, Configuration config, string pluginDirectory)
    {
        this.log = log;
        this.config = config;

        // Store in %APPDATA%/CactBridge/espeak-ng/ so it survives plugin updates
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        storagePath = Path.Combine(appData, "CactBridge", "espeak-ng");

        // Also check the plugin directory for a manually-placed binary (legacy)
        var pluginWin = Path.Combine(pluginDirectory, "espeak-ng.exe");
        var pluginLinux = Path.Combine(pluginDirectory, "espeak-ng");
        var cachedWin = Path.Combine(storagePath, ZipSubDir, "espeak-ng.exe");
        var cachedLinux = Path.Combine(storagePath, ZipSubDir, "espeak-ng");

        if (File.Exists(pluginWin))
        {
            espeakPath = pluginWin;
            IsReady = true;
            Status = "Ready";
            log.Information($"[CactBridge] TTS: eSpeak NG found at {espeakPath}");
        }
        else if (File.Exists(pluginLinux))
        {
            espeakPath = pluginLinux;
            IsReady = true;
            Status = "Ready";
            log.Information($"[CactBridge] TTS: eSpeak NG found at {espeakPath}");
        }
        else if (File.Exists(cachedWin))
        {
            espeakPath = cachedWin;
            IsReady = true;
            Status = "Ready";
            log.Information($"[CactBridge] TTS: eSpeak NG cached at {espeakPath}");
        }
        else if (File.Exists(cachedLinux))
        {
            espeakPath = cachedLinux;
            IsReady = true;
            Status = "Ready";
            log.Information($"[CactBridge] TTS: eSpeak NG cached at {espeakPath}");
        }
        else
        {
            espeakPath = cachedWin; // target for download
            Status = "Downloading eSpeak NG…";
            StatusChanged?.Invoke(Status);
            _ = Task.Run(() => DownloadAndExtractAsync(cts.Token));
        }
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Speaks the given text asynchronously (fire-and-forget).
    /// Respects the per-type enable toggles in <see cref="Configuration"/>.
    /// Silently skips if the engine hasn't finished downloading.
    /// </summary>
    public void Speak(string text, Models.AlertType alertType)
    {
        if (disposed) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!IsReady) return;
        if (!config.EnableTts) return;

        switch (alertType)
        {
            case Models.AlertType.Alarm when !config.TtsPlayAlarm:
            case Models.AlertType.Alert when !config.TtsPlayAlert:
            case Models.AlertType.Info  when !config.TtsPlayInfo:
                return;
        }

        _ = Task.Run(() => SpeakInternal(text), cts.Token);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cts.Cancel();
        cts.Dispose();
    }

    // -----------------------------------------------------------------------
    // Download & extraction
    // -----------------------------------------------------------------------

    private async Task DownloadAndExtractAsync(CancellationToken ct)
    {
        try
        {
            Status = "Downloading eSpeak NG…";
            StatusChanged?.Invoke(Status);
            log.Information("[CactBridge] TTS: Downloading eSpeak NG...");

            Directory.CreateDirectory(storagePath);
            var zipPath = Path.Combine(storagePath, $"espeak-ng-{EspeakVersion}-win64.zip");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;
            using var downloadStream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = File.Create(zipPath);

            var buf = new byte[65536];
            long done = 0;
            int read;
            while ((read = await downloadStream.ReadAsync(buf, ct)) > 0)
            {
                await fileStream.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
            }

            await fileStream.FlushAsync(ct);
            fileStream.Dispose();

            Status = "Extracting eSpeak NG…";
            StatusChanged?.Invoke(Status);
            log.Information($"[CactBridge] TTS: Downloaded ({done / 1024 / 1024} MB), extracting...");

            // Extract zip (overwrite if present)
            ZipFile.ExtractToDirectory(zipPath, storagePath, overwriteFiles: true);

            // Clean up the zip
            try { File.Delete(zipPath); } catch { /* best-effort */ }

            // Verify the binary is now present
            if (File.Exists(espeakPath))
            {
                IsReady = true;
                Status = "Ready";
                StatusChanged?.Invoke(Status);
                log.Information($"[CactBridge] TTS: eSpeak NG ready at {espeakPath}");
            }
            else
            {
                Status = "Error - binary not found after extraction";
                StatusChanged?.Invoke(Status);
                log.Warning($"[CactBridge] TTS: Extracted but {espeakPath} not found");
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Idle";
            StatusChanged?.Invoke(Status);
        }
        catch (Exception ex)
        {
            Status = "Download failed";
            StatusChanged?.Invoke(Status);
            log.Warning($"[CactBridge] TTS: Download failed: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Speech
    // -----------------------------------------------------------------------

    private void SpeakInternal(string text)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = espeakPath,
                Arguments              = $"\"{text}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                log.Warning("[CactBridge] TTS: Failed to start eSpeak NG process");
                return;
            }

            if (process.WaitForExit(10_000))
            {
                if (process.ExitCode != 0)
                {
                    var err = process.StandardError.ReadToEnd().Trim();
                    log.Verbose($"[CactBridge] TTS: espeak-ng exit code {process.ExitCode} — {err}");
                }
            }
            else
            {
                log.Warning("[CactBridge] TTS: eSpeak NG timed out — killing");
                try { process.Kill(); } catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[CactBridge] TTS error: {ex.Message}");
        }
    }
}
