using System;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
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
/// </summary>
public sealed class TtsService : IDisposable
{
    private readonly IPluginLog log;
    private readonly Configuration config;
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

    public TtsService(IPluginLog log, Configuration config)
    {
        this.log = log;
        this.config = config;

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

    /// <summary>
    /// Speaks the given text asynchronously (fire-and-forget).
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
    /// </summary>
    public void SpeakSync(string text)
    {
        if (disposed) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!IsReady || synth == null) return;

        try
        {
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
