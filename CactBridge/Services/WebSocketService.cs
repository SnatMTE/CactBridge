using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using CactBridge.Models;

namespace CactBridge.Services;

/// <summary>
/// Manages a persistent, auto-reconnecting WebSocket connection to the
/// OverlayPlugin endpoint at <c>ws://127.0.0.1:10501/ws</c>.
///
/// On connect the service subscribes to:
///   - <c>onBroadcastMessage</c>  - processed raidboss trigger alerts from cactbot
///   - <c>ChangeZone</c>          - zone transitions for status display
///   - <c>onInCombat</c>          - combat state changes
///
/// All network I/O runs on a background <see cref="Task"/>; the public API is
/// thread-safe and may be called from the ImGui draw loop without blocking.
/// </summary>
public sealed class WebSocketService : IDisposable
{
    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    // Match the proven WebSocket URL used by the working web overlay (overlay.js)
    // at https://snatmte.github.io/FFXIV-Overlay/. The overlay's autoConnect
    // tries root first, then /ws, /socket, etc. The working connection uses /ws.
    private const string WsUrl                 = "ws://127.0.0.1:10501/ws";
    private const int    MaxStoredAlerts       = 20;
    private const int    MaxStoredTimelineEntries = 50;
    private const int    ReconnectDelayMs      = 5_000;

    // Subscribed events - must match exactly what the web overlay (overlay.js)
    // uses.  The overlay only subscribes to LogLine and CombatData; including
    // additional events (e.g. "ChangeZone") may cause some OverlayPlugin builds
    // to reject the entire subscribe message.
    private static readonly string[] SubscribedEvents =
    [
        "LogLine",
        "CombatData"
    ];

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    private readonly IPluginLog          log;
    private readonly Configuration       config;
    private readonly object              alertLock   = new();
    private readonly List<CactbotAlert>  alerts      = new();
    private readonly object              timelineLock      = new();
    private readonly List<TimelineEntry> timelineEntries   = new();
    private readonly object              combatLock        = new();
    private EncounterInfo?               currentEncounter;
    private List<CombatantInfo>          combatants        = new();
    private bool                         wasFighting; // Tracks previous IsFighting state to detect combat end
    private readonly CancellationTokenSource cts         = new();
    private readonly ConcurrentQueue<string> chatQueue   = new();
    private readonly ConcurrentQueue<string> toastQueue  = new();
    private readonly ConcurrentQueue<(string text, AlertType type)> gameAlertQueue = new();
    private readonly ConcurrentQueue<(string text, AlertType type, float duration)> gimmickHintQueue = new();
    private readonly ConcurrentQueue<(string text, AlertType type, float duration)> battleTalkQueue = new();
    private readonly System.Collections.Generic.HashSet<string> seenTypes = new();
    private int              logLineCount;
    private int              rawMessageCount;     // Total messages received since connect
    private const int        VerboseLogLimit = 10; // Log first N raw messages in full

    private ClientWebSocket? socket;
    private bool             disposed;
    private readonly SemaphoreSlim subscribeLock = new(1, 1);

    // -----------------------------------------------------------------------
    // Public events / properties (safe to read from any thread)
    // -----------------------------------------------------------------------

    /// <summary>Fired for every raw ACT log line received (including lines this plugin ignores).</summary>
    public Action<string>? OnRawLogLine { get; set; }

    /// <summary>Fired when a zone change event is received.</summary>
    public Action<int, string>? OnZoneChanged { get; set; }

    /// <summary>
    /// Fired when an alert is enqueued that could be spoken via TTS.
    /// Subscribers (e.g. <see cref="TtsService"/>) can inspect the alert
    /// text and type and decide whether to speak.
    /// </summary>
    public Action<string, AlertType>? OnAlertForTts { get; set; }

    /// <summary>True when the WebSocket is in the <see cref="WebSocketState.Open"/> state.</summary>
    public bool IsConnected => socket?.State == WebSocketState.Open;

    /// <summary>Name of the current FFXIV zone, updated on each ChangeZone event.</summary>
    public string CurrentZone { get; private set; } = string.Empty;

    /// <summary>Total number of <c>LogLine</c> WebSocket events received since connecting. Use this to verify data is flowing.</summary>
    public int LogLineCount => logLineCount;

    /// <summary>Total number of raw WebSocket messages received since connecting. Useful for diagnostics.</summary>
    public int RawMessageCount => rawMessageCount;

    // -----------------------------------------------------------------------
    // Constructor / startup
    // -----------------------------------------------------------------------

