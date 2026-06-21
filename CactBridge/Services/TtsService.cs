using System;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Config;
using Dalamud.Plugin.Services;

namespace CactBridge.Services;

/// <summary>
/// Text-to-speech service using the Windows SAPI speech synthesizer
/// (built into .NET for Windows via <c>System.Speech</c>).
///
/// On Windows this uses the installed Microsoft TTS voices (David, Zira,
/// etc.) — no downloads or external binaries needed.
///
/// On Steam Deck (Proton/Wine), <c>System.Speech</c> maps to Wine's SAPI
/// implementation, which may work depending on the Wine version and
/// whether speech-dispatcher is installed on the host Linux system.
///
/// Speech requests are fire-and-forget via <see cref="SpeechSynthesizer.SpeakAsync"/>
/// so they never block the game thread.
///
/// Volume is automatically synced to the game's **Voice** sound channel
/// (<c>SystemConfigOption.SoundVoice</c>). If the game's Voice volume
/// is 0 the TTS will be silent; set it in-game under System Config → Sound.
/// </summary>
public sealed class TtsService : IDisposable
{
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly IGameConfig gameConfig;
    private SpeechSynthesizer? synth;
    private bool disposed;

    // -----------------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------------

    /// <summary>Human-readable status shown in the config window.</summary>
    public string Status { get; private set; } = "Initialising…";

    /// <summary>Fires when <see cref="Status"/> changes.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>True once the synthesizer is ready to use.</summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// The TTS volume (0–100) currently being used.
    /// Mirrors the game's Voice sound channel volume.
    /// </summary>
    public int CurrentVolume { get; private set; } = 100;

    public TtsService(IPluginLog log, Configuration config, IGameConfig gameConfig)
    {
        this.log = log;
        this.config = config;
        this.gameConfig = gameConfig;

        try
        {
            synth = new SpeechSynthesizer();

            // Query installed voices so we know it's working
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

            // Set output to the default audio device
            synth.SetOutputToDefaultAudioDevice();

            IsReady = true;
            Status = "Ready";
            StatusChanged?.Invoke(Status);
        }
        catch (Exception ex)
        {
            log.Warning($"[CactBridge] TTS: System.Speech unavailable ({ex.Message}) — speech disabled");
            synth = null;
            Status = "Unavailable";
            StatusChanged?.Invoke(Status);
        }
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // Volume sync
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

        if (gameConfig.TryGet(SystemConfigOption.SoundVoice, out uint voiceVol))
        {
            var clamped = (int)Math.Clamp(voiceVol, 0u, 100u);
            synth.Volume = clamped;
            CurrentVolume = clamped;
            return clamped;
        }

        // Fall back to 100 if we can't read the game config
        synth.Volume = 100;
        CurrentVolume = 100;
        return 100;
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Speaks the given text asynchronously (fire-and-forget).
    /// Volume is synced from the game's Voice channel before speaking.
    /// Respects the per-type enable toggles in <see cref="Configuration"/>.
    /// Silently skips if the synthesizer failed to initialise.
    /// </summary>
    public void Speak(string text, Models.AlertType alertType)
    {
        if (disposed) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!IsReady || synth == null) return;
        if (!config.EnableTts) return;

        switch (alertType)
        {
            case Models.AlertType.Alarm when !config.TtsPlayAlarm:
            case Models.AlertType.Alert when !config.TtsPlayAlert:
            case Models.AlertType.Info  when !config.TtsPlayInfo:
                return;
        }

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

    /// <summary>
    /// Speaks text synchronously (for testing / immediate use).
    /// Blocks the calling thread until speech completes.
    /// Volume is synced from the game's Voice channel before speaking.
    /// </summary>
    public void SpeakSync(string text)
    {
        if (disposed) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!IsReady || synth == null) return;

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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (synth != null)
        {
            // Cancel any in-progress speech and release resources
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
