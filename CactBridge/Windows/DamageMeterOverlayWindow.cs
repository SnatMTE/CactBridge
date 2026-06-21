using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using CactBridge.Models;
using CactBridge.Services;

namespace CactBridge.Windows;

/// <summary>
/// Transparent, always-on-top overlay window that renders a real-time DPS
/// meter table sourced from <c>CombatData</c> events via OverlayPlugin.
///
/// Supports drag-to-reposition, configurable column visibility, and a
/// per-column colour scheme.
/// </summary>
public class DamageMeterOverlayWindow : Window, IDisposable
{
    // Column colours
    private static readonly Vector4 ColorHeader      = new(1.00f, 0.85f, 0.10f, 1f); // gold
    private static readonly Vector4 ColorDps         = new(0.40f, 0.90f, 0.40f, 1f); // green
    private static readonly Vector4 ColorDamage      = new(1.00f, 1.00f, 1.00f, 1f); // white
    private static readonly Vector4 ColorHealing     = new(0.40f, 0.80f, 1.00f, 1f); // blue
    private static readonly Vector4 ColorDeaths      = new(1.00f, 0.30f, 0.30f, 1f); // red
    private static readonly Vector4 ColorJob         = new(0.60f, 0.60f, 0.60f, 1f); // grey
    private static readonly Vector4 ColorPosition    = new(0.80f, 0.80f, 0.80f, 1f); // light grey

    private const float RowHeight = 22f;
    private const float ColPadding = 8f;

    // -----------------------------------------------------------------------
    // Dependencies
    // -----------------------------------------------------------------------
    private readonly Plugin           plugin;
    private readonly WebSocketService wsService;
    private IFontHandle? axisFontHandle;
    private IFontHandle? jupiterFontHandle;
    private IFontHandle? trumpGothicFontHandle;

    private float           lastFontScale;
    private AlertFontPreset lastFontPreset;
    private bool            positionInitialised;
    private bool            moveMode;

    // Cached sorted combatants (rebuilt each frame)
    private readonly List<CombatantInfo> frameCombatants = new();

    // Column widths (calculated each frame based on content)
    private float colDpsWidth    = 60f;
    private float colDamageWidth = 70f;
    private float colHealWidth   = 70f;
    private float colDeathsWidth = 50f;

    // -----------------------------------------------------------------------
    // Constructor
    // -----------------------------------------------------------------------

    public DamageMeterOverlayWindow(Plugin plugin, WebSocketService wsService)
        : base("##CactBridgeDps",
               ImGuiWindowFlags.NoTitleBar        |
               ImGuiWindowFlags.NoScrollbar       |
               ImGuiWindowFlags.NoScrollWithMouse |
               ImGuiWindowFlags.NoSavedSettings   |
               ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.plugin    = plugin;
        this.wsService = wsService;

        IsOpen = true;
    }

    public void Dispose()
    {
        axisFontHandle?.Dispose();
        jupiterFontHandle?.Dispose();
        trumpGothicFontHandle?.Dispose();
    }

    // -----------------------------------------------------------------------
    // Window lifecycle hooks
    // -----------------------------------------------------------------------

