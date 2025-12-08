namespace AdherenceAgent.Shared.Models;

public class AdherenceEvent
{
    public long? Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime EventTimestampUtc { get; set; } = DateTime.UtcNow;
    public string? ApplicationName { get; set; }
    public string? ApplicationPath { get; set; }
    public string? WindowTitle { get; set; }
    public bool? IsWorkApplication { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

