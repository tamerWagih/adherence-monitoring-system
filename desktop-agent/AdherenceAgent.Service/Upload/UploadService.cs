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
    private int _consecutiveNetworkErrors = 0;
    private const int NetworkOutageThreshold = 3; // Consecutive errors to consider outage

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
        // Initialize with staggered interval (will be recalculated after credentials loaded)
        _currentIntervalSeconds = Math.Max(45, _config.SyncIntervalSeconds);
        _successStreak = 0;
        _consecutiveNetworkErrors = 0;
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
            
            // Initialize staggered interval after credentials are loaded
            _currentIntervalSeconds = CalculateStaggeredInterval();
            _logger.LogInformation("Using staggered batch interval: {Interval}s (30-90s range)", _currentIntervalSeconds);
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
                    // Reset network error counter on success
                    _consecutiveNetworkErrors = 0;
                    
                    // Ramp up slightly on success
                    _successStreak++;
                    if (_successStreak >= 2)
                    {
                        _currentBatchSize = Math.Min(_config.BatchSize, _currentBatchSize + 10);
                        // Use staggered interval for recovery (30-90s range)
                        _currentIntervalSeconds = CalculateStaggeredInterval();
                    }
                }
                else
                {
                    // For 409 Conflict (unmapped NT), mark as failed permanently (don't retry)
                    var failedEvents = pending.Where(e => e.Id.HasValue).Select(e => e.Id!.Value);
                    var errorMessage = result.StatusCode == HttpStatusCode.Conflict 
                        ? "unmapped_nt_account" 
                        : "upload_failed";
                    await _buffer.MarkFailedAsync(failedEvents, errorMessage, stoppingToken);
                    _successStreak = 0;
                    
                    // Adjust rate on throttle/health (but not for 409 Conflict)
                    if (result.StatusCode == HttpStatusCode.Conflict)
                    {
                        _logger.LogWarning("Events marked as failed due to unmapped NT account. Contact admin to register NT account in employee_personal_info.");
                    }
                    else if (result.IsRateLimited)
                    {
                        // Reduce by 25% instead of 50% for more gradual reduction
                        _currentBatchSize = Math.Max(10, (int)(_currentBatchSize * 0.75));
                        _currentIntervalSeconds = Math.Min(300, (int)(_currentIntervalSeconds * 1.5));
                    }
                    else if (result.IsNetworkError)
                    {
                        // Reduce by 25% and increase interval
                        _currentBatchSize = Math.Max(10, (int)(_currentBatchSize * 0.75));
                        _currentIntervalSeconds = Math.Min(300, _currentIntervalSeconds + 15);
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

            // Use staggered interval (30-90 seconds) instead of fixed interval + small jitter
            var loopDelay = CalculateStaggeredInterval();
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

    /// <summary>
    /// Calculate staggered interval using workstation ID hash for natural distribution.
    /// Returns interval between 30-90 seconds.
    /// </summary>
    private int CalculateStaggeredInterval()
    {
        if (!HasCredentials())
        {
            // Fallback to default if no credentials
            return Math.Max(45, _config.SyncIntervalSeconds);
        }

        // Use workstation ID hash to create consistent distribution
        int hash = Math.Abs(_workstationId!.GetHashCode());
        int baseInterval = 30 + (hash % 60); // 30-89 seconds
        int jitter = _random.Next(0, 2); // 0-1 second jitter
        return baseInterval + jitter; // 30-90 seconds
    }

    private async Task<UploadResult> UploadBatchAsync(IReadOnlyList<AdherenceEvent> events, CancellationToken token)
    {
        var client = _httpClientFactory.CreateClient("adherence");
        // Ensure trailing slash so relative path combines to /api/adherence/events
        client.BaseAddress ??= new Uri(_config.ApiEndpoint.TrimEnd('/') + "/");

        // Validate all events have NT account before uploading
        var eventsWithoutNt = events.Where(e => string.IsNullOrWhiteSpace(e.NtAccount)).ToList();
        if (eventsWithoutNt.Any())
        {
            _logger.LogWarning("Skipping {Count} events without NT account", eventsWithoutNt.Count);
            // Filter out events without NT from batch
            var validEvents = events.Where(e => !string.IsNullOrWhiteSpace(e.NtAccount)).ToList();
            if (validEvents.Count == 0)
            {
                return UploadResult.Failed(null);
            }
            events = validEvents;
        }

        var payload = new
        {
            events = events.Select(e => new
            {
                event_type = e.EventType,
                event_timestamp = e.EventTimestampUtc,
                nt = e.NtAccount,
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
                    // Reset network error counter on successful upload
                    _consecutiveNetworkErrors = 0;
                    _logger.LogInformation("Uploaded {Count} events.", events.Count);
                    return UploadResult.SuccessResult();
                }

                // Handle 409 Conflict (unmapped NT account) - don't retry, mark as failed
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(token);
                    _logger.LogWarning("Upload rejected - unmapped NT account (409 Conflict). Response: {Response}", responseBody);
                    // Mark events as failed permanently (don't retry unmapped NT)
                    return UploadResult.Failed(response.StatusCode);
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
                _consecutiveNetworkErrors++;
                
                // Check if we've hit network outage threshold
                if (_consecutiveNetworkErrors >= NetworkOutageThreshold && HasCredentials())
                {
                    // Network outage detected - use staggered reconnection delay (0-300s)
                    int baseDelay = Math.Abs(_workstationId!.GetHashCode()) % 300; // 0-299
                    int jitter = _random.Next(0, 10); // 0-9 second jitter
                    int reconnectionDelay = baseDelay + jitter; // 0-309 seconds
                    
                    _logger.LogInformation(
                        "Network outage detected ({ConsecutiveErrors} consecutive errors). Staggered reconnection delay: {Delay}s",
                        _consecutiveNetworkErrors, reconnectionDelay);
                    
                    await Task.Delay(TimeSpan.FromSeconds(reconnectionDelay), token);
                }
                else
                {
                    // Normal retry with exponential backoff + jitter
                    var baseDelay = TimeSpan.FromSeconds(_backoffSeconds[Math.Min(attempt, _backoffSeconds.Length - 1)]);
                    var jitter = TimeSpan.FromSeconds(_random.Next(0, 5)); // 0-4 seconds jitter
                    var delay = baseDelay + jitter;
                    
                    _logger.LogWarning(ex, "Network error during upload (attempt {Attempt}). Retrying in {Delay}s", 
                        attempt + 1, delay.TotalSeconds);
                    await Task.Delay(delay, token);
                }
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


