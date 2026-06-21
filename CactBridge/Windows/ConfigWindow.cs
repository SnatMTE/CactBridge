using System;
using System.Numerics;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using CactBridge.Services;

namespace CactBridge.Windows;

/// <summary>
/// Configuration window for the Cactbot alert overlay.
/// Lets the user toggle visibility, adjust display settings, and reposition
/// the overlay. All changes are persisted immediately via
/// <see cref="Configuration.Save"/>.
/// </summary>
public class ConfigWindow : Window, IDisposable
{
    private static readonly string[] FontPresetLabels =
    {
        "FFXIV Axis (default)",
        "Dalamud Default",
        "Dalamud Monospace",
        "FFXIV Jupiter",
        "FFXIV Trump Gothic",
    };

    private readonly Configuration           configuration;
    private readonly WebSocketService        wsService;
    private readonly OverlayWindow           overlayWindow;
    private readonly TimelineOverlayWindow   timelineWindow;
    private readonly DamageMeterOverlayWindow dpsWindow;
    private readonly RelayHttpService        relayService;
    private readonly BrowserService          browserService;
    private readonly TtsService              ttsService;

    // Cached TTS status string to avoid allocating a new string every frame
    private string ttsStatus = "Idle";

    // Constant window ID - the title can change without breaking ImGui identity
    public ConfigWindow(Plugin plugin, WebSocketService wsService, OverlayWindow overlayWindow,
                        TimelineOverlayWindow timelineWindow, DamageMeterOverlayWindow dpsWindow,
                        RelayHttpService relayService, BrowserService browserService,
                        TtsService ttsService)
        : base("CactBridge Settings###CactBridgeConfig")
    {
        Flags = ImGuiWindowFlags.NoResize    |
                ImGuiWindowFlags.NoCollapse;

        Size          = new Vector2(620, 720);
        SizeCondition = ImGuiCond.Always;

        configuration       = plugin.Configuration;
        this.wsService      = wsService;
        this.overlayWindow  = overlayWindow;
        this.timelineWindow = timelineWindow;
        this.dpsWindow      = dpsWindow;
        this.relayService   = relayService;
        this.browserService = browserService;
        this.ttsService     = ttsService;

        // Keep the cached status string in sync
        ttsStatus = ttsService.Status;
        ttsService.StatusChanged += s => ttsStatus = s;
    }

    public void Dispose() { }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public override void PreDraw()
    {
        // Allow or prevent dragging this config window based on config
        if (configuration.IsConfigWindowMovable)
            Flags &= ~ImGuiWindowFlags.NoMove;
        else
            Flags |= ImGuiWindowFlags.NoMove;
    }

    // -----------------------------------------------------------------------
    // Draw
    // -----------------------------------------------------------------------

