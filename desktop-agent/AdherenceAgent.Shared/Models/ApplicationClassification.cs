using System.Text.Json.Serialization;

namespace AdherenceAgent.Shared.Models;

/// <summary>
/// Application Classification Model
/// 
/// Represents a classification rule for applications.
/// Matches backend ApplicationClassification entity structure.
/// </summary>
public class ApplicationClassification
{
    /// <summary>
    /// Application name pattern (e.g., "chrome.exe", "*dialer*")
    /// Supports wildcards: * (any characters), ? (single character)
    /// </summary>
    [JsonPropertyName("name_pattern")]
    public string? NamePattern { get; set; }

    /// <summary>
    /// Application path pattern (e.g., "C:\\Program Files\\*CRM*\\*")
    /// Supports wildcards: * (any characters), ? (single character)
    /// </summary>
    [JsonPropertyName("path_pattern")]
    public string? PathPattern { get; set; }

    /// <summary>
    /// Window title pattern (e.g., "*Dashboard*", "*Facebook*")
    /// Supports wildcards: * (any characters), ? (single character)
    /// </summary>
    [JsonPropertyName("window_title_pattern")]
    public string? WindowTitlePattern { get; set; }

    /// <summary>
    /// Classification: "WORK", "NON_WORK", or "NEUTRAL"
    /// </summary>
    [JsonPropertyName("classification")]
    public string Classification { get; set; } = string.Empty;

    /// <summary>
    /// Priority (higher priority = checked first)
    /// Default: 10
    /// </summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 10;

    /// <summary>
    /// Whether this classification rule is active
    /// Note: Backend filters to is_active=true, so this may not be in response
    /// </summary>
    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; } = true;
}
