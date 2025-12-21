using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdherenceAgent.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Shared.Configuration;

/// <summary>
/// Manages caching of calling apps locally.
/// Loads from and saves to JSON file in ProgramData.
/// Similar to ClientWebsiteCache and ClassificationCache.
/// </summary>
public class CallingAppCache
{
    private readonly string _cacheFilePath;
    private readonly ILogger<CallingAppCache>? _logger;
    private List<CallingApp>? _cachedApps;
    private DateTime? _lastLoadedFileWriteUtc;

    public CallingAppCache(ILogger<CallingAppCache>? logger = null)
    {
        PathProvider.EnsureDirectories();
        _cacheFilePath = Path.Combine(PathProvider.BaseDirectory, "calling_apps.json");
        _logger = logger;
    }

    /// <summary>
    /// Get cached calling apps.
    /// Returns empty list if cache file doesn't exist or is invalid.
    /// </summary>
    public List<CallingApp> GetCallingApps()
    {
        // If cache exists, invalidate it when the file on disk changes.
        // This allows the service/tray to reflect new apps without a restart.
        try
        {
            if (_cachedApps != null && File.Exists(_cacheFilePath))
            {
                var writeUtc = File.GetLastWriteTimeUtc(_cacheFilePath);
                // IMPORTANT: If the cache was loaded before the file existed, _lastLoadedFileWriteUtc can be null.
                // In that case, the first time the file appears we MUST reload.
                if (!_lastLoadedFileWriteUtc.HasValue || writeUtc > _lastLoadedFileWriteUtc.Value)
                {
                    _logger?.LogDebug("Calling apps cache file changed on disk; reloading.");
                    _cachedApps = null;
                }
            }
        }
        catch
        {
            // ignore file stat errors; fall through to cached value if present
        }

        if (_cachedApps != null)
        {
            return _cachedApps;
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

                var data = JsonSerializer.Deserialize<CallingAppCacheData>(json, options);
                if (data?.CallingApps != null)
                {
                    _cachedApps = data.CallingApps;
                    try { _lastLoadedFileWriteUtc = File.GetLastWriteTimeUtc(_cacheFilePath); } catch { /* ignore */ }
                    _logger?.LogDebug("Loaded {Count} calling apps from cache", _cachedApps.Count);
                    return _cachedApps;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load calling apps from cache file");
        }

        // Return empty list if cache doesn't exist or failed to load
        _cachedApps = new List<CallingApp>();
        try { _lastLoadedFileWriteUtc = File.Exists(_cacheFilePath) ? File.GetLastWriteTimeUtc(_cacheFilePath) : null; } catch { /* ignore */ }
        return _cachedApps;
    }

    /// <summary>
    /// Save calling apps to cache file.
    /// </summary>
    public void SaveCallingApps(List<CallingApp> apps)
    {
        try
        {
            var data = new CallingAppCacheData
            {
                CallingApps = apps,
                LastUpdated = DateTime.UtcNow,
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                // JsonPropertyName attributes on CallingApp will override naming policy
                // This ensures snake_case is used (matching backend API)
            };

            var json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(_cacheFilePath, json);

            _cachedApps = apps;
            try { _lastLoadedFileWriteUtc = File.GetLastWriteTimeUtc(_cacheFilePath); } catch { /* ignore */ }
            _logger?.LogInformation("Saved {Count} calling apps to cache", apps.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save calling apps to cache file");
            throw;
        }
    }

    /// <summary>
    /// Clear cached calling apps (force reload from file on next access).
    /// </summary>
    public void ClearCache()
    {
        _cachedApps = null;
        _lastLoadedFileWriteUtc = null;
    }

    /// <summary>
    /// Get the cache file path (for debugging/logging).
    /// </summary>
    public string CacheFilePath => _cacheFilePath;

    /// <summary>
    /// Internal data structure for cache file.
    /// </summary>
    private class CallingAppCacheData
    {
        [JsonPropertyName("calling_apps")]
        public List<CallingApp> CallingApps { get; set; } = new();

        [JsonPropertyName("last_updated")]
        public DateTime LastUpdated { get; set; }
    }
}

