using System;
using System.Text.Json.Serialization;

namespace CactBridge.Models;

// ---------------------------------------------------------------------------
// Domain model
// ---------------------------------------------------------------------------

/// <summary>Severity level matching cactbot raidboss trigger types.</summary>
public enum AlertType
{
    Info,
    Alert,
    Alarm
}

/// <summary>A single raidboss alert, ready for display.</summary>
public class CactbotAlert
{
    /// <summary>Human-readable text to display (e.g. "Stack!", "Spread").</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Severity - determines display colour.</summary>
    public AlertType Type { get; set; } = AlertType.Info;

    /// <summary>How many seconds this alert should stay on screen.</summary>
    public float Duration { get; set; } = 3.0f;

    /// <summary>UTC timestamp at which this alert was received.</summary>
    public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When set, shows the remaining seconds instead of <see cref="Text"/>.</summary>
    public DateTime? CountdownEndTime { get; set; }

    /// <summary>When set, appends the remaining cast time to <see cref="Text"/>.</summary>
    public DateTime? CastEndTime { get; set; }

    /// <summary>Seconds elapsed since this alert arrived.</summary>
    public float ElapsedSeconds => (float)(DateTime.UtcNow - ReceivedAt).TotalSeconds;

    /// <summary>True when the alert has exceeded its display duration.</summary>
    public bool IsExpired => ElapsedSeconds >= Duration;

    /// <summary>Alpha multiplier (0-1) for a one-second fade-out before expiry.</summary>
    public float FadeAlpha
    {
        get
        {
            if (Duration <= 1f) return 1f;
            var remaining = Duration - ElapsedSeconds;
            return remaining < 1f ? Math.Clamp(remaining, 0f, 1f) : 1f;
        }
    }
}

// ---------------------------------------------------------------------------
// Timeline model
// ---------------------------------------------------------------------------

/// <summary>An upcoming boss ability in the encounter timeline.</summary>
public class TimelineEntry
{
    /// <summary>Name of the ability / mechanic.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Seconds until this ability fires, as received.</summary>
    public double InitialTimeRemaining { get; set; }

    /// <summary>Absolute UTC time when this entry was received.</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Seconds until this ability fires, counting down from <see cref="InitialTimeRemaining"/>.</summary>
    public double TimeRemaining
    {
        get
        {
            var elapsed = (DateTime.UtcNow - ReceivedAt).TotalSeconds;
            return InitialTimeRemaining - elapsed;
        }
    }

    /// <summary>True when the entry's time has passed (for pruning).</summary>
    public bool IsExpired => TimeRemaining <= -5;

    /// <summary>Duration in seconds this entry stays visible after its time passes.</summary>
    public float Duration { get; set; } = 5f;
}

// ---------------------------------------------------------------------------
// Combat data / Damage meter models
// ---------------------------------------------------------------------------

/// <summary>Encounter metadata from the <c>CombatData</c> event.</summary>
public class EncounterInfo
{
    /// <summary>Fight name / encounter title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Encounter duration in seconds.</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    /// <summary>Total encounter DPS (party-wide).</summary>
    [JsonPropertyName("DPS")]
    public double DPS { get; set; }

    /// <summary>Total damage dealt during the encounter.</summary>
    [JsonPropertyName("damage")]
    public double Damage { get; set; }

    /// <summary>True during an active encounter.</summary>
    [JsonPropertyName("isFighting")]
    public bool IsFighting { get; set; }

    /// <summary>Formatted duration string for display.</summary>
    [JsonIgnore]
    public string DurationStr
    {
        get
        {
            var ts = TimeSpan.FromSeconds(Duration);
            return ts.Hours > 0
                ? $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s"
                : ts.Minutes > 0
                    ? $"{ts.Minutes}m {ts.Seconds}s"
                    : $"{ts.Seconds}s";
        }
    }

    /// <summary>Formatted total damage.</summary>
    [JsonIgnore]
    public string DamageStr => Damage >= 1_000_000
        ? $"{Damage / 1_000_000:F2}M"
        : Damage >= 1_000
            ? $"{Damage / 1_000:F1}K"
            : $"{Damage:F0}";
}

/// <summary>A single combatant in the current encounter (OverlayPlugin sends uppercase field names, "ENCDPS" for DPS).</summary>
public class CombatantInfo
{
    private double _dps;

    /// <summary>Character name.</summary>
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Job abbreviation (e.g. "WAR", "WHM", "DRG").</summary>
    [JsonPropertyName("Job")]
    public string Job { get; set; } = string.Empty;

    /// <summary>Total damage dealt.</summary>
    [JsonPropertyName("Damage")]
    public double Damage { get; set; }

    /// <summary>Percentage of total encounter damage.</summary>
    [JsonPropertyName("DamagePercent")]
    public double DamagePercent { get; set; }

    /// <summary>Personal DPS (shares its backing field with <see cref="ENCDPS"/>).</summary>
    [JsonPropertyName("DPS")]
    public double DPS
    {
        get => _dps;
        set => _dps = value;
    }

    /// <summary>OverlayPlugin's native field name for personal DPS.</summary>
    [JsonPropertyName("ENCDPS")]
    public double ENCDPS
    {
        get => _dps;
        set => _dps = value;
    }

    /// <summary>Total healing done.</summary>
    [JsonPropertyName("Healing")]
    public double Healing { get; set; }

    /// <summary>Percentage of total encounter healing.</summary>
    [JsonPropertyName("HealingPercent")]
    public double HealingPercent { get; set; }

    /// <summary>Personal HPS.</summary>
    [JsonPropertyName("HPS")]
    public double HPS { get; set; }

    /// <summary>Number of deaths.</summary>
    [JsonPropertyName("Deaths")]
    public int Deaths { get; set; }

    [JsonIgnore]
    public string DamageStr => Damage >= 1_000_000
        ? $"{Damage / 1_000_000:F2}M"
        : Damage >= 1_000
            ? $"{Damage / 1_000:F1}K"
            : $"{Damage:F0}";

    [JsonIgnore]
    public string HealingStr => Healing >= 1_000_000
        ? $"{Healing / 1_000_000:F2}M"
        : Healing >= 1_000
            ? $"{Healing / 1_000:F1}K"
            : $"{Healing:F0}";

    [JsonIgnore]
    public string DpsStr => $"{DPS:F0}";

    [JsonIgnore]
    public string HpsStr => $"{HPS:F0}";
}

// ---------------------------------------------------------------------------
// OverlayPlugin WebSocket wire types
// ---------------------------------------------------------------------------

/// <summary>
/// Subscription request sent to OverlayPlugin immediately after connecting.
/// Format: <c>{ "call": "subscribe", "events": [ … ] }</c>
/// </summary>
internal class SubscribeRequest
{
    [JsonPropertyName("call")]
    public string Call { get; set; } = "subscribe";

    [JsonPropertyName("events")]
    public string[] Events { get; set; } = Array.Empty<string>();
}
