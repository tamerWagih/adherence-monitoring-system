using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AdherenceAgent.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Shared.Configuration;

/// <summary>
/// Manages caching of application classifications locally.
/// Loads from and saves to JSON file in ProgramData.
/// </summary>
public class ClassificationCache
{
    private readonly string _cacheFilePath;
    private readonly ILogger<ClassificationCache>? _logger;
    private List<ApplicationClassification>? _cachedClassifications;

    public ClassificationCache(ILogger<ClassificationCache>? logger = null)
    {
        PathProvider.EnsureDirectories();
        _cacheFilePath = Path.Combine(PathProvider.BaseDirectory, "classifications.json");
        _logger = logger;
    }

    /// <summary>
    /// Get cached classifications.
    /// Returns empty list if cache file doesn't exist or is invalid.
    /// </summary>
    public List<ApplicationClassification> GetClassifications()
    {
        if (_cachedClassifications != null)
        {
            return _cachedClassifications;
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

                var data = JsonSerializer.Deserialize<ClassificationCacheData>(json, options);
                if (data?.Classifications != null)
                {
                    _cachedClassifications = data.Classifications;
                    _logger?.LogDebug("Loaded {Count} classifications from cache", _cachedClassifications.Count);
                    return _cachedClassifications;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load classifications from cache file");
        }

        // Return empty list if cache doesn't exist or failed to load
        _cachedClassifications = new List<ApplicationClassification>();
        return _cachedClassifications;
    }

    /// <summary>
    /// Save classifications to cache file.
    /// </summary>
    public void SaveClassifications(List<ApplicationClassification> classifications)
    {
        try
        {
            var data = new ClassificationCacheData
            {
                Classifications = classifications,
                LastUpdated = DateTime.UtcNow,
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                // JsonPropertyName attributes on ApplicationClassification will override naming policy
                // This ensures snake_case is used (matching backend API)
            };

            var json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(_cacheFilePath, json);

            _cachedClassifications = classifications;
            _logger?.LogInformation("Saved {Count} classifications to cache", classifications.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save classifications to cache file");
            throw;
        }
    }

    /// <summary>
    /// Clear cached classifications (force reload from file on next access).
    /// </summary>
    public void ClearCache()
    {
        _cachedClassifications = null;
    }

    /// <summary>
    /// Get the cache file path (for debugging/logging).
    /// </summary>
    public string CacheFilePath => _cacheFilePath;

    /// <summary>
    /// Internal data structure for cache file.
    /// </summary>
    private class ClassificationCacheData
    {
        public List<ApplicationClassification> Classifications { get; set; } = new();
        public DateTime LastUpdated { get; set; }
    }
}