    public override void Draw()
    {
        if (!string.IsNullOrEmpty(wsService.CurrentZone))
        {
            ImGui.Text("Zone:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.80f, 0.80f, 1.00f, 1f), wsService.CurrentZone);
            ImGui.Separator();
        }

        // ------------------------------------------------------------------
        // Tab bar: Callout | Timeline | Damage Meter
        // ------------------------------------------------------------------
        if (ImGui.BeginTabBar("SettingsTabs", ImGuiTabBarFlags.None))
        {
            // ==============================================================
            // Callout tab (all current settings)
            // ==============================================================
            if (ImGui.BeginTabItem("Callout"))
            {
                // ----------------------------------------------------------
                // Enable / disable the overlay
                // ----------------------------------------------------------
                var enableOverlay = configuration.EnableCactbotOverlay;
                if (ImGui.Checkbox("Enable Cactbot overlay", ref enableOverlay))
                {
                    configuration.EnableCactbotOverlay = enableOverlay;
                    configuration.Save();
                }

                // ----------------------------------------------------------
                // Overlay style: Custom or Toast
                // ----------------------------------------------------------
                var style = (int)configuration.AlertOverlayStyle;
                ImGui.SetNextItemWidth(180f);
                if (ImGui.Combo("Overlay style", ref style, "Custom\0Toast\0"))
                {
                    configuration.AlertOverlayStyle = (OverlayStyle)style;
                    configuration.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Custom — renders alerts in the ImGui overlay with per-type colours and adjustable fonts\nToast — sends alerts as real FFXIV toasts via the game's native toast system");

                ImGui.Spacing();
                ImGui.Separator();

                // ----------------------------------------------------------
                // Background browser status
                // ----------------------------------------------------------
                ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Background Cactbot Browser");
                ImGui.Spacing();

                if (relayService.Port <= 0)
                {
                    ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Relay HTTP server failed to start (port in use?).");
                }
                else
                {
                    switch (browserService.State)
                    {
                        case BrowserService.BrowserState.Downloading:
                            ImGui.TextColored(new Vector4(0.80f, 0.80f, 0.20f, 1f), "\u23f3 Downloading Chromium\u2026");
                            ImGui.ProgressBar(browserService.DownloadPct / 100f, new Vector2(-1, 20), $"{browserService.DownloadPct}%");
                            ImGui.TextWrapped("One-time ~150 MB download. Alerts will begin once complete.");
                            break;
                        case BrowserService.BrowserState.Launching:
                            ImGui.TextColored(new Vector4(0.80f, 0.80f, 0.20f, 1f), "\u23f3 Launching browser\u2026");
                            break;
                        case BrowserService.BrowserState.Running:
                            ImGui.TextColored(new Vector4(0.20f, 1.00f, 0.20f, 1f), $"\u25cf {browserService.Status}");
                            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), relayService.OverlayUrl);
                            break;
                        case BrowserService.BrowserState.Error:
                            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"\u25cf {browserService.Status}");
                            break;
                        default:
                            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "\u25cf Idle");
                            break;
                    }
                }

                ImGui.Spacing();
                if (ImGui.Button("Restart Browser"))
                    browserService.Restart();
                ImGui.SameLine();
                if (ImGui.Button("Copy Overlay URL"))
                    ImGui.SetClipboardText(relayService.OverlayUrl);
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "(?)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Copy URL to open the overlay in your own browser if needed.");

                ImGui.Separator();

