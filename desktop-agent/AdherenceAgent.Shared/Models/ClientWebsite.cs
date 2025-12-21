using System.Text.Json.Serialization;

namespace AdherenceAgent.Shared.Models;

/// <summary>
/// Client Website Model
/// Represents a client-specific website that agents access during work.
/// </summary>
public class ClientWebsite
{
    [JsonPropertyName("client_name")]
    public string ClientName { get; set; } = string.Empty;

    [JsonPropertyName("website_url")]
    public string WebsiteUrl { get; set; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("url_pattern")]
    public string? UrlPattern { get; set; }
}

