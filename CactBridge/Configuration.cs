using Dalamud.Configuration;
using System;
using System.Numerics;

namespace CactBridge;

/// <summary>
/// TTS engine selection.
/// </summary>
public enum TtsEngine
{
    /// <summary>Auto-detect: SAPI on Windows, eSpeak NG on Linux.</summary>
    Auto = 0,

    /// <summary>Force Windows SAPI (<c>System.Speech</c>).</summary>
    SystemSpeech = 1,

    /// <summary>Force eSpeak NG (external process).</summary>
    EspeakNg = 2,
}

public enum AlertFontPreset
{
    FfxivAxis = 0,
    DalamudDefault = 1,
    DalamudMono = 2,
    FfxivJupiter = 3,
    FfxivTrumpGothic = 4,
}

public enum OverlayStyle
{
    Custom = 0,
    Toast = 1,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // -----------------------------------------------------------------------
    // Config window
    // -----------------------------------------------------------------------

    /// <summary>Whether the configuration window itself can be dragged.</summary>
    public bool IsConfigWindowMovable { get; set; } = true;

    // -----------------------------------------------------------------------
    // Overlay enable/disable toggles
    // -----------------------------------------------------------------------

    /// <summary>Whether the raidboss alert (Cactbot) overlay is enabled.</summary>
    public bool EnableCactbotOverlay { get; set; } = true;

    /// <summary>Whether the timeline overlay is enabled.</summary>
    public bool EnableTimelineOverlay { get; set; } = false;

    /// <summary>Whether the DPS meter overlay is enabled.</summary>
    public bool EnableDpsMeter { get; set; } = false;

    // -----------------------------------------------------------------------
    // Overlay visibility and position
    // -----------------------------------------------------------------------

    /// <summary>Whether the raidboss alert overlay is currently visible.</summary>
    public bool OverlayVisible { get; set; } = true;

    /// <summary>Screen X position of the overlay window (pixels).</summary>
    public float OverlayX { get; set; } = 100f;

    /// <summary>Screen Y position of the overlay window (pixels).</summary>
    public float OverlayY { get; set; } = 100f;

    /// <summary>When true the overlay cannot be dragged; position is read-only.</summary>
    public bool LockOverlayPosition { get; set; } = false;

    /// <summary>Width of the overlay box in pixels.</summary>
    public float OverlayWidth { get; set; } = 500f;

    /// <summary>Height of the overlay box in pixels.</summary>
    public float OverlayHeight { get; set; } = 150f;

    // -----------------------------------------------------------------------
    // Display tweaks
    // -----------------------------------------------------------------------

    /// <summary>Maximum number of alerts shown simultaneously (1–10).</summary>
    public int MaxVisibleAlerts { get; set; } = 5;

    // -----------------------------------------------------------------------
    // Overlay style
    // -----------------------------------------------------------------------

    /// <summary>How alerts are visually rendered: Custom (current) or Toast (game-style).</summary>
    public OverlayStyle AlertOverlayStyle { get; set; } = OverlayStyle.Toast;

    // -----------------------------------------------------------------------
    // Chat output
    // -----------------------------------------------------------------------

    /// <summary>When true, each alert is also printed to the local chat log as an Announcement.</summary>
    public bool OutputToChatAnnouncement { get; set; } = false;

    // -----------------------------------------------------------------------
    // Game alert notifications (native FFXIV error toasts with timer bars)
    // -----------------------------------------------------------------------

    /// <summary>Master toggle for displaying alerts as native FFXIV error toasts (red alerts with timer bars).</summary>
    public bool EnableGameAlerts { get; set; } = false;

    /// <summary>When true, Alarm-level alerts are shown as game error toasts.</summary>
    public bool GameAlertShowAlarm { get; set; } = true;

    /// <summary>When true, Alert-level alerts are shown as game error toasts.</summary>
    public bool GameAlertShowAlert { get; set; } = true;

    /// <summary>When true, Info-level alerts are shown as game error toasts.</summary>
    public bool GameAlertShowInfo { get; set; } = false;

    // -----------------------------------------------------------------------
    // Text-to-speech (TTS) settings
    // -----------------------------------------------------------------------

    /// <summary>Master toggle for all TTS output.</summary>
    public bool EnableTts { get; set; } = false;

    /// <summary>When true, Alarm-level alerts are spoken.</summary>
    public bool TtsPlayAlarm { get; set; } = true;

    /// <summary>When true, Alert-level alerts are spoken.</summary>
    public bool TtsPlayAlert { get; set; } = true;

    /// <summary>When true, Info-level alerts are spoken.</summary>
    public bool TtsPlayInfo { get; set; } = false;

    /// <summary>
    /// Which TTS engine to use.
    /// Auto = System.Speech on Windows, eSpeak NG on Linux.
    /// </summary>
    public TtsEngine TtsEngine { get; set; } = TtsEngine.Auto;

    /// <summary>
    /// Path to the eSpeak NG binary (used on Linux / Steam Deck).
    /// If empty, the service will search common locations.
    /// </summary>
    public string? EspeakNgPath { get; set; } = null;

