using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Game.Gui.Dtr;
using CactBridge.Services;
using System.Diagnostics;
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


    // /cactbridge       - open settings
    // /cactbridge move  - toggle move mode
    private const string CommandName = "/cactbridge";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("CactBridge");

    // -----------------------------------------------------------------------
    // Plugin-owned objects
    // -----------------------------------------------------------------------
    private readonly WebSocketService       wsService;
    private readonly RelayHttpService       relayService;
    private readonly BrowserService        browserService;
    private readonly TtsService            ttsService;
    private          ConfigWindow              ConfigWindow  { get; init; }
    private          OverlayWindow             OverlayWindow { get; init; }
    private          TimelineOverlayWindow     TimelineOverlayWindow { get; init; }
    private          DamageMeterOverlayWindow  DamageMeterOverlayWindow { get; init; }


    // DTR (server info bar) entries
    private IDtrBarEntry? partyDpsEntry;
    private IDtrBarEntry? personalDpsEntry;
    private string?      localPlayerName;

    // -----------------------------------------------------------------------
    // Constructor
    // -----------------------------------------------------------------------
    public Plugin()
    {
        // Load or create configuration from Dalamud's config storage
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Start the WebSocket service - connects and listens on a background Task
        wsService = new WebSocketService(Log, Configuration);

        // Start the relay HTTP server - serves raidboss-user.js to Cactbot
        var pluginDir = PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;
        relayService   = new RelayHttpService(Log, pluginDir);

        // Start the TTS service - speaks alerts aloud via Windows SAPI
        // Volume is automatically synced to the game's Voice sound channel.
        ttsService = new TtsService(Log, Configuration, GameConfig);

        // Launch headless browser with both alerts and timeline pages
        browserService = new BrowserService(Log, pluginDir, relayService.OverlayUrl, relayService.TimelineOverlayUrl);

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

        // Installation announcements — show progress in the chat announcement channel.
        // 1. Browser download starting
        wsService.EnqueueChatMessage("Installing: Browser...");

        // 2. Subscribe to browser state changes for subsequent steps
        browserService.StateChanged += OnBrowserStateChanged;

        // Create windows - OverlayWindow must exist before ConfigWindow
        // so ConfigWindow can hold a reference to it
        OverlayWindow             = new OverlayWindow(this, wsService);
        TimelineOverlayWindow     = new TimelineOverlayWindow(this, wsService);
        DamageMeterOverlayWindow  = new DamageMeterOverlayWindow(this, wsService);
        ConfigWindow              = new ConfigWindow(this, wsService, OverlayWindow, TimelineOverlayWindow, DamageMeterOverlayWindow, relayService, browserService, ttsService);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(OverlayWindow);
        WindowSystem.AddWindow(TimelineOverlayWindow);
        WindowSystem.AddWindow(DamageMeterOverlayWindow);


        // All overlays should always remain visible.
        OverlayWindow.IsOpen = true;
        TimelineOverlayWindow.IsOpen = true;
        DamageMeterOverlayWindow.IsOpen = true;


        // Cache local player name for personal DPS lookup
        localPlayerName = PlayerState.CharacterName;

        // Re-cache player name on login (character switch)
        ClientState.Login += OnLogin;

        // Register DTR (server info bar) entries
        partyDpsEntry = DtrBar.Get("CactBridge-PartyDPS", "0");
        partyDpsEntry.Shown = false;

        personalDpsEntry = DtrBar.Get("CactBridge-PersonalDPS", "0");
        personalDpsEntry.Shown = false;

        // Register slash command
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open CactBridge settings. '/cactbridge move' toggles move mode for the overlay."
        });

        // Hook into Dalamud's UI draw pipeline
        PluginInterface.UiBuilder.Draw        += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   += ToggleMainUi;
        Framework.Update                       += OnFrameworkUpdate;

        Log.Information("[CactBridge] Plugin loaded.");
    }

    // -----------------------------------------------------------------------
    // Browser installation announcements
    // -----------------------------------------------------------------------

    private void OnBrowserStateChanged(BrowserService.BrowserState state)
    {
        switch (state)
        {
            case BrowserService.BrowserState.Launching:
                wsService.EnqueueChatMessage("Installing: Puppet...");
                break;
            case BrowserService.BrowserState.Running:
                wsService.EnqueueChatMessage("Plugin Loaded!");
                break;
        }
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

        // Unsubscribe from browser state changes
        browserService.StateChanged -= OnBrowserStateChanged;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        OverlayWindow.Dispose();
        TimelineOverlayWindow.Dispose();
        DamageMeterOverlayWindow.Dispose();


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

        // Cancels the background task and closes the WebSocket gracefully
        wsService.Dispose();
        relayService.Dispose();
        browserService.Dispose();
        ttsService.Dispose();

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
    public void ToggleConfigUi() => ConfigWindow.Toggle();

    public bool IsConfigUiOpen => ConfigWindow.IsOpen;

    public void ToggleMainUi()
    {
        OverlayWindow.ToggleMoveMode();
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
        wsService.RefreshSubscription();
    }

    // -----------------------------------------------------------------------
    // Framework update - drain chat queue + update DTR entries
    // -----------------------------------------------------------------------
    private void OnFrameworkUpdate(IFramework framework)
    {
        // Drain chat announcement queue
        while (wsService.TryDequeueChat(out var msg))
            ChatGui.Print(new Dalamud.Game.Text.XivChatEntry
            {
                Type    = Dalamud.Game.Text.XivChatType.Notice,
                Message = msg
            });

        // Drain toast queue — fires real FFXIV toasts when in Toast style
        while (wsService.TryDequeueToast(out var toastMsg))
            ToastGui.ShowQuest(toastMsg);

        // Update server info bar entries
        var cfg = Configuration;

        // Party DPS
        if (cfg.ShowPartyDpsInBar && partyDpsEntry != null)
        {
            var enc = wsService.GetEncounter();
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
                var player = wsService.GetPlayerCombatant(localPlayerName);
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
}

