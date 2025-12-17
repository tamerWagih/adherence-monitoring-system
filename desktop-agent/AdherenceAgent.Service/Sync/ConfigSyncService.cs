using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Helpers;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Sync;

/// <summary>
/// Background service that periodically syncs workstation configuration (including application classifications) from backend.
/// Syncs on startup and then every hour (or configurable interval).
/// </summary>
public class ConfigSyncService : BackgroundService
{
    private readonly ILogger<ConfigSyncService> _logger;
    private readonly AgentConfig _config;
    private readonly CredentialStore _credentialStore;
    private readonly ClassificationCache _classificationCache;
    private readonly BreakScheduleCache _breakScheduleCache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeSpan _syncInterval;

    private string? _workstationId;
    private string? _apiKey;

    public ConfigSyncService(
        ILogger<ConfigSyncService> logger,
        AgentConfig config,
        CredentialStore credentialStore,
        ClassificationCache classificationCache,
        BreakScheduleCache breakScheduleCache,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _config = config;
        _credentialStore = credentialStore;
        _classificationCache = classificationCache;
        _breakScheduleCache = breakScheduleCache;
        _httpClientFactory = httpClientFactory;
        _syncInterval = TimeSpan.FromHours(1); // Sync every hour by default
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadCredentials();

        // Wait a bit for other services to initialize
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        // Initial sync attempt
        if (HasCredentials())
        {
            _logger.LogInformation("Performing initial configuration sync...");
            await SyncConfigurationAsync(stoppingToken);
        }
        else
        {
            _logger.LogWarning("Workstation credentials not found; configuration sync skipped. Set credentials first.");
        }

        // Periodic sync
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!HasCredentials())
                {
                    LoadCredentials();
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Check again in 5 minutes
                    continue;
                }

                await Task.Delay(_syncInterval, stoppingToken);
                await SyncConfigurationAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Configuration sync loop error");
                // Wait a bit before retrying
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private void LoadCredentials()
    {
        var creds = _credentialStore.Load();
        if (creds.HasValue)
        {
            _workstationId = creds.Value.workstationId;
            _apiKey = creds.Value.apiKey;
        }
        else
        {
            _workstationId = null;
            _apiKey = null;
        }
    }

    private bool HasCredentials() =>
        !string.IsNullOrWhiteSpace(_workstationId) && !string.IsNullOrWhiteSpace(_apiKey);

    private async Task SyncConfigurationAsync(CancellationToken token)
    {
        if (!HasCredentials())
        {
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("adherence");
            client.BaseAddress ??= new Uri(_config.ApiEndpoint.TrimEnd('/') + "/");

            // Include NT account in query parameter for break schedule resolution
            var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
            var configUrl = string.IsNullOrWhiteSpace(ntAccount)
                ? "adherence/workstation/config"
                : $"adherence/workstation/config?nt={Uri.EscapeDataString(ntAccount)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, configUrl);
            request.Headers.Add("X-API-Key", _apiKey);
            request.Headers.Add("X-Workstation-ID", _workstationId);

            var response = await client.SendAsync(request, token);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var configData = await response.Content.ReadFromJsonAsync<WorkstationConfigResponse>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    token);

                // Log workstation configuration values
                if (configData != null)
                {
                    _logger.LogInformation(
                        "Configuration sync received - workstation_id: {WorkstationId}, batch_size: {BatchSize}, sync_interval_seconds: {SyncInterval}, idle_threshold_minutes: {IdleThreshold}",
                        configData.WorkstationId,
                        configData.BatchSize ?? _config.BatchSize,
                        configData.SyncIntervalSeconds ?? _config.SyncIntervalSeconds,
                        configData.IdleThresholdMinutes ?? _config.IdleThresholdMinutes);
                }

                if (configData?.ApplicationClassifications != null)
                {
                    _classificationCache.SaveClassifications(configData.ApplicationClassifications);
                    _logger.LogInformation(
                        "Configuration synced successfully. Loaded {Count} application classifications.",
                        configData.ApplicationClassifications.Count);
                }
                else
                {
                    _logger.LogWarning("Configuration sync returned empty classifications");
                }

                // Cache break schedules if available
                if (configData?.BreakSchedules != null)
                {
                    _breakScheduleCache.SaveSchedules(configData.BreakSchedules);
                    _logger.LogInformation(
                        "Loaded {Count} break schedules.",
                        configData.BreakSchedules.Count);
                }
                else
                {
                    _logger.LogDebug("No break schedules in configuration response");
                }
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Configuration sync failed: Invalid credentials (status {StatusCode})", response.StatusCode);
            }
            else
            {
                _logger.LogWarning("Configuration sync failed: HTTP {StatusCode}", response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Configuration sync failed: Network error. Using cached classifications.");
            // Continue using cached classifications - this is expected behavior for offline mode
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Configuration sync failed unexpectedly");
        }
    }

    /// <summary>
    /// Response model for workstation config endpoint.
    /// Matches backend WorkstationConfigService.getWorkstationConfig() response.
    /// </summary>
    private class WorkstationConfigResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("workstation_id")]
        public string? WorkstationId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("sync_interval_seconds")]
        public int? SyncIntervalSeconds { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("batch_size")]
        public int? BatchSize { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("idle_threshold_minutes")]
        public int? IdleThresholdMinutes { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("application_classifications")]
        public List<ApplicationClassification>? ApplicationClassifications { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("break_schedules")]
        public List<BreakSchedule>? BreakSchedules { get; set; }
    }
}