                if (configuration.AlertOverlayStyle == OverlayStyle.Custom)
                {
                // ----------------------------------------------------------
                // Move mode
                // ----------------------------------------------------------
                var moveMode = overlayWindow.IsMoveMode;
                if (ImGui.Checkbox("Move mode (/cactbridge)", ref moveMode))
                {
                    overlayWindow.SetMoveMode(moveMode);
                    overlayWindow.IsOpen = true;
                }

                // ----------------------------------------------------------
                // Position lock
                // ----------------------------------------------------------
                var locked = configuration.LockOverlayPosition;
                if (ImGui.Checkbox("Lock overlay position", ref locked))
                {
                    configuration.LockOverlayPosition = locked;
                    configuration.Save();
                }

                // ----------------------------------------------------------
                // Max visible alerts slider
                // ----------------------------------------------------------
                var maxAlerts = configuration.MaxVisibleAlerts;
                ImGui.SetNextItemWidth(120f);
                if (ImGui.SliderInt("Max visible alerts", ref maxAlerts, 1, 10))
                {
                    configuration.MaxVisibleAlerts = maxAlerts;
                    configuration.Save();
                }

                // ----------------------------------------------------------
                // Font scale controls
                // ----------------------------------------------------------
                ImGui.Text("Font size");
                ImGui.SameLine();
                if (ImGui.Button("-") && configuration.AlertFontScale > 0.1f)
                {
                    configuration.AlertFontScale = Math.Max(0.1f, configuration.AlertFontScale - 0.1f);
                    configuration.Save();
                }
                ImGui.SameLine();
                ImGui.Text($"{configuration.AlertFontScale:0.0}x");
                ImGui.SameLine();
                if (ImGui.Button("+"))
                {
                    configuration.AlertFontScale += 0.1f;
                    configuration.Save();
                }

                // Font family selection
                var fontPreset = (int)configuration.AlertFontPreset;
                ImGui.SetNextItemWidth(240f);
                if (ImGui.Combo("Alert font", ref fontPreset, FontPresetLabels, FontPresetLabels.Length))
                {
                    configuration.AlertFontPreset = (AlertFontPreset)fontPreset;
                    configuration.Save();
                }

                // ----------------------------------------------------------
                // Text colour override
                // ----------------------------------------------------------
                var useCustomColor = configuration.UseCustomAlertColor;
                if (ImGui.Checkbox("Override alert text colour", ref useCustomColor))
                {
                    configuration.UseCustomAlertColor = useCustomColor;
                    configuration.Save();
                }
                if (useCustomColor)
                {
                    var textCol = configuration.AlertTextColor;
                    if (ImGui.ColorEdit4("Text colour", ref textCol, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreviewHalf))
                    {
                        configuration.AlertTextColor = textCol;
                        configuration.Save();
                    }
                }

                // ----------------------------------------------------------
                // Text outline
                // ----------------------------------------------------------
                var outline = configuration.AlertTextOutline;
                if (ImGui.Checkbox("Text outline", ref outline))
                {
                    configuration.AlertTextOutline = outline;
                    configuration.Save();
                }
                if (outline)
                {
                    var outlineCol = configuration.AlertOutlineColor;
                    if (ImGui.ColorEdit4("Outline colour", ref outlineCol, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreviewHalf))
                    {
                        configuration.AlertOutlineColor = outlineCol;
                        configuration.Save();
                    }
                }

                ImGui.Separator();

                // ----------------------------------------------------------
                // Position readout + reset
                // ----------------------------------------------------------
                ImGui.Text($"Position: ({configuration.OverlayX:F0}, {configuration.OverlayY:F0})");
                ImGui.SameLine();
                if (ImGui.Button("Reset"))
                {
                    configuration.OverlayX = 100f;
                    configuration.OverlayY = 100f;
                    overlayWindow.ResetPosition();   // re-apply saved coords next PreDraw
                    configuration.Save();
                }

                ImGui.Separator();

                // ----------------------------------------------------------
                // Overlay box size
                // ----------------------------------------------------------
                ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Overlay Box Size");
                ImGui.Spacing();

                var boxW = configuration.OverlayWidth;
                var boxH = configuration.OverlayHeight;
                ImGui.SetNextItemWidth(100f);
                if (ImGui.DragFloat("Width", ref boxW, 1f, 100f, 2000f, "%.0f"))
                {
                    configuration.OverlayWidth = Math.Clamp(boxW, 100f, 2000f);
                    configuration.Save();
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100f);
                if (ImGui.DragFloat("Height", ref boxH, 1f, 50f, 1000f, "%.0f"))
                {
                    configuration.OverlayHeight = Math.Clamp(boxH, 50f, 1000f);
                    configuration.Save();
                }

                ImGui.Spacing();
                }
                else
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f),
                        "Toast mode is active \u2014 overlay display settings do not apply.");
                    ImGui.Spacing();
                }

                ImGui.Separator();

                // ----------------------------------------------------------
                // Misc
                // ----------------------------------------------------------
                var movable = configuration.IsConfigWindowMovable;
                if (ImGui.Checkbox("Movable config window", ref movable))
                {
                    configuration.IsConfigWindowMovable = movable;
                    configuration.Save();
                }

                // ----------------------------------------------------------
                // Chat announcement output
                // ----------------------------------------------------------
                var chatOut = configuration.OutputToChatAnnouncement;
                if (ImGui.Checkbox("Output alerts to chat as Announcement", ref chatOut))
                {
                    configuration.OutputToChatAnnouncement = chatOut;
                    configuration.Save();
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "(?)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Prints each alert to your local chat log using the Announcement channel.");

                ImGui.Separator();

                // ----------------------------------------------------------
                // Text-to-Speech (TTS)
                // ----------------------------------------------------------
                ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Text-to-Speech");
                ImGui.Spacing();

                // TTS engine status
                if (ttsService.IsReady)
                {
                    ImGui.TextColored(new Vector4(0.20f, 1.00f, 0.20f, 1f), $"\u25cf {ttsStatus}");
                    ImGui.SameLine();
                    ImGui.TextDisabled($" (Vol: {ttsService.CurrentVolume}% \u2014 follows game Voice)");
                }
                else if (ttsStatus == "Unavailable")
                    ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"\u25cf {ttsStatus}");
                else
                    ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), $"\u25cf {ttsStatus}");

                ImGui.Spacing();

                var enableTts = configuration.EnableTts;
                if (ImGui.Checkbox("Enable TTS (spoken alerts)", ref enableTts))
                {
                    configuration.EnableTts = enableTts;
                    configuration.Save();
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "(?)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Speaks Cactbot alerts aloud using Windows Speech API (System.Speech).\nVolume is synced to the game's Voice sound channel \u2014\nadjust it in System Config \u2192 Sound Settings \u2192 Voice.");

                if (configuration.EnableTts)
                {
                    ImGui.Indent(16f);

                    var ttsAlarm = configuration.TtsPlayAlarm;
                    if (ImGui.Checkbox("Play for Alarm alerts", ref ttsAlarm))
                    {
                        configuration.TtsPlayAlarm = ttsAlarm;
                        configuration.Save();
                    }

                    var ttsAlert = configuration.TtsPlayAlert;
                    if (ImGui.Checkbox("Play for Alert alerts", ref ttsAlert))
                    {
                        configuration.TtsPlayAlert = ttsAlert;
                        configuration.Save();
                    }

                    var ttsInfo = configuration.TtsPlayInfo;
                    if (ImGui.Checkbox("Play for Info alerts", ref ttsInfo))
                    {
                        configuration.TtsPlayInfo = ttsInfo;
                        configuration.Save();
                    }

                    ImGui.Unindent(16f);
                    ImGui.Spacing();
                }

                ImGui.Separator();

                ImGui.EndTabItem();
            }

            // ==============================================================
            // Timeline tab
            // ==============================================================
            if (ImGui.BeginTabItem("Timeline"))
            {
                // ----------------------------------------------------------
                // Enable / disable the overlay
                // ----------------------------------------------------------
                var enableTimeline = configuration.EnableTimelineOverlay;
                if (ImGui.Checkbox("Enable Timeline overlay", ref enableTimeline))
                {
                    configuration.EnableTimelineOverlay = enableTimeline;
                    configuration.Save();
                }
                ImGui.Spacing();
                ImGui.Separator();

                // ----------------------------------------------------------
                // Background browser status (timeline page)
                // ----------------------------------------------------------
                ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Background Cactbot Browser (Timeline)");
                ImGui.Spacing();

                if (relayService.Port <= 0)
                {
                    ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Relay HTTP server failed to start (port in use?).");
                }
                else
                {
                    var tlStatus = browserService.TimelineStatus;
                    if (tlStatus == "Running")
                        ImGui.TextColored(new Vector4(0.20f, 1.00f, 0.20f, 1f), $"\u25cf {tlStatus}");
                    else if (tlStatus == "Error")
                        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"\u25cf {tlStatus}");
                    else if (tlStatus == "Launching\u2026")
                        ImGui.TextColored(new Vector4(0.80f, 0.80f, 0.20f, 1f), $"\u23f3 {tlStatus}");
                    else
                        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), $"\u25cf {tlStatus}");

                    ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), relayService.TimelineOverlayUrl);
                }

                ImGui.Spacing();
                if (ImGui.Button("Restart Timeline Browser"))
                    browserService.RestartTimeline();
                ImGui.SameLine();
                if (ImGui.Button("Copy Timeline URL"))
                    ImGui.SetClipboardText(relayService.TimelineOverlayUrl);
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "(?)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Copy URL to open the timeline overlay in your own browser if needed.");

                ImGui.Separator();

                // ----------------------------------------------------------
                // Move mode
                // ----------------------------------------------------------
                var tlMoveMode = timelineWindow.IsMoveMode;
                if (ImGui.Checkbox("Move mode (Timeline)", ref tlMoveMode))
                {
                    timelineWindow.SetMoveMode(tlMoveMode);
                    timelineWindow.IsOpen = true;
                }

                // ----------------------------------------------------------
                // Position lock
                // ----------------------------------------------------------
                var tlLocked = configuration.LockTimelinePosition;
                if (ImGui.Checkbox("Lock timeline overlay position", ref tlLocked))
                {
                    configuration.LockTimelinePosition = tlLocked;
                    configuration.Save();
                }

                // ----------------------------------------------------------
                // Max visible entries + look-ahead
                // ----------------------------------------------------------
                var tlMax = configuration.MaxVisibleTimelineEntries;
                ImGui.SetNextItemWidth(120f);
                if (ImGui.SliderInt("Max visible entries", ref tlMax, 1, 30))
                {
                    configuration.MaxVisibleTimelineEntries = tlMax;
                    configuration.Save();
                }

                var tlLookAhead = configuration.TimelineLookAhead;
                ImGui.SetNextItemWidth(120f);
                if (ImGui.SliderFloat("Look-ahead (seconds)", ref tlLookAhead, 5f, 120f))
                {
                    configuration.TimelineLookAhead = tlLookAhead;
                    configuration.Save();
                }

                ImGui.Separator();

                // ----------------------------------------------------------
                // Font size controls
                // ----------------------------------------------------------
                ImGui.Text("Font size");
                ImGui.SameLine();
                if (ImGui.Button("-##tl") && configuration.TimelineFontScale > 0.1f)
                {
                    configuration.TimelineFontScale = Math.Max(0.1f, configuration.TimelineFontScale - 0.1f);
                    configuration.Save();
                }
                ImGui.SameLine();
                ImGui.Text($"{configuration.TimelineFontScale:0.0}x");
                ImGui.SameLine();
                if (ImGui.Button("+##tl"))
                {
                    configuration.TimelineFontScale += 0.1f;
                    configuration.Save();
                }

                var tlFontPreset = (int)configuration.TimelineFontPreset;
                ImGui.SetNextItemWidth(240f);
                if (ImGui.Combo("Timeline font", ref tlFontPreset, FontPresetLabels, FontPresetLabels.Length))
                {
                    configuration.TimelineFontPreset = (AlertFontPreset)tlFontPreset;
                    configuration.Save();
                }

                // ----------------------------------------------------------
                // Text colour override
                // ----------------------------------------------------------
                var tlUseCustomColor = configuration.UseCustomTimelineColor;
                if (ImGui.Checkbox("Override timeline text colour", ref tlUseCustomColor))
                {
                    configuration.UseCustomTimelineColor = tlUseCustomColor;
                    configuration.Save();
                }
                if (tlUseCustomColor)
                {
                    var tlTextCol = configuration.TimelineTextColor;
                    if (ImGui.ColorEdit4("Timeline colour", ref tlTextCol, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreviewHalf))
                    {
                        configuration.TimelineTextColor = tlTextCol;
                        configuration.Save();
                    }
                }

                // ----------------------------------------------------------
                // Text outline
                // ----------------------------------------------------------
                var tlOutline = configuration.TimelineTextOutline;
                if (ImGui.Checkbox("Timeline text outline", ref tlOutline))
                {
                    configuration.TimelineTextOutline = tlOutline;
                    configuration.Save();
                }
                if (tlOutline)
                {
                    var tlOutlineCol = configuration.TimelineOutlineColor;
                    if (ImGui.ColorEdit4("Timeline outline colour", ref tlOutlineCol, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreviewHalf))
                    {
                        configuration.TimelineOutlineColor = tlOutlineCol;
                        configuration.Save();
                    }
                }

                ImGui.Separator();

                // ----------------------------------------------------------
                // Position readout + reset
                // ----------------------------------------------------------
                ImGui.Text($"Position: ({configuration.TimelineX:F0}, {configuration.TimelineY:F0})");
                ImGui.SameLine();
                if (ImGui.Button("Reset##tl"))
                {
                    configuration.TimelineX = 100f;
                    configuration.TimelineY = 300f;
                    timelineWindow.ResetPosition();
                    configuration.Save();
                }

                ImGui.Separator();

                // ----------------------------------------------------------
                // Overlay box size
                // ----------------------------------------------------------
                ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Timeline Overlay Box Size");
                ImGui.Spacing();

                var tlBoxW = configuration.TimelineWidth;
                var tlBoxH = configuration.TimelineHeight;
                ImGui.SetNextItemWidth(100f);
                if (ImGui.DragFloat("Width##tl", ref tlBoxW, 1f, 100f, 2000f, "%.0f"))
                {
                    configuration.TimelineWidth = Math.Clamp(tlBoxW, 100f, 2000f);
                    configuration.Save();
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100f);
                if (ImGui.DragFloat("Height##tl", ref tlBoxH, 1f, 50f, 1000f, "%.0f"))
                {
                    configuration.TimelineHeight = Math.Clamp(tlBoxH, 50f, 1000f);
                    configuration.Save();
                }
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.EndTabItem();
            }

            // ==============================================================
            // Damage Meter tab
            // ==============================================================
            if (ImGui.BeginTabItem("Damage Meter"))
            {
                // ----------------------------------------------------------
                // Enable / disable the overlay
                // ----------------------------------------------------------
                var enableDps = configuration.EnableDpsMeter;
                if (ImGui.Checkbox("Enable Damage Meter overlay", ref enableDps))
                {
                    configuration.EnableDpsMeter = enableDps;
                    configuration.Save();
                }
                ImGui.Spacing();
                ImGui.Separator();

                // ----------------------------------------------------------
                // Current encounter info (live)
                // ----------------------------------------------------------
                var enc = wsService.GetEncounter();
                if (enc != null)
                {
                    ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Current Encounter");
                    ImGui.Text($"Fight: {enc.Title}");
                    ImGui.Text($"Duration: {enc.DurationStr}");
                    ImGui.Text($"Total DPS: {enc.DPS:F0}");
                    ImGui.Text($"Total Damage: {enc.DamageStr}");
                    ImGui.Separator();
                }

                // ----------------------------------------------------------
                // Move mode
                // ----------------------------------------------------------
                var dpsMoveMode = dpsWindow.IsMoveMode;
                if (ImGui.Checkbox("Move mode (Damage Meter)", ref dpsMoveMode))
                {
                    dpsWindow.SetMoveMode(dpsMoveMode);
                    dpsWindow.IsOpen = true;
                }

                // ----------------------------------------------------------
                // Position lock
                // ----------------------------------------------------------
                var dpsLocked = configuration.LockDpsPosition;
                if (ImGui.Checkbox("Lock damage meter position", ref dpsLocked))
                {
                    configuration.LockDpsPosition = dpsLocked;
                    configuration.Save();
                }

                ImGui.Separator();

                // ----------------------------------------------------------
                // Column visibility toggles
                // ----------------------------------------------------------
                ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Column Visibility");
                ImGui.Spacing();

                var showHeader = configuration.DpsShowHeader;
                if (ImGui.Checkbox("Show encounter header", ref showHeader))
                {
                    configuration.DpsShowHeader = showHeader;
                    configuration.Save();
                }

                var showHealing = configuration.DpsShowHealing;
                if (ImGui.Checkbox("Show healing columns", ref showHealing))
                {
                    configuration.DpsShowHealing = showHealing;
                    configuration.Save();
                }

                var showDeaths = configuration.DpsShowDeaths;
                if (ImGui.Checkbox("Show deaths column", ref showDeaths))
                {
                    configuration.DpsShowDeaths = showDeaths;
                    configuration.Save();
                }

                ImGui.Separator();

                // ----------------------------------------------------------
                // Server info bar (_DTR) options
                // ----------------------------------------------------------
                ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Server Info Bar");
                ImGui.Spacing();

                var showPartyDps = configuration.ShowPartyDpsInBar;
                if (ImGui.Checkbox("Show party DPS in server info bar", ref showPartyDps))
                {
                    configuration.ShowPartyDpsInBar = showPartyDps;
                    configuration.Save();
                }

                var showPersonalDps = configuration.ShowPersonalDpsInBar;
                if (ImGui.Checkbox("Show personal DPS in server info bar", ref showPersonalDps))
                {
                    configuration.ShowPersonalDpsInBar = showPersonalDps;
                    configuration.Save();
                }

                ImGui.Separator();

                // ----------------------------------------------------------
                // Background opacity
                // ----------------------------------------------------------
                ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Appearance");
                ImGui.Spacing();

                var bgAlpha = configuration.DpsBgAlpha;
                ImGui.SetNextItemWidth(200f);
                if (ImGui.SliderFloat("Background opacity", ref bgAlpha, 0f, 1f, "%.2f"))
                {
                    configuration.DpsBgAlpha = bgAlpha;
                    configuration.Save();
                }

                // ----------------------------------------------------------
                // Font size controls
                // ----------------------------------------------------------
                ImGui.Text("Font size");
                ImGui.SameLine();
                if (ImGui.Button("-##dps") && configuration.DpsFontScale > 0.1f)
                {
                    configuration.DpsFontScale = Math.Max(0.1f, configuration.DpsFontScale - 0.1f);
                    configuration.Save();
                }
                ImGui.SameLine();
                ImGui.Text($"{configuration.DpsFontScale:0.0}x");
                ImGui.SameLine();
                if (ImGui.Button("+##dps"))
                {
                    configuration.DpsFontScale += 0.1f;
                    configuration.Save();
                }

                var dpsFontPreset = (int)configuration.DpsFontPreset;
                ImGui.SetNextItemWidth(240f);
                if (ImGui.Combo("DPS font", ref dpsFontPreset, FontPresetLabels, FontPresetLabels.Length))
                {
                    configuration.DpsFontPreset = (AlertFontPreset)dpsFontPreset;
                    configuration.Save();
                }

                ImGui.Separator();

                // ----------------------------------------------------------
                // Position readout + reset
                // ----------------------------------------------------------
                ImGui.Text($"Position: ({configuration.DpsX:F0}, {configuration.DpsY:F0})");
                ImGui.SameLine();
                if (ImGui.Button("Reset##dps"))
                {
                    configuration.DpsX = 50f;
                    configuration.DpsY = 400f;
                    dpsWindow.ResetPosition();
                    configuration.Save();
                }

                ImGui.Separator();

                // ----------------------------------------------------------
                // Overlay box size
                // ----------------------------------------------------------
                ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Damage Meter Box Size");
                ImGui.Spacing();

                var dpsBoxW = configuration.DpsWidth;
                var dpsBoxH = configuration.DpsHeight;
                ImGui.SetNextItemWidth(100f);
                if (ImGui.DragFloat("Width##dps", ref dpsBoxW, 1f, 200f, 2000f, "%.0f"))
                {
                    configuration.DpsWidth = Math.Clamp(dpsBoxW, 200f, 2000f);
                    configuration.Save();
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100f);
                if (ImGui.DragFloat("Height##dps", ref dpsBoxH, 1f, 100f, 1000f, "%.0f"))
                {
                    configuration.DpsHeight = Math.Clamp(dpsBoxH, 100f, 1000f);
                    configuration.Save();
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }
}

