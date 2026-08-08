using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Config;
using Dalamud.Plugin.Services;

namespace CactBridge.Services;

/// <summary>
/// Text-to-speech service. Uses <c>System.Speech</c> (Windows SAPI) on
/// Windows, with the game's Voice channel volume applied automatically.
/// On Linux / Steam Deck it falls back to eSpeak NG as an external process.
///
/// Speech requests are fire-and-forget so they never block the game thread.
/// </summary>
public sealed class TtsService : IDisposable
{
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly IGameConfig gameConfig;

    // Primary engine: Windows SAPI
    private SpeechSynthesizer? synth;

    // Fallback engine: eSpeak NG process (used on Linux)
    private string? espeakNgPath;

    private bool disposed;
    private readonly CancellationTokenSource cts = new();

    /// <summary>Which engine is currently active (shown in UI).</summary>
    public string ActiveEngine { get; private set; } = "None";

    /// <summary>Human-readable status shown in the config window.</summary>
    public string Status { get; private set; } = "Initialising…";

    /// <summary>Fires when <see cref="Status"/> changes.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>True once an engine is ready to use.</summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// The TTS volume (0–100) currently being used.
    /// On Windows this mirrors the game's Voice channel.
    /// On Linux this is a manual setting.
    /// </summary>
    public int CurrentVolume { get; private set; } = 100;

    // -----------------------------------------------------------------------
    // Constructor
    // -----------------------------------------------------------------------

    public TtsService(IPluginLog log, Configuration config, IGameConfig gameConfig)
    {
        this.log = log;
        this.config = config;
        this.gameConfig = gameConfig;

        var useEspeak = config.TtsEngine == TtsEngine.EspeakNg
                        || (config.TtsEngine == TtsEngine.Auto && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        if (useEspeak)
        {
            // TODO: eSpeak NG on Linux / Steam Deck. The binary lookup and
            // download logic used to live here (see git history).
            log.Warning("[CactBridge] TTS: eSpeak NG engine selected but not yet implemented; speech disabled");
            Status = "eSpeak NG (TODO)";
            StatusChanged?.Invoke(Status);
        }
        else
        {
            TryInitSystemSpeech();
        }
    }

    /// <summary>Attempts to initialise the Windows SAPI engine.</summary>
    private void TryInitSystemSpeech()
    {
        try
        {
            synth = new SpeechSynthesizer();

            var voices = synth.GetInstalledVoices();
            var voiceCount = 0;
            foreach (var v in voices)
                if (v.Enabled) voiceCount++;

            log.Information($"[CactBridge] TTS: System.Speech ready ({voiceCount} voice(s) available)");

            // Select the first female voice if available, otherwise default
            foreach (var v in voices)
            {
                if (v.Enabled && v.VoiceInfo.Gender == VoiceGender.Female)
                {
                    synth.SelectVoice(v.VoiceInfo.Name);
                    log.Information($"[CactBridge] TTS: Selected voice \"{v.VoiceInfo.Name}\"");
                    break;
                }
            }

            synth.SetOutputToDefaultAudioDevice();

            ActiveEngine = "System.Speech";
            IsReady = true;
            Status = "Ready";
            StatusChanged?.Invoke(Status);
        }
        catch (Exception ex)
        {
            log.Warning($"[CactBridge] TTS: System.Speech unavailable ({ex.Message})");
            synth = null;
            Status = "Unavailable";
            StatusChanged?.Invoke(Status);
        }
    }

    // -----------------------------------------------------------------------
    // Volume sync (Windows SAPI only)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the game's Voice channel volume (<c>SystemConfigOption.SoundVoice</c>)
    /// and applies it to the speech synthesizer.
    ///
    /// Returns the volume level (0–100), or 100 if the config value can't be read.
    /// </summary>
    private int SyncVolumeFromGame()
    {
        if (synth == null) return 0;

        try
        {
            if (gameConfig.TryGet(SystemConfigOption.SoundVoice, out uint voiceVol))
            {
                var clamped = (int)Math.Clamp(voiceVol, 0u, 100u);
                synth.Volume = clamped;
                CurrentVolume = clamped;
                return clamped;
            }
        }
        catch (Exception ex)
        {
            // IGameConfig.TryGet can throw exceptions when logged out or during zone changes.
            // Fall through to default volume rather than disrupting TTS.
            log.Verbose($"[CactBridge] TTS: Failed to read game volume: {ex.Message}");
        }

        synth.Volume = 100;
        CurrentVolume = 100;
        return 100;
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Speaks the given text asynchronously (fire-and-forget).
    /// Routes to the active engine (System.Speech or eSpeak NG).
    /// Respects the per-type enable toggles in <see cref="Configuration"/>.
    /// Silently skips if no engine is ready.
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

        if (synth != null)
        {
            // Windows SAPI path
            try
            {
                SyncVolumeFromGame();
                synth.SpeakAsync(text);
            }
            catch (Exception ex)
            {
                log.Warning($"[CactBridge] TTS: SpeakAsync failed: {ex.Message}");
            }
        }
        else if (espeakNgPath != null)
        {
            // TODO: eSpeak NG speak path. Spawn the binary with an amplitude
            // matching the Voice channel volume.
            _ = Task.Run(() => SpeakEspeakNg(text), cts.Token);
        }
    }

    /// <summary>
    /// Speaks text synchronously (for testing / immediate use).
    /// Blocks the calling thread until speech completes.
    /// </summary>
    public void SpeakSync(string text)
    {
        if (disposed) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!IsReady) return;

        if (synth != null)
        {
            try
            {
                SyncVolumeFromGame();
                synth.Speak(text);
            }
            catch (Exception ex)
            {
                log.Warning($"[CactBridge] TTS: Speak failed: {ex.Message}");
            }
        }
        else if (espeakNgPath != null)
        {
            SpeakEspeakNg(text);
        }
    }

    // -----------------------------------------------------------------------
    // eSpeak NG fallback (stub)
    // -----------------------------------------------------------------------

    /// <summary>Speaks text via the eSpeak NG external process (background thread).</summary>
    private void SpeakEspeakNg(string text)
    {
        // TODO: not implemented yet, just log the attempt.
        log.Verbose($"[CactBridge] TTS: eSpeak NG would speak: \"{text}\"");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cts.Cancel();
        cts.Dispose();

        if (synth != null)
        {
            try
            {
                synth.SpeakAsyncCancelAll();
                synth.Dispose();
            }
            catch (Exception ex)
            {
                log.Verbose($"[CactBridge] TTS: Dispose: {ex.Message}");
            }
            synth = null;
        }
    }
}
