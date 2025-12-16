using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AdherenceAgent.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Shared.Configuration;

/// <summary>
/// Manages caching of break schedules locally.
/// Loads from and saves to JSON file in ProgramData.
/// </summary>
public class BreakScheduleCache
{
    private readonly string _cacheFilePath;
    private readonly ILogger<BreakScheduleCache>? _logger;
    private List<BreakSchedule>? _cachedSchedules;

    public BreakScheduleCache(ILogger<BreakScheduleCache>? logger = null)
    {
        PathProvider.EnsureDirectories();
        _cacheFilePath = Path.Combine(PathProvider.BaseDirectory, "break_schedules.json");
        _logger = logger;
    }

    /// <summary>
    /// Get cached break schedules.
    /// Returns empty list if cache file doesn't exist or is invalid.
    /// Invalidates cache if it's from a previous day (break schedules are date-specific).
    /// </summary>
    public List<BreakSchedule> GetSchedules()
    {
        if (_cachedSchedules != null)
        {
            return _cachedSchedules;
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

                var data = JsonSerializer.Deserialize<BreakScheduleCacheData>(json, options);
                if (data?.Schedules != null)
                {
                    // Check if cache is from today (break schedules are date-specific)
                    var cacheDate = data.LastUpdated.Date;
                    var today = DateTime.UtcNow.Date;
                    
                    if (cacheDate < today)
                    {
                        // Cache is from a previous day - invalidate it
                        _logger?.LogInformation(
                            "Break schedule cache is from {CacheDate}, but today is {Today}. Invalidating cache.",
                            cacheDate, today);
                        ClearCache();
                        // Delete the old cache file
                        try
                        {
                            File.Delete(_cacheFilePath);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to delete old cache file");
                        }
                        _cachedSchedules = new List<BreakSchedule>();
                        return _cachedSchedules;
                    }

                    _cachedSchedules = data.Schedules;
                    _logger?.LogDebug("Loaded {Count} break schedules from cache (dated {CacheDate})", 
                        _cachedSchedules.Count, cacheDate);
                    return _cachedSchedules;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load break schedules from cache file");
        }

        // Return empty list if cache doesn't exist or failed to load
        _cachedSchedules = new List<BreakSchedule>();
        return _cachedSchedules;
    }

    /// <summary>
    /// Save break schedules to cache file.
    /// </summary>
    public void SaveSchedules(List<BreakSchedule> schedules)
    {
        try
        {
            var data = new BreakScheduleCacheData
            {
                Schedules = schedules,
                LastUpdated = DateTime.UtcNow,
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
            };

            var json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(_cacheFilePath, json);

            _cachedSchedules = schedules;
            _logger?.LogInformation("Saved {Count} break schedules to cache", schedules.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save break schedules to cache file");
            throw;
        }
    }

    /// <summary>
    /// Clear cached schedules (force reload from file on next access).
    /// </summary>
    public void ClearCache()
    {
        _cachedSchedules = null;
    }

    /// <summary>
    /// Get the cache file path (for debugging/logging).
    /// </summary>
    public string CacheFilePath => _cacheFilePath;

    /// <summary>
    /// Internal data structure for cache file.
    /// </summary>
    private class BreakScheduleCacheData
    {
        public List<BreakSchedule> Schedules { get; set; } = new();
        public DateTime LastUpdated { get; set; }
    }
}
