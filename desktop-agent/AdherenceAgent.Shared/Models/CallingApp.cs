using System.Text.Json.Serialization;

namespace AdherenceAgent.Shared.Models;

/// <summary>
/// Calling App Model
/// Represents a VoIP/telephony application used by agents.
/// </summary>
public class CallingApp
{
    [JsonPropertyName("app_name")]
    public string AppName { get; set; } = string.Empty;

    [JsonPropertyName("app_type")]
    public string AppType { get; set; } = string.Empty; // "WEB" or "DESKTOP"

    [JsonPropertyName("client_name")]
    public string? ClientName { get; set; }

    // For web apps:
    [JsonPropertyName("website_url")]
    public string? WebsiteUrl { get; set; }

    [JsonPropertyName("domain")]
    public string? Domain { get; set; }

    [JsonPropertyName("url_pattern")]
    public string? UrlPattern { get; set; }

    // For desktop apps:
    [JsonPropertyName("process_name_pattern")]
    public string? ProcessNamePattern { get; set; }

    [JsonPropertyName("window_title_pattern")]
    public string? WindowTitlePattern { get; set; }

    // Call status patterns
    [JsonPropertyName("call_status_patterns")]
    public CallStatusPatterns? CallStatusPatterns { get; set; }
}

/// <summary>
/// Call Status Patterns
/// Patterns used to detect call status from window titles.
/// </summary>
public class CallStatusPatterns
{
    [JsonPropertyName("in_call")]
    public List<string>? InCall { get; set; }

    [JsonPropertyName("ringing")]
    public List<string>? Ringing { get; set; }

    [JsonPropertyName("idle")]
    public List<string>? Idle { get; set; }

    [JsonPropertyName("on_hold")]
    public List<string>? OnHold { get; set; }
}