    public override void PreDraw()
    {
        var cfg = plugin.Configuration;
        IsOpen = cfg.EnableDpsMeter;

        if (MathF.Abs(cfg.DpsFontScale - lastFontScale) > 0.01f || cfg.DpsFontPreset != lastFontPreset)
        {
            InvalidateFontHandles();
            lastFontScale = cfg.DpsFontScale;
            lastFontPreset = cfg.DpsFontPreset;
        }

        if (!positionInitialised)
        {
            ImGui.SetNextWindowPos(new Vector2(cfg.DpsX, cfg.DpsY), ImGuiCond.Always);
            positionInitialised = true;
        }

        ImGui.SetNextWindowSize(new Vector2(cfg.DpsWidth, cfg.DpsHeight), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(moveMode ? 0.15f : cfg.DpsBgAlpha);

        if (!moveMode || cfg.LockDpsPosition)
            Flags |= ImGuiWindowFlags.NoMove;
        else
            Flags &= ~ImGuiWindowFlags.NoMove;

        if (moveMode)
            Flags &= ~ImGuiWindowFlags.NoInputs;
        else
            Flags |= ImGuiWindowFlags.NoInputs;
    }

    public override void OnClose()
    {
        positionInitialised = false;
    }

    public void ResetPosition() => positionInitialised = false;

    public bool IsMoveMode => moveMode;

    public void SetMoveMode(bool enabled)
    {
        moveMode = enabled;
        IsOpen = true;
    }

    public void ToggleMoveMode()
    {
        moveMode = !moveMode;
        IsOpen = true;
    }

    // -----------------------------------------------------------------------
    // Draw
    // -----------------------------------------------------------------------

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        using var fontPush = PushConfiguredDpsFont(cfg.DpsFontPreset, cfg.DpsFontScale);

        var encounter = wsService.GetEncounter();
        frameCombatants.Clear();
        frameCombatants.AddRange(wsService.GetPlayerCombatants());

        // Fallback: if player-job filtering produced nothing, try unfiltered list
        if (frameCombatants.Count == 0)
            frameCombatants.AddRange(wsService.GetCombatants());

        var drawList = ImGui.GetWindowDrawList();
        var boxPos   = ImGui.GetWindowPos();
        var boxSize  = ImGui.GetWindowSize();
        var lineHeight = ImGui.GetFontSize() * cfg.DpsFontScale;

        // Calculate column widths based on visible columns
        colDpsWidth = ImGui.CalcTextSize("99999 DPS").X + ColPadding;
        colDamageWidth = ImGui.CalcTextSize("99.99M").X + ColPadding;
        colHealWidth = ImGui.CalcTextSize("99.99M HPS").X + ColPadding;
        colDeathsWidth = ImGui.CalcTextSize("Deaths").X + ColPadding;

        var cursorY = boxPos.Y + 4f;
        var leftX = boxPos.X + 4f;

        // ---------- Always-visible status line: encDPS & DPS ----------
        // Even when no combat data has arrived, show zeros so user knows the overlay is active.
        var encDpsValue = encounter?.DPS ?? 0;
        // Find the player's own DPS from combatants (first combatant with a non-empty name)
        var playerDps = frameCombatants.Count > 0 ? frameCombatants[0].DPS : 0;
        var encDpsText = $"encDPS: {encDpsValue:F0}";
        var dpsText    = $"DPS: {playerDps:F0}";

        // Connection indicator
        var connText = wsService.IsConnected ? "ACT ✓" : "ACT ✗";
        var connColor = wsService.IsConnected ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.3f, 0.3f, 1f);

        // Draw the status line at the top-right of the window
        var statusLine = $"{encDpsText}  |  {dpsText}  |  {connText}";
        var statusSize = ImGui.CalcTextSize(statusLine);
        var statusX = boxPos.X + boxSize.X - statusSize.X - 4f;
        drawList.AddText(new Vector2(statusX, cursorY), ImGui.ColorConvertFloat4ToU32(ColorHeader), statusLine);
        cursorY += lineHeight + 2f;

        // ---------- Encounter header ----------
        if (cfg.DpsShowHeader && encounter != null)
        {
            drawList.AddText(new Vector2(leftX, cursorY), ImGui.ColorConvertFloat4ToU32(ColorHeader),
                $"{encounter.Title}  |  {encounter.DurationStr}  |  {encounter.DamageStr}");
            cursorY += lineHeight + 2f;
        }

        // ---------- Table header row ----------
        var headerY = cursorY;

        var xPos = leftX;
        drawList.AddText(new Vector2(xPos, headerY), ImGui.ColorConvertFloat4ToU32(ColorPosition), "#");
        xPos += 24f;
        drawList.AddText(new Vector2(xPos, headerY), ImGui.ColorConvertFloat4ToU32(ColorHeader), "Job");
        xPos += 32f;
        drawList.AddText(new Vector2(xPos, headerY), ImGui.ColorConvertFloat4ToU32(ColorHeader), "Name");
        xPos += 120f;
        drawList.AddText(new Vector2(xPos, headerY), ImGui.ColorConvertFloat4ToU32(ColorDps), "DPS");
        xPos += colDpsWidth;
        drawList.AddText(new Vector2(xPos, headerY), ImGui.ColorConvertFloat4ToU32(ColorDamage), "Damage");
        xPos += colDamageWidth;

        if (cfg.DpsShowHealing)
        {
            drawList.AddText(new Vector2(xPos, headerY), ImGui.ColorConvertFloat4ToU32(ColorHealing), "Healing");
            xPos += colHealWidth;
        }

        if (cfg.DpsShowDeaths)
        {
            drawList.AddText(new Vector2(xPos, headerY), ImGui.ColorConvertFloat4ToU32(ColorDeaths), "Deaths");
        }

        cursorY += lineHeight + 2f;

