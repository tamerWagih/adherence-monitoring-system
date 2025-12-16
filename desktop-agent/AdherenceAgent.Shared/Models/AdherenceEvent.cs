namespace AdherenceAgent.Shared.Models;

/// <summary>
/// Adherence Event Model
/// 
/// Represents an event captured by the Desktop Agent.
/// All events must include the NT account (sam_account_name) for employee identification.
/// </summary>
public class AdherenceEvent
{
    public long? Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime EventTimestampUtc { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Windows NT account (sam_account_name only, e.g., z.salah.3613)
    /// Required for employee identification at event ingestion.
    /// </summary>
    public string NtAccount { get; set; } = string.Empty;
    
    public string? ApplicationName { get; set; }
    public string? ApplicationPath { get; set; }
    public string? WindowTitle { get; set; }
    public bool? IsWorkApplication { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

