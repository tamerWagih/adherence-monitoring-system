using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Security;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Upload;

/// <summary>
/// Dequeues buffered events and uploads them in batches with retry/backoff.
/// </summary>
public class UploadService : BackgroundService
{
    private readonly ILogger<UploadService> _logger;
    private readonly IEventBuffer _buffer;
    private readonly AgentConfig _config;
    private readonly CredentialStore _credentialStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly int[] _backoffSeconds = new[] { 5, 10, 20, 40 };
    private readonly Random _random = new();
    private int _currentBatchSize;
    private int _currentIntervalSeconds;
    private int _successStreak;

    private string? _workstationId;
    private string? _apiKey;

    public UploadService(
        ILogger<UploadService> logger,
        IEventBuffer buffer,
        AgentConfig config,
        CredentialStore credentialStore,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _buffer = buffer;
        _config = config;
        _credentialStore = credentialStore;
        _httpClientFactory = httpClientFactory;
        _currentBatchSize = Math.Max(10, Math.Min(_config.BatchSize, _config.BatchSize / 2 + 10));
        _currentIntervalSeconds = Math.Max(45, _config.SyncIntervalSeconds);
        _successStreak = 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadCredentials();

        // Staggered startup to avoid thundering herd on restore/start
        if (HasCredentials())
        {
            int baseDelay = Math.Abs(_workstationId!.GetHashCode()) % 300; // 0-299
            int jitter = _random.Next(0, 60); // 0-59
            int initialDelay = baseDelay + jitter;
            _logger.LogInformation("Initial staggered delay before uploads: {Delay}s", initialDelay);
            await Task.Delay(TimeSpan.FromSeconds(initialDelay), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!HasCredentials())
                {
                    LoadCredentials();
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                var pending = await _buffer.GetPendingAsync(_currentBatchSize, _config.MaxRetryAttempts, stoppingToken);
                if (pending.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_currentIntervalSeconds), stoppingToken);
                    continue;
                }

                var result = await UploadBatchAsync(pending, stoppingToken);
                if (result.Success)
                {
                    await _buffer.MarkSentAsync(pending.Select(e => e.Id!.Value), stoppingToken);
                    // Ramp up slightly on success
                    _successStreak++;
                    if (_successStreak >= 2)
                    {
                        _currentBatchSize = Math.Min(_config.BatchSize, _currentBatchSize + 10);
                        _currentIntervalSeconds = Math.Max(20, _currentIntervalSeconds - 5);
                    }
                }
                else
                {
                    await _buffer.MarkFailedAsync(pending.Where(e => e.Id.HasValue).Select(e => e.Id!.Value), "upload_failed", stoppingToken);
                    _successStreak = 0;
                    // Adjust rate on throttle/health
                    if (result.IsRateLimited)
                    {
                        _currentBatchSize = Math.Max(10, _currentBatchSize / 2);
                        _currentIntervalSeconds = Math.Min(300, _currentIntervalSeconds * 2);
                    }
                    else if (result.IsNetworkError)
                    {
                        _currentIntervalSeconds = Math.Min(300, _currentIntervalSeconds + 15);
                        _currentBatchSize = Math.Max(10, _currentBatchSize / 2);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Upload loop iteration failed");
                _successStreak = 0;
            }

            // Add small jitter each loop
            var loopDelay = _currentIntervalSeconds + _random.Next(0, 5);
            await Task.Delay(TimeSpan.FromSeconds(loopDelay), stoppingToken);
        }
    }

    private void LoadCredentials()
    {
        var creds = _credentialStore.Load();
        if (creds.HasValue)
        {
            _workstationId = creds.Value.workstationId;
            _apiKey = creds.Value.apiKey;
            _logger.LogInformation("Loaded workstation credentials from Credential Manager.");
        }
        else
        {
            _logger.LogWarning("Workstation credentials not found in Credential Manager; uploads will be skipped.");
        }
    }

    private bool HasCredentials() =>
        !string.IsNullOrWhiteSpace(_workstationId) && !string.IsNullOrWhiteSpace(_apiKey);

    private async Task<UploadResult> UploadBatchAsync(IReadOnlyList<AdherenceEvent> events, CancellationToken token)
    {
        var client = _httpClientFactory.CreateClient("adherence");
        // Ensure trailing slash so relative path combines to /api/adherence/events
        client.BaseAddress ??= new Uri(_config.ApiEndpoint.TrimEnd('/') + "/");

        var payload = new
        {
            events = events.Select(e => new
            {
                event_type = e.EventType,
                event_timestamp = e.EventTimestampUtc,
                application_name = e.ApplicationName,
                application_path = e.ApplicationPath,
                window_title = e.WindowTitle,
                is_work_application = e.IsWorkApplication,
                metadata = e.Metadata
            })
        };

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var contentString = JsonSerializer.Serialize(payload, jsonOptions);

        for (int attempt = 0; attempt <= _backoffSeconds.Length; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "adherence/events")
                {
                    Content = new StringContent(contentString, System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-API-Key", _apiKey);
                request.Headers.Add("X-Workstation-ID", _workstationId);

                var response = await client.SendAsync(request, token);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Uploaded {Count} events.", events.Count);
                    return UploadResult.SuccessResult();
                }

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(_backoffSeconds[Math.Min(attempt, _backoffSeconds.Length - 1)]);
                    var jitter = TimeSpan.FromSeconds(_random.Next(0, 5));
                    var delay = retryAfter + jitter;
                    _logger.LogWarning("Upload throttled ({Status}). Retrying in {Delay}s", response.StatusCode, delay.TotalSeconds);
                    await Task.Delay(delay, token);
                    continue;
                }

                _logger.LogWarning("Upload failed with status {Status}", response.StatusCode);
                return UploadResult.Failed(response.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                var delay = TimeSpan.FromSeconds(_backoffSeconds[Math.Min(attempt, _backoffSeconds.Length - 1)]) + TimeSpan.FromSeconds(_random.Next(0, 5));
                _logger.LogWarning(ex, "Network error during upload. Retrying in {Delay}s", delay.TotalSeconds);
                await Task.Delay(delay, token);
                continue;
            }
        }

        return UploadResult.Failed(null);
    }

    private record UploadResult(bool Success, bool IsRateLimited, bool IsNetworkError, HttpStatusCode? StatusCode)
    {
        public static UploadResult SuccessResult() => new(true, false, false, null);
        public static UploadResult RateLimited(HttpStatusCode code) => new(false, true, false, code);
        public static UploadResult NetworkError() => new(false, false, true, null);
        public static UploadResult Failed(HttpStatusCode? code) => new(false, false, false, code);
    }
}