    /// <summary>
    /// ImGui font scale applied to alert text.
    /// 1.0 = default size, 1.5 = 50 % larger, etc.
    /// </summary>
    public float AlertFontScale { get; set; } = 1.2f;

    /// <summary>
    /// Font family preset used for alert text.
    /// Defaults to the game's Axis font style.
    /// </summary>
    public AlertFontPreset AlertFontPreset { get; set; } = AlertFontPreset.FfxivAxis;

    // -----------------------------------------------------------------------
    // Text appearance
    // -----------------------------------------------------------------------

    /// <summary>Custom text color override for alert text (RGBA).</summary>
    public Vector4 AlertTextColor { get; set; } = new(1.00f, 1.00f, 1.00f, 1f);

    /// <summary>When true, uses <see cref="AlertTextColor"/> instead of per-type colors.</summary>
    public bool UseCustomAlertColor { get; set; } = false;

    /// <summary>When true, draws a dark outline behind alert text for readability.</summary>
    public bool AlertTextOutline { get; set; } = false;

    /// <summary>Color of the text outline (RGBA).</summary>
    public Vector4 AlertOutlineColor { get; set; } = new(0.00f, 0.00f, 0.00f, 1f);

    // -----------------------------------------------------------------------
    // Timeline overlay settings
    // -----------------------------------------------------------------------

    /// <summary>Screen X position of the timeline overlay (pixels).</summary>
    public float TimelineX { get; set; } = 100f;

    /// <summary>Screen Y position of the timeline overlay (pixels).</summary>
    public float TimelineY { get; set; } = 300f;

    /// <summary>Width of the timeline overlay box in pixels.</summary>
    public float TimelineWidth { get; set; } = 500f;

    /// <summary>Height of the timeline overlay box in pixels.</summary>
    public float TimelineHeight { get; set; } = 300f;

    /// <summary>When true the timeline overlay cannot be dragged.</summary>
    public bool LockTimelinePosition { get; set; } = false;

    /// <summary>Font scale applied to timeline entry text.</summary>
    public float TimelineFontScale { get; set; } = 1.0f;

    /// <summary>Font preset for timeline entries.</summary>
    public AlertFontPreset TimelineFontPreset { get; set; } = AlertFontPreset.FfxivAxis;

    /// <summary>Custom text color for timeline entries (RGBA).</summary>
    public Vector4 TimelineTextColor { get; set; } = new(1.00f, 1.00f, 1.00f, 1f);

    /// <summary>When true, uses <see cref="TimelineTextColor"/> instead of default.</summary>
    public bool UseCustomTimelineColor { get; set; } = false;

    /// <summary>When true, draws a dark outline behind timeline text.</summary>
    public bool TimelineTextOutline { get; set; } = false;

    /// <summary>Colour of the timeline text outline (RGBA).</summary>
    public Vector4 TimelineOutlineColor { get; set; } = new(0.00f, 0.00f, 0.00f, 1f);

    /// <summary>Maximum number of timeline entries shown simultaneously.</summary>
    public int MaxVisibleTimelineEntries { get; set; } = 10;

    /// <summary>How far ahead to show timeline entries (seconds).</summary>
    public float TimelineLookAhead { get; set; } = 30f;

    // -----------------------------------------------------------------------
    // Damage Meter overlay settings
    // -----------------------------------------------------------------------

    /// <summary>Screen X position of the damage meter overlay (pixels).</summary>
    public float DpsX { get; set; } = 50f;

    /// <summary>Screen Y position of the damage meter overlay (pixels).</summary>
    public float DpsY { get; set; } = 400f;

    /// <summary>Width of the damage meter overlay in pixels.</summary>
    public float DpsWidth { get; set; } = 320f;

    /// <summary>Height of the damage meter overlay in pixels.</summary>
    public float DpsHeight { get; set; } = 400f;

    /// <summary>When true the damage meter overlay cannot be dragged.</summary>
    public bool LockDpsPosition { get; set; } = false;

    /// <summary>Font scale for the DPS meter text.</summary>
    public float DpsFontScale { get; set; } = 1.0f;

    /// <summary>Font preset for the DPS meter.</summary>
    public AlertFontPreset DpsFontPreset { get; set; } = AlertFontPreset.FfxivAxis;

    /// <summary>When true, shows encounter header (title, duration, total DPS).</summary>
    public bool DpsShowHeader { get; set; } = true;

    /// <summary>When true, shows the healing columns.</summary>
    public bool DpsShowHealing { get; set; } = false;

    /// <summary>When true, shows the deaths column.</summary>
    public bool DpsShowDeaths { get; set; } = false;


    /// <summary>Background opacity (0 = transparent, 1 = solid).</summary>
    public float DpsBgAlpha { get; set; } = 0.25f;

    // -----------------------------------------------------------------------
    // Server info bar (_DTR) settings
    // -----------------------------------------------------------------------

    /// <summary>When true, shows party DPS in the native server info bar.</summary>
    public bool ShowPartyDpsInBar { get; set; } = false;

    /// <summary>When true, shows personal DPS in the native server info bar.</summary>
    public bool ShowPersonalDpsInBar { get; set; } = true;

    // -----------------------------------------------------------------------
    // Persistence helper
    // -----------------------------------------------------------------------

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
