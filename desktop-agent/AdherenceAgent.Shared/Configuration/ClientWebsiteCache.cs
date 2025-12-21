using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AdherenceAgent.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Shared.Configuration;

/// <summary>
/// Manages caching of client websites locally.
/// Loads from and saves to JSON file in ProgramData.
/// Similar to ClassificationCache.
/// </summary>
public class ClientWebsiteCache
{
    private readonly string _cacheFilePath;
    private readonly ILogger<ClientWebsiteCache>? _logger;
    private List<ClientWebsite>? _cachedWebsites;
    private DateTime? _lastLoadedFileWriteUtc;

    public ClientWebsiteCache(ILogger<ClientWebsiteCache>? logger = null)
    {
        PathProvider.EnsureDirectories();
        _cacheFilePath = Path.Combine(PathProvider.BaseDirectory, "client_websites.json");
        _logger = logger;
    }

    /// <summary>
    /// Get cached client websites.
    /// Returns empty list if cache file doesn't exist or is invalid.
    /// </summary>
    public List<ClientWebsite> GetClientWebsites()
    {
        // If cache exists, invalidate it when the file on disk changes.
        // This allows the service/tray to reflect new websites without a restart.
        try
        {
            if (_cachedWebsites != null && File.Exists(_cacheFilePath))
            {
                var writeUtc = File.GetLastWriteTimeUtc(_cacheFilePath);
                // IMPORTANT: If the cache was loaded before the file existed, _lastLoadedFileWriteUtc can be null.
                // In that case, the first time the file appears we MUST reload.
                if (!_lastLoadedFileWriteUtc.HasValue || writeUtc > _lastLoadedFileWriteUtc.Value)
                {
                    _logger?.LogDebug("Client websites cache file changed on disk; reloading.");
                    _cachedWebsites = null;
                }
            }
        }
        catch
        {
            // ignore file stat errors; fall through to cached value if present
        }

        if (_cachedWebsites != null)
        {
            return _cachedWebsites;
        }

        try
        {
            if (File.Exists(_cacheFilePath))
            {
                var json = File.ReadAllText(_cacheFilePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                };

                var data = JsonSerializer.Deserialize<ClientWebsiteCacheData>(json, options);
                if (data?.ClientWebsites != null)
                {
                    _cachedWebsites = data.ClientWebsites;
                    try { _lastLoadedFileWriteUtc = File.GetLastWriteTimeUtc(_cacheFilePath); } catch { /* ignore */ }
                    _logger?.LogDebug("Loaded {Count} client websites from cache", _cachedWebsites.Count);
                    return _cachedWebsites;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load client websites from cache file");
        }

        // Return empty list if cache doesn't exist or failed to load
        _cachedWebsites = new List<ClientWebsite>();
        try { _lastLoadedFileWriteUtc = File.Exists(_cacheFilePath) ? File.GetLastWriteTimeUtc(_cacheFilePath) : null; } catch { /* ignore */ }
        return _cachedWebsites;
    }

    /// <summary>
    /// Save client websites to cache file.
    /// </summary>
    public void SaveClientWebsites(List<ClientWebsite> websites)
    {
        try
        {
            var data = new ClientWebsiteCacheData
            {
                ClientWebsites = websites,
                LastUpdated = DateTime.UtcNow,
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                // JsonPropertyName attributes on ClientWebsite will override naming policy
                // This ensures snake_case is used (matching backend API)
            };

            var json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(_cacheFilePath, json);

            _cachedWebsites = websites;
            try { _lastLoadedFileWriteUtc = File.GetLastWriteTimeUtc(_cacheFilePath); } catch { /* ignore */ }
            _logger?.LogInformation("Saved {Count} client websites to cache", websites.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save client websites to cache file");
            throw;
        }
    }

    /// <summary>
    /// Clear cached client websites (force reload from file on next access).
    /// </summary>
    public void ClearCache()
    {
        _cachedWebsites = null;
        _lastLoadedFileWriteUtc = null;
    }

    /// <summary>
    /// Get the cache file path (for debugging/logging).
    /// </summary>
    public string CacheFilePath => _cacheFilePath;

    /// <summary>
    /// Internal data structure for cache file.
    /// </summary>
    private class ClientWebsiteCacheData
    {
        [JsonPropertyName("client_websites")]
        public List<ClientWebsite> ClientWebsites { get; set; } = new();

        [JsonPropertyName("last_updated")]
        public DateTime LastUpdated { get; set; }
    }
}