        // Separator line
        drawList.AddLine(new Vector2(leftX, cursorY), new Vector2(boxPos.X + boxSize.X - 4f, cursorY),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.40f, 0.40f, 0.40f, 0.60f)));
        cursorY += 2f;

        // ---------- Combatant rows or empty state ----------
        if (frameCombatants.Count == 0)
        {
            var emptyText = !wsService.IsConnected
                ? "Not connected to ACT"
                : encounter != null
                    ? $"encounter: {encounter.DPS:F0} — no combatants"
                : "Waiting for combat data...";
            drawList.AddText(new Vector2(leftX + 24f, cursorY),
                ImGui.ColorConvertFloat4ToU32(ColorJob), emptyText);
            cursorY += lineHeight;
        }
        else
        {
            var altBg = new Vector4(1f, 1f, 1f, 0.03f);
            for (int i = 0; i < frameCombatants.Count; i++)
            {
                var c = frameCombatants[i];
                if (cursorY + lineHeight > boxPos.Y + boxSize.Y)
                    break; // clip

                // Alternating row background
                if (i % 2 == 1)
                {
                    drawList.AddRectFilled(new Vector2(leftX, cursorY),
                        new Vector2(boxPos.X + boxSize.X - 4f, cursorY + lineHeight),
                        ImGui.ColorConvertFloat4ToU32(altBg));
                }

                xPos = leftX;

                // Position #
                drawList.AddText(new Vector2(xPos, cursorY), ImGui.ColorConvertFloat4ToU32(ColorPosition), $"{i + 1}");
                xPos += 24f;

                // Job (coloured)
                drawList.AddText(new Vector2(xPos, cursorY), ImGui.ColorConvertFloat4ToU32(ColorJob), c.Job);
                xPos += 32f;

                // Name
                drawList.AddText(new Vector2(xPos, cursorY), ImGui.ColorConvertFloat4ToU32(ColorDamage), c.Name);
                xPos += 120f;

                // DPS
                drawList.AddText(new Vector2(xPos, cursorY), ImGui.ColorConvertFloat4ToU32(ColorDps), c.DpsStr);
                xPos += colDpsWidth;

                // Damage
                drawList.AddText(new Vector2(xPos, cursorY), ImGui.ColorConvertFloat4ToU32(ColorDamage), c.DamageStr);
                xPos += colDamageWidth;

                // Healing
                if (cfg.DpsShowHealing)
                {
                    drawList.AddText(new Vector2(xPos, cursorY), ImGui.ColorConvertFloat4ToU32(ColorHealing), c.HealingStr);
                    xPos += colHealWidth;
                }

                // Deaths
                if (cfg.DpsShowDeaths)
                {
                    drawList.AddText(new Vector2(xPos, cursorY),
                        ImGui.ColorConvertFloat4ToU32(ColorDeaths),
                        c.Deaths > 0 ? $"{c.Deaths}" : "-");
                }

                cursorY += lineHeight;
            }
        }

        // Persist position and size
        var pos = ImGui.GetWindowPos();
        if (MathF.Abs(pos.X - cfg.DpsX) > 0.5f || MathF.Abs(pos.Y - cfg.DpsY) > 0.5f)
        {
            cfg.DpsX = pos.X;
            cfg.DpsY = pos.Y;
            cfg.Save();
        }

        var currentSize = ImGui.GetWindowSize();
        if (MathF.Abs(currentSize.X - cfg.DpsWidth) > 0.5f || MathF.Abs(currentSize.Y - cfg.DpsHeight) > 0.5f)
        {
            cfg.DpsWidth = currentSize.X;
            cfg.DpsHeight = currentSize.Y;
            cfg.Save();
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void InvalidateFontHandles()
    {
        axisFontHandle?.Dispose();
        axisFontHandle = null;
        jupiterFontHandle?.Dispose();
        jupiterFontHandle = null;
        trumpGothicFontHandle?.Dispose();
        trumpGothicFontHandle = null;
    }

    public IDisposable? PushConfiguredDpsFont(AlertFontPreset preset, float fontScale)
    {
        var ui = Plugin.PluginInterface.UiBuilder;
        return preset switch
        {
            AlertFontPreset.DalamudDefault => ui.DefaultFontHandle.Push(),
            AlertFontPreset.DalamudMono => ui.MonoFontHandle.Push(),
            AlertFontPreset.FfxivJupiter => GetOrCreateJupiterFontHandle(fontScale).Push(),
            AlertFontPreset.FfxivTrumpGothic => GetOrCreateTrumpGothicFontHandle(fontScale).Push(),
            _ => GetOrCreateAxisFontHandle(fontScale).Push(),
        };
    }

    private IFontHandle GetOrCreateAxisFontHandle(float scale)
        => axisFontHandle ??= Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.Axis, 14f * scale));

    private IFontHandle GetOrCreateJupiterFontHandle(float scale)
        => jupiterFontHandle ??= Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.Jupiter, 16f * scale));

    private IFontHandle GetOrCreateTrumpGothicFontHandle(float scale)
        => trumpGothicFontHandle ??= Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.TrumpGothic, 23f * scale));
}
