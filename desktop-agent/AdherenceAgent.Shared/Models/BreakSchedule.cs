using System.Text.Json.Serialization;

namespace AdherenceAgent.Shared.Models;

/// <summary>
/// Represents a scheduled break window from the backend.
/// </summary>
public class BreakSchedule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("start_time")]
    public string StartTime { get; set; } = string.Empty; // Format: "HH:mm:ss"

    [JsonPropertyName("end_time")]
    public string EndTime { get; set; } = string.Empty; // Format: "HH:mm:ss"

    [JsonPropertyName("break_duration_minutes")]
    public int BreakDurationMinutes { get; set; }
}
