using System;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.ImGuiNotification;
using FFXIVClientStructs.FFXIV.Client.UI;
using CactBridge.Models;
using CactBridge.Services;
using CactBridge.Windows;

namespace CactBridge;

/// <summary>
/// Entry point for the Cactbot Alert Overlay plugin.
///
/// Responsibilities:
///   - Inject Dalamud services
///   - Start <see cref="WebSocketService"/> on a background thread
///   - Register ImGui windows with Dalamud's <see cref="WindowSystem"/>
///   - Handle slash commands
///   - Clean up everything on unload
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    // -----------------------------------------------------------------------
    // Dalamud service injection
    // Dalamud populates these via [PluginService] before the constructor runs
    // -----------------------------------------------------------------------
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider        TextureProvider  { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager   { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState      { get; private set; } = null!;
    [PluginService] internal static IPlayerState            PlayerState      { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager      { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log              { get; private set; } = null!;
    [PluginService] internal static IChatGui                ChatGui          { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework        { get; private set; } = null!;
    [PluginService] internal static IDtrBar                 DtrBar           { get; private set; } = null!;
    [PluginService] internal static IToastGui               ToastGui         { get; private set; } = null!;
    [PluginService] internal static IGameConfig             GameConfig       { get; private set; } = null!;
    [PluginService] internal static INotificationManager    NotificationManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable            ObjectTable      { get; private set; } = null!;


    // /cactbridge       - open settings
    // /cactbridge move  - toggle move mode
    private const string CommandName = "/cactbridge";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("CactBridge");

    // -----------------------------------------------------------------------
    // Plugin-owned objects (initialised lazily after IINACT is detected)
    // -----------------------------------------------------------------------
    private WebSocketService?       wsService;
    private RelayHttpService?       relayService;
    private BrowserService?         browserService;
    private TtsService?             ttsService;
    private ConfigWindow?           ConfigWindow  { get; set; }
    private OverlayWindow?          OverlayWindow { get; set; }
    private TimelineOverlayWindow?  TimelineOverlayWindow { get; set; }
    private DamageMeterOverlayWindow? DamageMeterOverlayWindow { get; set; }


    // DTR (server info bar) entries
    private IDtrBarEntry? partyDpsEntry;
    private IDtrBarEntry? personalDpsEntry;
    private string?      localPlayerName;

    // Deferred-initialisation state
    private bool   _initialized;
    private string _pluginDir = string.Empty;

    // -----------------------------------------------------------------------
    // Constructor — minimal setup only; heavy init waits for IINACT
    // -----------------------------------------------------------------------
    public Plugin()
    {
        // Load or create configuration from Dalamud's config storage
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Cache the plugin directory for later use during deferred init
        _pluginDir = PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;

        // Register slash command immediately so it's always available
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open CactBridge settings. '/cactbridge move' toggles move mode for the overlay."
        });

        // Hook into Dalamud's UI draw pipeline (WindowSystem.Draw is a no-op
        // until windows are created, so it's safe to attach early)
        PluginInterface.UiBuilder.Draw        += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   += ToggleMainUi;
        Framework.Update                       += OnFrameworkUpdate;

        Log.Information("[CactBridge] Plugin loaded. Waiting for IINACT...");
    }

    // -----------------------------------------------------------------------
    // Full initialisation — called once IINACT is detected
    // -----------------------------------------------------------------------
    private void InitializeServices()
    {
        // Re-entrancy guard: set the flag FIRST so that even if a step below
        // throws, we never retry initialisation every frame. Previously a
        // mid-init failure left _initialized false, which caused a per-frame
        // retry loop that spawned duplicate services and an endless browser
        // restart loop (the "Loading Browser…" loop seen in the log).
        if (_initialized) return;
        _initialized = true;

        try
        {
            Log.Information("[CactBridge] IINACT detected — initialising services...");

            // Start the WebSocket service - connects and listens on a background Task
            wsService = new WebSocketService(Log, Configuration);

            // Start the relay HTTP server - serves raidboss-user.js to Cactbot
            relayService   = new RelayHttpService(Log, _pluginDir);

            // Start the TTS service - speaks alerts aloud via Windows SAPI
            // Volume is automatically synced to the game's Voice sound channel.
            ttsService = new TtsService(Log, Configuration, GameConfig);

            // Launch headless browser with both alerts and timeline pages
            browserService = new BrowserService(Log, _pluginDir, relayService.OverlayUrl, relayService.TimelineOverlayUrl);

            // Forward ACT log lines from the plugin's WebSocket into the headless
            // browser page, so the Cactbot timeline controller receives data even
            // if the browser's own WebSocket connection to OverlayPlugin fails.
            wsService.OnRawLogLine += browserService.ForwardLogLine;

            // Forward zone-change events so Cactbot's PopupText.OnChangeZone fires,
            // which calls ReloadTimelines() and activates the timeline for the zone.
            wsService.OnZoneChanged += browserService.ForwardChangeZone;

            // Receive broadcasts from the headless browser pages via the native
            // PuppeteerSharp bridge (bypasses OverlayPlugin WebSocket entirely).
            browserService.OnPageBroadcast += wsService.HandlePageBroadcast;

            // Forward alerts to the TTS service for spoken output
            wsService.OnAlertForTts += ttsService.Speak;

            // Subscribe to browser state changes for subsequent steps
            browserService.StateChanged += OnBrowserStateChanged;

            // Create windows - OverlayWindow must exist before ConfigWindow
            // so ConfigWindow can hold a reference to it
            OverlayWindow             = new OverlayWindow(this, wsService);
            TimelineOverlayWindow     = new TimelineOverlayWindow(this, wsService);
            DamageMeterOverlayWindow  = new DamageMeterOverlayWindow(this, wsService);
            ConfigWindow              = new ConfigWindow(this, wsService, OverlayWindow, TimelineOverlayWindow, DamageMeterOverlayWindow, relayService, browserService, ttsService);

            // AddWindow throws if a window with the same name already exists.
            // Guard each add so a partially-initialised state can't wedge the
            // plugin into a retry loop.
            AddWindowSafely(ConfigWindow);
            AddWindowSafely(OverlayWindow);
            AddWindowSafely(TimelineOverlayWindow);
            AddWindowSafely(DamageMeterOverlayWindow);

            // All overlays should always remain visible.
            OverlayWindow.IsOpen = true;
            TimelineOverlayWindow.IsOpen = true;
            DamageMeterOverlayWindow.IsOpen = true;


            // Cache local player name for personal DPS lookup
            localPlayerName = PlayerState.CharacterName;

            // Re-cache player name on login (character switch)
            ClientState.Login += OnLogin;

            // Register DTR (server info bar) entries.
            // These may already be registered by a previous plugin instance
            // that wasn't disposed yet; DtrBar.Get throws in that case. Log a
            // warning and continue rather than aborting initialisation (which
            // previously caused an infinite per-frame retry loop).
            try
            {
                partyDpsEntry = DtrBar.Get("CactBridge-PartyDPS", "0");
                partyDpsEntry.Shown = false;
            }
            catch (Exception ex)
            {
                Log.Warning($"[CactBridge] Could not register Party DPS DTR entry: {ex.Message}");
            }

            try
            {
                personalDpsEntry = DtrBar.Get("CactBridge-PersonalDPS", "0");
                personalDpsEntry.Shown = false;
            }
            catch (Exception ex)
            {
                Log.Warning($"[CactBridge] Could not register Personal DPS DTR entry: {ex.Message}");
            }

            Log.Information("[CactBridge] Services initialised.");
        }
        catch (Exception ex)
        {
            // Log the failure once. Do NOT clear _initialized — retrying would
            // create duplicate services and restart the browser in a loop.
            Log.Error($"[CactBridge] Initialisation failed: {ex}");
        }
    }

    /// <summary>Adds a window to the window system without throwing if a
    /// window with the same name is already present.</summary>
    private void AddWindowSafely(Window window)
    {
        try
        {
            WindowSystem.AddWindow(window);
        }
        catch (Exception ex)
        {
            Log.Warning($"[CactBridge] Could not add window \"{window.WindowName}\": {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Browser installation announcements
    // -----------------------------------------------------------------------

    private BrowserService.BrowserState lastAnnouncedState = BrowserService.BrowserState.Idle;

    private void OnBrowserStateChanged(BrowserService.BrowserState state)
    {
        // Prevent duplicate notifications for the same state
        if (lastAnnouncedState == state) return;
        lastAnnouncedState = state;

        // StateChanged fires from the browser service's background thread,
        // but Dalamud notifications must be posted on the framework thread.
        Framework.RunOnFrameworkThread(() =>
        {
            switch (state)
            {
                case BrowserService.BrowserState.Downloading:
                    NotificationManager.AddNotification(new Notification
                    {
                        Content = "Installing: Browser...",
                        Title   = "CactBridge",
                        Type    = NotificationType.Info
                    });
                    break;
                case BrowserService.BrowserState.Launching:
                    NotificationManager.AddNotification(new Notification
                    {
                        Content = "Loading Browser...",
                        Title   = "CactBridge",
                        Type    = NotificationType.Info
                    });
                    break;
                case BrowserService.BrowserState.Running:
                    NotificationManager.AddNotification(new Notification
                    {
                        Content = "CactBridge is ready!",
                        Title   = "CactBridge",
                        Type    = NotificationType.Success
                    });
                    break;
                case BrowserService.BrowserState.Error:
                    var errMsg = string.IsNullOrEmpty(browserService?.LastError)
                        ? "Browser failed to start — check /xllog for details."
                        : $"Browser error: {browserService.LastError}";
                    NotificationManager.AddNotification(new Notification
                    {
                        Content = errMsg,
                        Title   = "CactBridge",
                        Type    = NotificationType.Error
                    });
                    break;
            }
        });
    }

    // -----------------------------------------------------------------------
    // Disposal - unregister everything to prevent leaks on reload
    // -----------------------------------------------------------------------
    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw        -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUi;
        Framework.Update                       -= OnFrameworkUpdate;

        if (_initialized)
        {
            // Unsubscribe from browser state changes
            if (browserService != null)
                browserService.StateChanged -= OnBrowserStateChanged;

            WindowSystem.RemoveAllWindows();
            ConfigWindow?.Dispose();
            OverlayWindow?.Dispose();
            TimelineOverlayWindow?.Dispose();
            DamageMeterOverlayWindow?.Dispose();


            // Unsubscribe from events
            ClientState.Login -= OnLogin;

            // Remove DTR entries from the server info bar
            if (partyDpsEntry != null)
            {
                DtrBar.Remove("CactBridge-PartyDPS");
                partyDpsEntry = null;
            }
            if (personalDpsEntry != null)
            {
                DtrBar.Remove("CactBridge-PersonalDPS");
                personalDpsEntry = null;
            }

            // Dispose services gracefully
            wsService?.Dispose();
            relayService?.Dispose();
            browserService?.Dispose();
            ttsService?.Dispose();
        }

        CommandManager.RemoveHandler(CommandName);

        Log.Information("[CactBridge] Plugin unloaded.");
    }

    // -----------------------------------------------------------------------
    // Command handler
    // -----------------------------------------------------------------------
    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("move", System.StringComparison.OrdinalIgnoreCase))
            ToggleMainUi();
        else
            ToggleConfigUi();
    }

    // -----------------------------------------------------------------------
    // UI toggle helpers (also wired to plugin-installer buttons)
    // -----------------------------------------------------------------------
    public void ToggleConfigUi() => ConfigWindow?.Toggle();

    public bool IsConfigUiOpen => ConfigWindow?.IsOpen ?? false;

    public void ToggleMainUi()
    {
        OverlayWindow?.ToggleMoveMode();
        if (OverlayWindow != null)
            OverlayWindow.IsOpen = true;
    }

    // -----------------------------------------------------------------------
    // Login handler - re-cache player name on character switch
    // -----------------------------------------------------------------------
    private void OnLogin()
    {
        localPlayerName = PlayerState.CharacterName;
        
        // Re-subscribe to OverlayPlugin events after login.
        // When you log out, ACT/OverlayPlugin may stop sending BroadcastMessage
        // events. This ensures they resume when you log back in.
        wsService?.RefreshSubscription();
    }

    // -----------------------------------------------------------------------
    // Framework update - wait for IINACT, then drain queues + update DTR
    // -----------------------------------------------------------------------
    private void OnFrameworkUpdate(IFramework framework)
    {
        // --- Deferred initialisation: wait until IINACT is loaded and -------
        // --- the player has logged in with a character. --------------------
        if (!_initialized)
        {
            var iinactLoaded = PluginInterface.InstalledPlugins
                .Any(p => p.InternalName == "IINACT" && p.IsLoaded);

            if (!iinactLoaded)
                return; // keep waiting

            if (!ClientState.IsLoggedIn || ObjectTable.LocalPlayer == null)
                return; // wait for character login

            InitializeServices();
        }

        // --- Regular per-frame work (only when initialised) ----------------
        var ws = wsService;
        if (ws == null) return;

        // Drain chat announcement queue
        while (ws.TryDequeueChat(out var msg))
            ChatGui.Print(new Dalamud.Game.Text.XivChatEntry
            {
                Type    = Dalamud.Game.Text.XivChatType.Notice,
                Message = msg
            });

        // Drain game alert queue — shows native FFXIV error toasts (red alerts with timer bars)
        while (ws.TryDequeueGameAlert(out var gameAlert))
            ToastGui.ShowError(gameAlert.text);

        // Drain center alert queue — shows native FFXIV center-screen gimmick
        // hints with countdown timer bars (the game's "tells you what's going
        // on" announcements, e.g. Limit Break)
        while (ws.TryDequeueCenterAlert(out var centerAlert))
            ShowCenterAlert(centerAlert.text, centerAlert.type, centerAlert.duration);

        // Drain toast queue — fires real FFXIV toasts when in Toast style
        while (ws.TryDequeueToast(out var toastMsg))
            ToastGui.ShowQuest(toastMsg);

        // Update server info bar entries
        var cfg = Configuration;

        // Party DPS
        if (cfg.ShowPartyDpsInBar && partyDpsEntry != null)
        {
            var enc = ws.GetEncounter();
            if (enc != null)
            {
                partyDpsEntry.Text = $"encDPS: {enc.DPS:F0}";
                partyDpsEntry.Shown = true;
            }
            else
            {
                partyDpsEntry.Shown = false;
            }
        }
        else if (partyDpsEntry != null)
        {
            partyDpsEntry.Shown = false;
        }

        // Personal DPS — only use the local player's own DPS (no fallback)
        if (cfg.ShowPersonalDpsInBar && personalDpsEntry != null)
        {
            double dpsValue = 0;

            if (localPlayerName != null)
            {
                var player = ws.GetPlayerCombatant(localPlayerName);
                if (player != null)
                    dpsValue = player.DPS;
            }

            if (dpsValue > 0)
            {
                personalDpsEntry.Text = $"DPS: {dpsValue:F0}";
                personalDpsEntry.Shown = true;
            }
            else
            {
                personalDpsEntry.Shown = false;
            }
        }
        else if (personalDpsEntry != null)
        {
            personalDpsEntry.Shown = false;
        }
    }

    // -----------------------------------------------------------------------
    // Center alert display — native FFXIV "gimmick hint" (the center-screen
    // announcement with a countdown timer bar the game uses to tell players
    // what's going on, e.g. Limit Break / duty mechanic warnings).
    // Must run on the game's main thread (called from OnFrameworkUpdate).
    // -----------------------------------------------------------------------
    private void ShowCenterAlert(string text, AlertType type, float duration)
    {
        // Alarm/Alert callouts are the important ones — show them with the red
        // Warning styling. Info-level hints use the neutral Info styling.
        var style = type == AlertType.Alarm || type == AlertType.Alert
            ? RaptureAtkModule.TextGimmickHintStyle.Warning
            : RaptureAtkModule.TextGimmickHintStyle.Info;

        var fallback = Configuration.CenterAlertFallbackDuration > 0f
            ? Configuration.CenterAlertFallbackDuration
            : 5f;
        var seconds = (int)System.Math.Clamp(duration > 0f ? duration : fallback, 1f, 60f);

        unsafe
        {
            var module = RaptureAtkModule.Instance();
            if (module != null)
                module->ShowTextGimmickHint(text, style, seconds);
        }
    }
}