    public WebSocketService(IPluginLog log, Configuration config)
    {
        this.log    = log;
        this.config = config;
        // Fire-and-forget; run at BelowNormal priority so the game always
        // gets CPU time first when the CPU is saturated.
        _ = Task.Run(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            Thread.CurrentThread.IsBackground = true;
            RunLoopAsync(cts.Token);
        });
    }

    // -----------------------------------------------------------------------
    // Public API (UI thread)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Re-sends subscription and start commands to ACT/OverlayPlugin.
    /// Call this after login or when events stop flowing (e.g. after logout/login cycle).
    /// Thread-safe and non-blocking.
    /// </summary>
    public void RefreshSubscription()
    {
        _ = Task.Run(async () =>
        {
            if (disposed || socket == null || socket.State != WebSocketState.Open)
                return;

            // Prevent concurrent subscription attempts
            if (!await subscribeLock.WaitAsync(0))
                return;

            try
            {
                log.Information("[CactBridge] Refreshing subscription to OverlayPlugin events...");
                await SendSubscriptionAsync(socket, cts.Token);
                log.Information("[CactBridge] Subscription refreshed.");
            }
            catch (Exception ex)
            {
                log.Warning($"[CactBridge] Failed to refresh subscription: {ex.Message}");
            }
            finally
            {
                subscribeLock.Release();
            }
        });
    }

    /// <summary>
    /// Returns a point-in-time snapshot of active (non-expired) alerts trimmed
    /// to <paramref name="maxCount"/> most-recent entries. Expired alerts are
    /// pruned from the internal list during this call. Thread-safe.
    /// </summary>
    public List<CactbotAlert> GetActiveAlerts(int maxCount)
    {
        lock (alertLock)
        {
            // Prune expired entries while we hold the lock
            alerts.RemoveAll(a => a.IsExpired);

            var start = Math.Max(0, alerts.Count - maxCount);
            return alerts.GetRange(start, alerts.Count - start);
        }
    }

    /// <summary>
    /// Returns a point-in-time snapshot of non-expired timeline entries,
    /// ordered by time remaining (closest first), trimmed to <paramref name="maxCount"/>.
    /// Thread-safe.
    /// </summary>
    public List<TimelineEntry> GetTimelineEntries(int maxCount, float lookAheadSeconds = 30f)
    {
        lock (timelineLock)
        {
            // Prune expired and far-future entries
            var cutoff = DateTime.UtcNow.AddSeconds(lookAheadSeconds);
            timelineEntries.RemoveAll(e => e.IsExpired || e.ReceivedAt > cutoff);

            // Sort by time remaining (soonest first)
            timelineEntries.Sort((a, b) => a.TimeRemaining.CompareTo(b.TimeRemaining));

            return timelineEntries.Count > maxCount
                ? timelineEntries.GetRange(0, maxCount)
                : new List<TimelineEntry>(timelineEntries);
        }
    }

    // -----------------------------------------------------------------------
    // Background reconnect loop
    // -----------------------------------------------------------------------

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Warning($"[CactBridge] WebSocket error: {ex.Message}");
            }

            if (!token.IsCancellationRequested)
            {
                log.Debug($"[CactBridge] Reconnecting in {ReconnectDelayMs / 1000}s…");
                try { await Task.Delay(ReconnectDelayMs, token); }
                catch (OperationCanceledException) { break; }
            }
        }

        log.Information("[CactBridge] WebSocket loop exited.");
    }

    // -----------------------------------------------------------------------
    // Subscription helper
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sends subscribe and start commands to ACT/OverlayPlugin.
    /// Some OverlayPlugin / ACT WS servers require an explicit "start"
    /// after subscribing to begin forwarding CombatData events.
    /// </summary>
    private async Task SendSubscriptionAsync(ClientWebSocket ws, CancellationToken token)
    {
        const int maxAttempts = 3;
        
        for (int attempt = 1; attempt <= maxAttempts && !token.IsCancellationRequested; attempt++)
        {
            if (attempt > 1)
            {
                // Small delay before retry
                try { await Task.Delay(300, token); } catch (OperationCanceledException) { break; }
            }

            var subJson  = JsonSerializer.Serialize(new SubscribeRequest { Events = SubscribedEvents });
            var subBytes = Encoding.UTF8.GetBytes(subJson);
            await ws.SendAsync(
                new ArraySegment<byte>(subBytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken: token);

            var startMsg = """{"call":"start"}""";
            var startBytes = Encoding.UTF8.GetBytes(startMsg);
            await ws.SendAsync(
                new ArraySegment<byte>(startBytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken: token);
        }
        
        log.Information("[CactBridge] Subscribed and sent start.");
    }

    // -----------------------------------------------------------------------
    // Connect → subscribe → receive loop
    // -----------------------------------------------------------------------

    /// <summary>
    /// Opens the WebSocket, sends a subscription message, then reads frames
    /// in a loop until the connection closes or the token is cancelled.
    /// </summary>
    private async Task ConnectAndReceiveAsync(CancellationToken token)
    {
        // Dispose any previous socket before creating a fresh one
        socket?.Dispose();
        socket = new ClientWebSocket();

        log.Information($"[CactBridge] Connecting to {WsUrl}…");
        await socket.ConnectAsync(new Uri(WsUrl), token);
        log.Information("[CactBridge] Connected to OverlayPlugin WebSocket.");

        // Send subscription and start commands
        await SendSubscriptionAsync(socket, token);

        // ------------------------------------------------------------------
        // Receive loop - reassemble fragmented frames before processing
        // ------------------------------------------------------------------
        var buffer         = new byte[16 * 1024]; // 16 KB per frame
        var messageBuilder = new StringBuilder();

        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            messageBuilder.Clear();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    log.Information("[CactBridge] Server requested connection close.");
                    // Respond with a close frame and exit
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Server closed",
                        CancellationToken.None);
                    return;
                }

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            var raw = messageBuilder.ToString();
            HandleMessage(raw);
        }
    }

    // -----------------------------------------------------------------------
    // Message handling
    // -----------------------------------------------------------------------

    /// <summary>
    /// Top-level dispatcher: deserialises the JSON envelope and routes to the
    /// appropriate handler based on the <c>type</c> field.
    /// </summary>
    private void HandleMessage(string json)
    {
        // Verbose logging of the first N messages to help diagnose format issues
        var msgNum = Interlocked.Increment(ref rawMessageCount);
        if (msgNum <= VerboseLogLimit)
        {
            var preview = json.Length > 300 ? json[..300] + "..." : json;
            log.Information($"[CactBridge] Raw message #{msgNum}: {preview}");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // CRITICAL: Check for combat data FIRST, before any type-based routing.
            // This matches overlay.js behaviour — findCombatantsArray is called before
            // any type/call/event field inspection. This ensures we catch CombatData
            // regardless of what wrapper format OverlayPlugin uses.
            // Check at root level and one level deep (data/msg wrappers).
            if (LooksLikeCombatData(root))
            {
                HandleCombatData(root);
                return;
            }
            foreach (var wrapperKey in new[] { "data", "msg", "CombatData" })
            {
                if (root.TryGetProperty(wrapperKey, out var inner) &&
                    (inner.ValueKind == JsonValueKind.Object || inner.ValueKind == JsonValueKind.Array))
                {
                    if (LooksLikeCombatData(inner))
                    {
                        HandleCombatData(inner);
                        return;
                    }
                }
            }
            // Also try recursive search for deeply nested combatants
            if (FindCombatantsAnywhere(root, out var found))
            {
                log.Debug("[CactBridge] Found combatants in nested structure (pre-type check).");
                HandleCombatData(found);
                return;
            }

            // Determine event type: prefer "type" field, fall back to "call" field,
            // then try "event" field (common in some OverlayPlugin builds).
            string? type = null;
            if (root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
                type = typeElement.GetString();
            else if (root.TryGetProperty("call", out var callElement) && callElement.ValueKind == JsonValueKind.String)
                type = callElement.GetString();
            else if (root.TryGetProperty("event", out var eventElement) && eventElement.ValueKind == JsonValueKind.String)
                type = eventElement.GetString();

            if (string.IsNullOrEmpty(type))
            {
                // No identifiable type field — the pre-type check above already
                // looked for combat data. If we're here, it's truly unknown.
                // Log first occurrence of each unrecognised message shape.
                var preview = json.Length > 200 ? json[..200] + "..." : json;
                if (seenTypes.Add(preview))
                    log.Information($"[CactBridge] Unrecognised message (no type field): {preview}");
                return;
            }

            // Log each new message type at INFO level once so user can see what's flowing
            if (seenTypes.Add(type))
                log.Information($"[CactBridge] First message of type: {type}");

            switch (type)
            {
                case "BroadcastMessage":
                case "onBroadcastMessage":
                    HandleBroadcast(root);
                    break;

                case "ChangeZone":
                case "onZoneChangedEvent":
                    var zoneId = 0;
                    var zoneName = string.Empty;
                    if (root.TryGetProperty("zoneID", out var zoneIdProp))
                        zoneId = zoneIdProp.GetInt32();
                    if (root.TryGetProperty("zoneName", out var zoneNameProp) && zoneNameProp.ValueKind == JsonValueKind.String)
                        zoneName = zoneNameProp.GetString() ?? string.Empty;
                    CurrentZone = !string.IsNullOrEmpty(zoneName) ? zoneName : $"Zone {zoneId}";
                    log.Information($"[CactBridge] Zone changed: {CurrentZone} (ID={zoneId})");
                    // Clear all state when entering a new zone
                    ClearAlerts();
                    ClearTimelineEntries();
                    ResetCombatState();
                    wasFighting = false;
                    OnZoneChanged?.Invoke(zoneId, zoneName);
                    
                    // Refresh subscription after a brief delay to ensure ACT/OverlayPlugin
                    // resumes sending events after the zone transition.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000); // Wait 1 second for zone transition to complete
                        RefreshSubscription();
                    });
                    break;

                case "LogLine":
                    Interlocked.Increment(ref logLineCount);
                    // OverlayPlugin sends "rawLine" as the pipe-delimited string;
                    // "line" is a JSON array and cannot be used as a string directly.
                    if (root.TryGetProperty("rawLine", out var rawLineEl) && rawLineEl.ValueKind == JsonValueKind.String)
                        HandleActLogLine(rawLineEl.GetString() ?? string.Empty);
                    else if (root.TryGetProperty("line", out var lineArrEl) && lineArrEl.ValueKind == JsonValueKind.Array)
                    {
                        var parts = new System.Collections.Generic.List<string>();
                        foreach (var el in lineArrEl.EnumerateArray())
                            parts.Add(el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString());
                        HandleActLogLine(string.Join("|", parts));
                    }
                    break;

                case "ImportedLogLines":
                    // Cactbot test mode injects synthetic log lines through this event.
                    // Field is "logLines" (string array of raw pipe-delimited ACT log lines).
                    if (root.TryGetProperty("logLines", out var importedLines) && importedLines.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in importedLines.EnumerateArray())
                        {
                            if (entry.ValueKind == JsonValueKind.String)
                            {
                                var importedLine = entry.GetString() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(importedLine))
                                {
                                    Interlocked.Increment(ref logLineCount);
                                    HandleActLogLine(importedLine);
                                }
                            }
                        }
                    }
                    break;

                case "onLogEvent":
                    if (root.TryGetProperty("detail", out var detail) && detail.TryGetProperty("logs", out var logs)
                        && logs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in logs.EnumerateArray())
                            if (entry.ValueKind == JsonValueKind.String)
                                HandleActLogLine(entry.GetString() ?? string.Empty);
                    }
                    break;

                case "CombatData":
                    // Content may be at root level or nested inside "data" / "msg".
                    if (LooksLikeCombatData(root))
                    {
                        HandleCombatData(root);
                    }
                    else
                    {
                        var unwrapped = false;
                        foreach (var wrapperKey in new[] { "data", "msg" })
                        {
                            if (root.TryGetProperty(wrapperKey, out var inner) && inner.ValueKind == JsonValueKind.Object
                                && LooksLikeCombatData(inner))
                            {
                                HandleCombatData(inner);
                                unwrapped = true;
                                break;
                            }
                        }
                        if (!unwrapped)
                            HandleCombatData(root);
                    }
                    break;

                // OverlayPlugin sometimes wraps CombatData inside a "send" or "broadcast"
                // envelope with the actual type in msgtype / msgType / msg_type.
                case "send":
                case "broadcast":
                    HandleWrappedCombatData(root);
                    break;
            }
        }
        catch (JsonException ex)
        {
            log.Verbose($"[CactBridge] JSON parse failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            log.Warning($"[CactBridge] Unexpected error in HandleMessage: {ex.Message}");
        }
    }

    /// <summary>
    /// Processes an <c>onBroadcastMessage</c> event.
    ///
    /// Cactbot's raidboss overlay broadcasts processed trigger alerts here with
    /// the payload shape: <c>{ type: "alarm"|"alert"|"info", text: "…", duration?: number }</c>
    ///
    /// This is the primary data source for on-screen alerts.
    /// </summary>
    // -----------------------------------------------------------------------
    // ACT LogLine parsing
    // -----------------------------------------------------------------------

    // ACT network log line format: "typeCode|timestamp|field1|field2|..."
    // Type codes are decimal integers.
    // Common codes:
    //   20  = NetworkStartsCasting
    //   25  = NetworkDeath
    //   27  = NetworkTargetIcon (headmarker)
    //   33  = NetworkTether
    //   268 = Countdown (OverlayPlugin synthetic)
    //   269 = CountdownCancel (OverlayPlugin synthetic)
    private void HandleActLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // Forward raw log line to subscribers (e.g. headless browser timeline)
        OnRawLogLine?.Invoke(line);

        var parts = line.Split('|');
        if (parts.Length < 2) return;
        if (!int.TryParse(parts[0], out var typeCode)) return;

        switch (typeCode)
        {
            case 20: // NetworkStartsCasting: raw cast info — suppressed; processed alerts come via BroadcastMessage
                break;

            case 27: // NetworkTargetIcon (headmarker): suppressed; processed alerts come via BroadcastMessage
                break;

            case 33: // NetworkTether: suppressed; processed alerts come via BroadcastMessage
                break;

            case 268: // Countdown: [268|ts|playerId|worldId|countdownTime|result|playerName|...]
                // result: "0" = countdown started, non-zero = engage
                if (parts.Length >= 5 && int.TryParse(parts[4], out var countdownSecs))
                {
                    var alertType = countdownSecs <= 3 ? AlertType.Alarm
                        : countdownSecs <= 5 ? AlertType.Alert
                        : AlertType.Info;
                    var endTime = DateTime.UtcNow.AddSeconds(countdownSecs);
                    EnqueueAlert(new CactbotAlert
                    {
                        Text = $"Engage in {countdownSecs}s!",
                        Type = alertType,
                        Duration = (float)countdownSecs + 2f,
                        ReceivedAt = DateTime.UtcNow,
                        CountdownEndTime = endTime
                    });
                }
                break;

            case 269: // CountdownCancel: [269|ts|playerId|worldId|playerName|...]
                EnqueueAlert("Countdown canceled", AlertType.Info, 3f);
                break;
        }
    }

    private void HandleBroadcast(JsonElement root)
    {
        if (!root.TryGetProperty("msg", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return;

        // Check for timeline-type messages first
        if (TryGetString(payload, "type", out var typeStr))
        {
            var typeLower = typeStr.ToLowerInvariant();
            if (typeLower is "timeline" or "timelineentry" or "timeline-entry")
            {
                HandleTimelineEntry(payload);
                return;
            }
        }

        // Also detect timeline entries by the "time" field
        if (payload.TryGetProperty("time", out var timeProp) && timeProp.ValueKind == JsonValueKind.Number)
        {
            HandleTimelineEntry(payload);
            return;
        }

        // Common raidboss shape: { type: "alarm"|"alert"|"info", text: "...", duration?: n }
        // Also handles: { alarmText: "...", alertText: "...", infoText: "..." }
        // Only process ONE alert per message to avoid duplicates.

        // Try the standard "text" field first
        if (TryGetString(payload, "text", out var baseText) && !string.IsNullOrWhiteSpace(baseText))
        {
            var rawType = TryGetString(payload, "type", out var typeText) ? typeText : "info";
            var alertType = ParseAlertType(rawType);
            var duration = TryGetFloat(payload, "duration", out var parsedDuration)
                ? parsedDuration
                : DefaultDuration(alertType);

            EnqueueAlert(baseText.Trim(), alertType, duration);
            return; // Done - don't also process the named fields
        }

        // Fall back to the named text fields if no "text" field present
        AddNamedAlert(payload, "alarmText", AlertType.Alarm, 5f);
        AddNamedAlert(payload, "alertText", AlertType.Alert, 4f);
        AddNamedAlert(payload, "infoText", AlertType.Info, 3f);
        AddNamedAlert(payload, "tts", AlertType.Info, 3f);
    }

    /// <summary>
    /// Processes a timeline entry from a BroadcastMessage.
    /// Expected shape: { type: "timeline", text: "Ability Name", time: 123.4, duration?: n }
    /// </summary>
    private void HandleTimelineEntry(JsonElement payload)
    {
        var text = TryGetString(payload, "text", out var t) ? t?.Trim() : null;
        if (string.IsNullOrWhiteSpace(text))
            return;

        var timeRemaining = TryGetFloat(payload, "time", out var timeVal)
            ? (double)timeVal
            : 0.0;

        lock (timelineLock)
        {
            // Avoid duplicates: if the same text already exists with close time, update it
            var existing = timelineEntries.Find(e =>
                e.Text.Equals(text, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(e.TimeRemaining - timeRemaining) < 2.0);

            if (existing != null)
            {
                existing.InitialTimeRemaining = timeRemaining;
                existing.ReceivedAt = DateTime.UtcNow;
            }
            else
            {
                timelineEntries.Add(new TimelineEntry
                {
                    Text = text,
                    InitialTimeRemaining = timeRemaining,
                    ReceivedAt = DateTime.UtcNow,
                });
            }

            // Bound the list
            if (timelineEntries.Count > MaxStoredTimelineEntries)
                timelineEntries.RemoveRange(0, timelineEntries.Count - MaxStoredTimelineEntries);
        }

        log.Debug($"[CactBridge] [Timeline] \"{text}\" in {timeRemaining:F1}s");
    }

    /// <summary>
    /// Process a broadcast from the headless browser page (received via the
    /// native PuppeteerSharp bridge, bypassing the OverlayPlugin WebSocket).
    /// The JSON string should match the shape produced by the relay JS:
    /// <c>{ type, text, time?, duration? }</c>.
    /// </summary>
    public void HandlePageBroadcast(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var payload = doc.RootElement;

            // Detect timeline entries by type prefix or "time" field
            if (TryGetString(payload, "type", out var typeStr))
            {
                var typeLower = typeStr.ToLowerInvariant();
                if (typeLower is "timeline" or "timelineentry" or "timeline-entry")
                {
                    HandleTimelineEntry(payload);
                    return;
                }
            }

            if (payload.TryGetProperty("time", out var timeProp) && timeProp.ValueKind == JsonValueKind.Number)
            {
                HandleTimelineEntry(payload);
                return;
            }

            // Standard alert shape: { type: "alarm"|"alert"|"info", text: "..." }
            if (TryGetString(payload, "text", out var baseText) && !string.IsNullOrWhiteSpace(baseText))
            {
                var rawType = TryGetString(payload, "type", out var typeText) ? typeText : "info";
                var alertType = ParseAlertType(rawType);
                var duration = TryGetFloat(payload, "duration", out var parsedDuration)
                    ? parsedDuration
                    : DefaultDuration(alertType);
                EnqueueAlert(baseText.Trim(), alertType, duration);
            }
        }
        catch (Exception ex)
        {
            log.Verbose($"[CactBridge] Page broadcast parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// Processes a <c>CombatData</c> event from OverlayPlugin / ACT.
    /// Expected shape:
    /// <code>
    /// { "type":"CombatData", "encounter":{...}, "combatants":[...] }
    /// </code>
    /// or OverlayPlugin's native format:
    /// <code>
    /// { "type":"CombatData", "Encounter":{...}, "Combatant":[...] }
    /// </code>
    /// </summary>
    private void HandleCombatData(JsonElement root)
    {
        try
        {
            // Step 0: Unwrap nested "CombatData" field if present (matching
            // overlay.js findCombatantsArray which does `if(data.CombatData) data = data.CombatData`).
            // Some OverlayPlugin builds send: { "type":"CombatData", "CombatData":{ Encounter:{...}, Combatant:[...] } }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("CombatData", out var combatDataField))
            {
                if (combatDataField.ValueKind == JsonValueKind.Object)
                {
                    log.Verbose("[CactBridge] Unwrapped nested CombatData field.");
                    root = combatDataField;
                }
            }

            EncounterInfo? encounter = null;
            var combatantsList = new List<CombatantInfo>();

            // Step 1: Try to extract encounter metadata (optional)
            // OverlayPlugin sends this under "Encounter" (uppercase) or "encounter" (lowercase).
            foreach (var key in new[] { "Encounter", "encounter" })
            {
                if (root.TryGetProperty(key, out var encEl) && encEl.ValueKind == JsonValueKind.Object)
                {
                    encounter = DeserializeCombatEncounter(encEl.GetRawText());
                    if (encounter != null) break;
                }
            }

            // Step 2: Extract combatants — handle multiple formats the way
            // the web overlay (overlay.js) does, matching its flexible approach.
            // OverlayPlugin can send combatants as:
            //   - "Combatant" / "Combatants" / "combatants" / "Players" / "Party"
            //   - As an ARRAY or an OBJECT (dictionary keyed by player name)
            //   - Or the root element itself is an array of combatant objects.
            var combatantCandidates = new[] {
                "Combatant", "combatant", "Combatants", "combatants",
                "Players", "players", "Party", "party",
                "Members", "members", "Member", "member",
                "Allies", "allies"
            };

            foreach (var key in combatantCandidates)
            {
                if (!root.TryGetProperty(key, out var comEl)) continue;

                if (comEl.ValueKind == JsonValueKind.Array)
                {
                    var parsed = DeserializeCombatants(comEl.GetRawText());
                    if (parsed != null && parsed.Count > 0)
                    {
                        combatantsList = parsed;
                        break;
                    }
                }
                else if (comEl.ValueKind == JsonValueKind.Object)
                {
                    // Object/dictionary — e.g. {"PlayerName": {...}, ...}
                    // Convert to array by serialising the values.
                    var entries = new List<CombatantInfo>();
                    foreach (var prop in comEl.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            var single = DeserializeCombatant(prop.Value.GetRawText());
                            if (single != null) entries.Add(single);
                        }
                    }
                    if (entries.Count > 0)
                    {
                        combatantsList = entries;
                        break;
                    }
                }
            }

            // Step 3: If no combatant keys found, the root itself might be an
            // array of combatant objects (rare but handled by overlay.js).
            if (combatantsList.Count == 0 && root.ValueKind == JsonValueKind.Array)
            {
                var parsed = DeserializeCombatants(root.GetRawText());
                if (parsed != null) combatantsList = parsed;
            }

            // Step 3b: Fallback — if we have an encounter but no combatants were
            // found at any known key, try scanning every top-level property on the
            // root object. Some OverlayPlugin builds embed combatant data under
            // unexpected key names or even directly as sibling properties of Encounter.
            if (combatantsList.Count == 0 && encounter != null && root.ValueKind == JsonValueKind.Object)
            {
                var knownNonCombatant = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "type", "call", "event", "source", "msg", "data",
                    "Encounter", "encounter", "EncounterData",
                    "CombatData", "isFighting", "Infight", "incombat",
                    "zoneID", "zoneName", "duration", "title", "DPS",
                    "damage", "Damage", "ENCDPS",
                };
                foreach (var prop in root.EnumerateObject())
                {
                    if (knownNonCombatant.Contains(prop.Name)) continue;
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        // Check if this looks like a single combatant (has Name/Job-like fields)
                        var ci = DeserializeCombatant(prop.Value.GetRawText());
                        if (ci != null && !string.IsNullOrEmpty(ci.Name))
                            combatantsList.Add(ci);
                    }
                }
                if (combatantsList.Count > 0)
                    log.Information($"[CactBridge] Found {combatantsList.Count} combatant(s) via property-scan fallback.");
            }

            // Step 4: Store results
            lock (combatLock)
            {
                if (encounter != null)
                    currentEncounter = encounter;
                if (combatantsList.Count > 0)
                    combatants = combatantsList;
            }

            // Step 5: Detect combat end — when IsFighting transitions from true to false,
            // clear alerts and timeline so stale callouts don't persist after a boss dies.
            if (encounter != null)
            {
                if (wasFighting && !encounter.IsFighting)
                {
                    log.Information($"[CactBridge] Combat ended for \"{encounter.Title}\" — clearing alerts and timeline.");
                    ClearAlerts();
                    ClearTimelineEntries();
                }
                wasFighting = encounter.IsFighting;

                log.Information($"[CactBridge] [CombatData] \"{encounter.Title}\" encDPS={encounter.DPS:F0} ({combatantsList.Count} combatants)");
            }
            else if (combatantsList.Count > 0)
                log.Information($"[CactBridge] [CombatData] {combatantsList.Count} combatants (no encounter info)");
            else
            {
                // Log available keys to help diagnose the actual message shape
                var keys = root.ValueKind == JsonValueKind.Object
                    ? string.Join(", ", root.EnumerateObject().Select(p => p.Name))
                    : root.ValueKind.ToString();
                log.Information($"[CactBridge] [CombatData] 0 combatants — keys: [{keys}]");
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[CactBridge] Failed to parse CombatData: {ex.Message}");
        }
    }

    /// <summary>
    /// Recursively searches a JsonElement tree for combatant-like arrays or objects,
    /// matching the approach used by overlay.js findCombatantsArray.
    /// Returns true and sets <paramref name="found"/> to the nearest ancestor that
    /// contains combatant data.
    /// </summary>
    private static bool FindCombatantsAnywhere(JsonElement el, out JsonElement found)
    {
        found = default;
        if (el.ValueKind == JsonValueKind.Array)
        {
            // An array of objects — could be a combatant list.
            if (el.GetArrayLength() > 0)
            {
                // Check if items look like combatants (have Name or DPS-like fields)
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var hint in new[] { "Name", "name", "Job", "job", "ENCDPS", "DPS", "Damage", "damage" })
                        {
                            if (item.TryGetProperty(hint, out _))
                            {
                                found = el;
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        if (el.ValueKind != JsonValueKind.Object) return false;

        // Check if this object has a known combatant-container key
        foreach (var key in new[] {
            "Combatant", "combatant", "Combatants", "combatants",
            "Players", "players", "Party", "party",
            "CombatantList", "CombatantsList",
            "Encounter", "encounter",
            "CombatData" })
        {
            if (el.TryGetProperty(key, out var val))
            {
                if (val.ValueKind == JsonValueKind.Array || val.ValueKind == JsonValueKind.Object)
                {
                    // If the value itself is an array, it might be the combatant list itself
                    if (val.ValueKind == JsonValueKind.Array)
                    {
                        found = el;
                        return true;
                    }
                    // If the value is an object, it could be a dictionary or nested wrapper
                    found = el;
                    return true;
                }
            }
        }

        // Recurse into object properties
        foreach (var prop in el.EnumerateObject())
        {
            if (FindCombatantsAnywhere(prop.Value, out var inner))
            {
                found = inner;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Handles CombatData wrapped inside a "send"/"broadcast" envelope:
    /// <c>{ type:"send", msgtype:"CombatData", msg:{ Encounter:{...}, Combatant:[...] } }</c>
    /// or <c>{ type:"send", msgtype:"CombatData", msg:{ encounter:{...}, combatants:[...] } }</c>
    /// </summary>
    private void HandleWrappedCombatData(JsonElement root)
    {
        // Check msgtype / msgType / msg_type for "CombatData"
        string? msgType = null;
        if (TryGetString(root, "msgtype", out var mt)) msgType = mt;
        else if (TryGetString(root, "msgType", out mt)) msgType = mt;
        else if (TryGetString(root, "msg_type", out mt)) msgType = mt;

        if (msgType == null || !msgType.Contains("combat", StringComparison.OrdinalIgnoreCase))
        {
            // Not a CombatData wrapper — check if the payload itself looks like combat data
            if (root.TryGetProperty("msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
            {
                if (LooksLikeCombatData(msgEl))
                {
                    HandleCombatData(msgEl);
                    return;
                }
            }
            return;
        }

        // Extract the inner payload from "msg" field
        if (!root.TryGetProperty("msg", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return;

        HandleCombatData(payload);
    }

    /// <summary>
    /// Heuristic check: does this JSON object look like it contains combat data?
    /// Looks for Encounter/encounter or combatant/player/party fields.
    /// Also returns true if the element itself is an array (can be combatant list)
    /// or if it has a "CombatData" field (some OverlayPlugin builds nest the real
    /// payload under that key).
    /// </summary>
    private static bool LooksLikeCombatData(JsonElement el)
    {
        // Array at root level could be a list of combatants
        if (el.ValueKind == JsonValueKind.Array) return true;
        if (el.ValueKind != JsonValueKind.Object) return false;

        // Some builds send { type:"CombatData", CombatData:{ ... } }
        if (el.TryGetProperty("CombatData", out var cdField) && cdField.ValueKind == JsonValueKind.Object)
            return true;

        foreach (var key in new[] {
            "Encounter", "encounter",
            "Combatant", "combatant", "Combatants", "combatants",
            "Players", "players", "Party", "party",
            "Members", "members", "Member", "member",
            "Allies", "allies" })
        {
            if (el.TryGetProperty(key, out _))
                return true;
        }
        return false;
    }

    /// <summary>
    /// OverlayPlugin IINACT sends ALL numeric Encounter fields as STRINGS
    /// (e.g. "duration":"06:07", "DPS":"0", "damage":"106").
    /// This method manually parses the JsonElement, preferring the ALL-CAPS
    /// numeric variants (DURATION, DAMAGE-k, etc.) over formatted strings.
    /// </summary>
    private static EncounterInfo? DeserializeCombatEncounter(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var enc = new EncounterInfo();

            // title — always a string
            enc.Title = TryGetStringValue(root, "title") ?? "Encounter";

            // Duration: prefer DURATION (uppercase, numeric seconds as string),
            // fall back to parsing "duration" (may be "MM:SS" or number string).
            enc.Duration = ParseDouble(root, "DURATION");
            if (enc.Duration <= 0)
                enc.Duration = ParseDuration(root, "duration");

            // DPS: try the uppercase "DPS" first (OverlayPlugin sends as string "0"),
            // then try "encdps" / "ENCDPS".
            enc.DPS = ParseDouble(root, "DPS");
            if (enc.DPS <= 0)
            {
                enc.DPS = ParseDouble(root, "encdps");
                if (enc.DPS <= 0)
                    enc.DPS = ParseDouble(root, "ENCDPS");
            }

            // Damage: prefer "damage" (lowercase, numeric string), then "Damage".
            enc.Damage = ParseDouble(root, "damage");
            if (enc.Damage <= 0)
                enc.Damage = ParseDouble(root, "Damage");
            if (enc.Damage <= 0)
                enc.Damage = ParseDouble(root, "DAMAGE-k"); // OverlayPlugin kilo variant

            enc.IsFighting = ParseBool(root, "isFighting") 
                || ParseBool(root, "Infight") 
                || ParseBool(root, "incombat");

            return enc;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deserializes a list of CombatantInfo, manually parsing each to handle
    /// OverlayPlugin's string-format numeric values.
    /// </summary>
    private static List<CombatantInfo>? DeserializeCombatants(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return null;

            var list = new List<CombatantInfo>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var ci = ParseCombatant(item);
                if (ci != null) list.Add(ci);
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a single CombatantInfo from a JsonElement object.
    /// </summary>
    private static CombatantInfo? DeserializeCombatant(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return ParseCombatant(root);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Core combatant parser — handles all the field-name variants that
    /// OverlayPlugin uses (Name, name, Job, job, ENCDPS, DPS, Damage, damage, etc.)
    /// with numeric values as either numbers or strings.
    /// </summary>
    private static CombatantInfo? ParseCombatant(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;

        var ci = new CombatantInfo();

        ci.Name = TryGetStringValue(obj, "Name")
            ?? TryGetStringValue(obj, "name")
            ?? string.Empty;

        ci.Job = TryGetStringValue(obj, "Job")
            ?? TryGetStringValue(obj, "job")
            ?? string.Empty;

        // DPS: OverlayPlugin sends "ENCDPS" (uppercase) or "encdps".
        var dps = ParseDouble(obj, "ENCDPS");
        if (dps <= 0) dps = ParseDouble(obj, "encdps");
        if (dps <= 0) dps = ParseDouble(obj, "DPS");
        if (dps <= 0) dps = ParseDouble(obj, "dps");
        ci.DPS = dps;

        ci.Damage = ParseDouble(obj, "Damage");
        if (ci.Damage <= 0) ci.Damage = ParseDouble(obj, "damage");

        ci.DamagePercent = ParseDouble(obj, "DamagePercent");
        if (ci.DamagePercent <= 0) ci.DamagePercent = ParseDouble(obj, "damagePercent");

        ci.Healing = ParseDouble(obj, "Healing");
        if (ci.Healing <= 0) ci.Healing = ParseDouble(obj, "healing");

        ci.HealingPercent = ParseDouble(obj, "HealingPercent");
        if (ci.HealingPercent <= 0) ci.HealingPercent = ParseDouble(obj, "healingPercent");

        ci.HPS = ParseDouble(obj, "HPS");
        if (ci.HPS <= 0) ci.HPS = ParseDouble(obj, "hps");
        if (ci.HPS <= 0) ci.HPS = ParseDouble(obj, "ENCHPS");
        if (ci.HPS <= 0) ci.HPS = ParseDouble(obj, "enchps");

        ci.Deaths = ParseInt(obj, "Deaths");
        if (ci.Deaths <= 0) ci.Deaths = ParseInt(obj, "deaths");

        return ci;
    }

    // -------------------------------------------------------------------
    // Low-level JsonElement helpers for string-tolerant number parsing
    // -------------------------------------------------------------------

    /// <summary>Parse a double from a JsonElement, handling Number and String values.</summary>
    private static double ParseDouble(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el))
            return 0;
        return ReadDouble(el);
    }

    private static double ReadDouble(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetDouble(out var d) ? d : 0;
            case JsonValueKind.String:
            {
                var s = el.GetString();
                if (string.IsNullOrWhiteSpace(s)) return 0;
                if (double.TryParse(s,
                        System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands,
                        System.Globalization.CultureInfo.InvariantCulture, out var v))
                    return v;
                return 0;
            }
            default:
                return 0;
        }
    }

    /// <summary>Parse duration from a JsonElement that may be "MM:SS" or "367" (seconds).</summary>
    private static double ParseDuration(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el))
            return 0;

        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetDouble(out var d) ? d : 0;

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString() ?? "";
            // Try direct number parse first
            if (double.TryParse(s,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                return v;
            // Try "MM:SS" or "HH:MM:SS" format
            if (TimeSpan.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var ts))
                return ts.TotalSeconds;
        }

        return 0;
    }

    private static int ParseInt(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el))
            return 0;

        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetInt32(out var i) ? i : 0;
        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var iv))
            return iv;

        return 0;
    }

    private static bool ParseBool(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el))
            return false;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.String
            && bool.TryParse(el.GetString(), out var b))
            return b;
        return false;
    }

    private static string? TryGetStringValue(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        if (el.ValueKind == JsonValueKind.Number || el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            return el.ToString();
        return null;
    }

    private void AddNamedAlert(JsonElement payload, string propertyName, AlertType type, float defaultDuration)
    {
        if (TryGetString(payload, propertyName, out var text) && !string.IsNullOrWhiteSpace(text))
            EnqueueAlert(text.Trim(), type, defaultDuration);
    }

    private void EnqueueAlert(CactbotAlert alert)
    {
        // Route based on overlay style
        switch (config.AlertOverlayStyle)
        {
            case OverlayStyle.GameAlert:
                // Display as native FFXIV error toasts (red alerts with timer bars).
                // Respect the per-type filter toggles so users can choose which
                // severity levels trigger game alerts.
                bool shouldShowGameAlert = alert.Type switch
                {
                    AlertType.Alarm => config.GameAlertShowAlarm,
                    AlertType.Alert => config.GameAlertShowAlert,
                    AlertType.Info => config.GameAlertShowInfo,
                    _ => false
                };

                if (shouldShowGameAlert)
                    gameAlertQueue.Enqueue((alert.Text, alert.Type));
                break;

            case OverlayStyle.GimmickHint:
                // Display as native FFXIV center-screen gimmick hints with a
                // countdown timer bar (the game's "tells you what's going on"
                // announcements, e.g. Limit Break). Respect the per-type filter
                // toggles so users can choose which severity levels trigger them.
                bool shouldShowGimmickHint = alert.Type switch
                {
                    AlertType.Alarm => config.GimmickHintShowAlarm,
                    AlertType.Alert => config.GimmickHintShowAlert,
                    AlertType.Info => config.GimmickHintShowInfo,
                    _ => false
                };

                if (shouldShowGimmickHint)
                    gimmickHintQueue.Enqueue((alert.Text, alert.Type, alert.Duration));
                break;

            case OverlayStyle.BattleTalk:
                // Display as native FFXIV in-combat "Battle Talk" dialogue
                // boxes (speaker name plate + portrait icon). Respect the
                // per-type filter toggles so users can choose which severity
                // levels trigger them.
                bool shouldShowBattleTalk = alert.Type switch
                {
                    AlertType.Alarm => config.BattleTalkShowAlarm,
                    AlertType.Alert => config.BattleTalkShowAlert,
                    AlertType.Info => config.BattleTalkShowInfo,
                    _ => false
                };

                if (shouldShowBattleTalk)
                    battleTalkQueue.Enqueue((alert.Text, alert.Type, alert.Duration));
                break;

            case OverlayStyle.Toast:
                // Send as a real FFXIV quest toast instead of rendering in the overlay.
                // Toasts queue naturally in the game UI (one at a time, with native duration).
                toastQueue.Enqueue(alert.Text);
                break;

            default: // Custom
                lock (alertLock)
                {
                    alerts.Add(alert);

                    // Bound the list so memory usage stays predictable
                    if (alerts.Count > MaxStoredAlerts)
                        alerts.RemoveRange(0, alerts.Count - MaxStoredAlerts);
                }
                break;
        }

        if (config.OutputToChatAnnouncement)
            chatQueue.Enqueue(alert.Text);

        // Notify TTS subscribers (fire-and-forget, non-blocking)
        OnAlertForTts?.Invoke(alert.Text, alert.Type);

        log.Debug($"[CactbotPlugin] [{alert.Type}] \"{alert.Text}\" ({alert.Duration:F1}s)");
    }

    /// <summary>
    /// Enqueues a message to be printed in the chat announcement channel.
    /// The message is drained on the game main thread via <c>IFramework.Update</c>.
    /// Thread-safe.
    /// </summary>
    public void EnqueueChatMessage(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            chatQueue.Enqueue(message);
    }

    /// <summary>
    /// Returns the current encounter info (or null if none active). Thread-safe.
    /// </summary>
    public EncounterInfo? GetEncounter()
    {
        lock (combatLock)
            return currentEncounter;
    }

    /// <summary>
    /// Returns a copy of the current combatants list, sorted by DPS descending. Thread-safe.
    /// </summary>
    public List<CombatantInfo> GetCombatants()
    {
        lock (combatLock)
        {
            var sorted = new List<CombatantInfo>(combatants);
            sorted.Sort((a, b) => b.DPS.CompareTo(a.DPS));
            return sorted;
        }
    }

    /// <summary>
    /// Returns combatants that have a known playable job (filters out pets,
    /// chocobos, NPCs, and other non-player entities). Thread-safe.
    /// </summary>
    public List<CombatantInfo> GetPlayerCombatants()
    {
        lock (combatLock)
        {
            var sorted = new List<CombatantInfo>(combatants.Count);
            foreach (var c in combatants)
            {
                if (IsPlayerJob(c.Job))
                    sorted.Add(c);
            }
            sorted.Sort((a, b) => b.DPS.CompareTo(a.DPS));
            return sorted;
        }
    }

    /// <summary>
    /// Returns true if <paramref name="job"/> is a known playable job abbreviation.
    /// </summary>
    private static bool IsPlayerJob(string job)
    {
        if (string.IsNullOrWhiteSpace(job) || job.Length < 3)
            return false;

        // All playable jobs in FFXIV (as of Dawntrail 7.x)
        return job switch
        {
            // Tanks
            "PLD" or "WAR" or "DRK" or "GNB" =>
                true,
            // Healers
            "WHM" or "SCH" or "AST" or "SGE" =>
                true,
            // Melee DPS
            "MNK" or "DRG" or "NIN" or "SAM" or "RPR" or "VPR" =>
                true,
            // Physical Ranged DPS
            "BRD" or "MCH" or "DNC" =>
                true,
            // Magical Ranged DPS
            "BLM" or "SMN" or "RDM" or "PCT" or "BLU" =>
                true,
            _ => false,
        };
    }

    /// <summary>
    /// Finds a combatant by name and returns their info, or null if not found.
    /// Thread-safe.
    /// </summary>
    public CombatantInfo? GetPlayerCombatant(string playerName)
    {
        lock (combatLock)
        {
            return combatants.Find(c =>
                c.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Dequeues one pending chat announcement message. Returns <c>false</c> when the queue is empty.
    /// Call this from the game's main thread (e.g. <c>IFramework.Update</c>) to safely forward
    /// messages to <c>IChatGui</c>.
    /// </summary>
    public bool TryDequeueChat(out string message) => chatQueue.TryDequeue(out message!);

    /// <summary>
    /// Tries to dequeue the next game alert (for native FFXIV error toast display).
    /// Call this from the framework update loop to drain the queue and show game alerts.
    /// </summary>
    public bool TryDequeueGameAlert(out (string text, AlertType type) alert)
    {
        return gameAlertQueue.TryDequeue(out alert);
    }

    /// <summary>
    /// Tries to dequeue the next gimmick hint (for native FFXIV center-screen
    /// gimmick hint display with a countdown timer bar).
    /// Call this from the framework update loop to drain the queue.
    /// </summary>
    public bool TryDequeueGimmickHint(out (string text, AlertType type, float duration) alert)
    {
        return gimmickHintQueue.TryDequeue(out alert);
    }

    /// <summary>
    /// Tries to dequeue the next battle talk (for native FFXIV in-combat
    /// "Battle Talk" dialogue box display with a speaker name and portrait).
    /// Call this from the framework update loop to drain the queue.
    /// </summary>
    public bool TryDequeueBattleTalk(out (string text, AlertType type, float duration) alert)
    {
        return battleTalkQueue.TryDequeue(out alert);
    }

    /// <summary>
    /// Enqueues a message to be shown as a real FFXIV toast.
    /// Drained on the game main thread via <c>IFramework.Update</c>.
    /// Thread-safe.
    /// </summary>
    public void EnqueueToast(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            toastQueue.Enqueue(message);
    }

    /// <summary>
    /// Dequeues one pending toast message. Returns <c>false</c> when the queue is empty.
    /// </summary>
    public bool TryDequeueToast(out string message) => toastQueue.TryDequeue(out message!);

    /// <summary>Clears all stored timeline entries (e.g. on zone change). Thread-safe.</summary>
    public void ClearTimelineEntries()
    {
        lock (timelineLock)
            timelineEntries.Clear();
    }

    /// <summary>Clears all stored alerts. Thread-safe.</summary>
    public void ClearAlerts()
    {
        lock (alertLock)
            alerts.Clear();
    }

    /// <summary>Resets encounter metadata and combatant list. Thread-safe.</summary>
    public void ResetCombatState()
    {
        lock (combatLock)
        {
            currentEncounter = null;
            combatants.Clear();
        }
    }

    private void EnqueueAlert(string text, AlertType alertType, float duration)
    {
        EnqueueAlert(new CactbotAlert
        {
            Text = text,
            Type = alertType,
            Duration = Math.Max(0.5f, duration),
            ReceivedAt = DateTime.UtcNow
        });
    }

    private static bool TryGetString(JsonElement obj, string propertyName, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(propertyName, out var property))
            return false;

        switch (property.ValueKind)
        {
            case JsonValueKind.String:
                value = property.GetString();
                return !string.IsNullOrWhiteSpace(value);

            case JsonValueKind.Object:
            {
                // Some payloads localize text in a map, prefer English then first string.
                if (property.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
                {
                    value = en.GetString();
                    return !string.IsNullOrWhiteSpace(value);
                }

                var firstString = property
                    .EnumerateObject()
                    .FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.String);

                if (firstString.Value.ValueKind == JsonValueKind.String)
                {
                    value = firstString.Value.GetString();
                    return !string.IsNullOrWhiteSpace(value);
                }

                return false;
            }

            default:
                return false;
        }
    }

    private static bool TryGetFloat(JsonElement obj, string propertyName, out float value)
    {
        value = 0f;
        if (!obj.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetSingle(out value);

        if (property.ValueKind == JsonValueKind.String && float.TryParse(property.GetString(), out value))
            return true;

        return false;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AlertType ParseAlertType(string? raw) => raw?.ToLowerInvariant() switch
    {
        "alarm" => AlertType.Alarm,
        "alert" => AlertType.Alert,
        "info"  => AlertType.Info,
        _       => AlertType.Info
    };

    private static float DefaultDuration(AlertType type) => type switch
    {
        AlertType.Alarm => 5f,
        AlertType.Alert => 4f,
        _               => 3f
    };

    // -----------------------------------------------------------------------
    // IDisposable
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        cts.Cancel();

        // Attempt a clean WebSocket close (best effort - we're shutting down)
        try
        {
            if (socket?.State == WebSocketState.Open)
                socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Plugin unloading",
                    CancellationToken.None)
                .Wait(millisecondsTimeout: 1_000);
        }
        catch { /* swallow - plugin is being disposed */ }

        socket?.Dispose();
        cts.Dispose();
    }
}
