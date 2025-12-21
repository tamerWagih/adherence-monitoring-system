using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Helpers;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Capture;

/// <summary>
/// Monitors browser tab changes by detecting browser processes and parsing window titles.
/// Extracts URL/domain information from browser window titles.
/// </summary>
public class BrowserTabMonitor
{
    private readonly ILogger<BrowserTabMonitor> _logger;
    private readonly IEventBuffer _buffer;
    private readonly TimeSpan _pollInterval;
    private Timer? _timer;
    private readonly object _lock = new object();
    private string? _lastBrowserWindowTitle;
    private string? _lastBrowserProcess;
    private string? _lastUrl;
    private string? _lastDomain;
    private DateTime _lastEventTime = DateTime.MinValue;
    private readonly TimeSpan _minEventInterval = TimeSpan.FromSeconds(5); // Minimum 5 seconds between events for same domain

    // Browser process names
    private static readonly string[] BrowserProcessNames = 
    {
        "chrome",
        "msedge",
        "firefox",
        "brave",
        "opera"
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public BrowserTabMonitor(
        ILogger<BrowserTabMonitor> logger,
        IEventBuffer buffer,
        AgentConfig config)
    {
        _logger = logger;
        _buffer = buffer;
        _pollInterval = TimeSpan.FromSeconds(10); // Poll every 10 seconds
    }

    public void Start(CancellationToken token)
    {
        _timer = new Timer(async _ => await TickAsync(token), null, TimeSpan.Zero, _pollInterval);
        _logger.LogInformation("Browser tab monitoring started (interval {Interval}s).", _pollInterval.TotalSeconds);
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _logger.LogInformation("Browser tab monitoring stopped.");
    }

    private async Task TickAsync(CancellationToken token)
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            var (procName, windowTitle) = GetProcessInfo(hWnd);
            
            // Check if this is a browser process
            if (!IsBrowserProcess(procName))
            {
                // Reset tracking if we moved away from browser
                lock (_lock)
                {
                    if (_lastBrowserProcess != null)
                    {
                        _lastBrowserWindowTitle = null;
                        _lastBrowserProcess = null;
                        _lastUrl = null;
                        _lastDomain = null;
                    }
                }
                return;
            }

            // Check if window title changed (indicates tab change)
            // Use lock to prevent race conditions
            bool shouldCreateEvent = false;
            string? url = null;
            string? domain = null;
            
            lock (_lock)
            {
                if (windowTitle != _lastBrowserWindowTitle)
                {
                    // Extract URL and domain from window title
                    var extracted = ExtractUrlFromTitle(windowTitle);
                    url = extracted.url;
                    domain = extracted.domain;
                    
                    // Debounce: Only create event if:
                    // 1. Domain changed (different website), OR
                    // 2. Enough time has passed since last event (prevents spam from dynamic page updates)
                    var timeSinceLastEvent = DateTime.UtcNow - _lastEventTime;
                    bool domainChanged = domain != _lastDomain;
                    bool enoughTimePassed = timeSinceLastEvent >= _minEventInterval;
                    
                    if (domainChanged || enoughTimePassed)
                    {
                        // Update tracking state IMMEDIATELY to prevent race conditions
                        // This ensures we don't create duplicate events if TickAsync is called again
                        // before the async event creation completes
                        _lastBrowserWindowTitle = windowTitle;
                        _lastBrowserProcess = procName;
                        _lastUrl = url;
                        _lastDomain = domain;
                        _lastEventTime = DateTime.UtcNow;
                        shouldCreateEvent = true;
                    }
                    else
                    {
                        // Update title tracking but don't create event (debouncing)
                        _lastBrowserWindowTitle = windowTitle;
                        _lastBrowserProcess = procName;
                    }
                }
            }
            
            // Create event outside the lock to avoid blocking
            if (shouldCreateEvent)
            {
                await CreateBrowserTabChangeEventAsync(procName, windowTitle, url, domain, token);
                
                // Note: Client website detection is handled by TrayInteractiveCapture.BrowserTabMonitor
                // The service-side BrowserTabMonitor only creates BROWSER_TAB_CHANGE events
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BrowserTabMonitor tick failed: {Message}", ex.Message);
        }
    }

    private async Task CreateBrowserTabChangeEventAsync(
        string? processName, 
        string? windowTitle, 
        string? url, 
        string? domain, 
        CancellationToken token)
    {
        var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
        var evt = new AdherenceEvent
        {
            EventType = EventTypes.BrowserTabChange,
            EventTimestampUtc = DateTime.UtcNow,
            NtAccount = ntAccount,
            ApplicationName = processName,
            WindowTitle = windowTitle,
            Metadata = new Dictionary<string, object>
            {
                { "url", url ?? string.Empty },
                { "domain", domain ?? string.Empty },
                { "window_title", windowTitle ?? string.Empty }
            }
        };

        try
        {
            await _buffer.AddAsync(evt, token);
            _logger.LogDebug("Browser tab changed: {Domain} - {Title}", domain, windowTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store browser tab change event: {Domain}", domain);
        }
    }

    private static bool IsBrowserProcess(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        foreach (var browserName in BrowserProcessNames)
        {
            if (processName.Contains(browserName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static (string? url, string? domain) ExtractUrlFromTitle(string? windowTitle)
    {
        if (string.IsNullOrEmpty(windowTitle))
        {
            return (null, null);
        }

        // Common browser title patterns:
        // "Page Title - Browser Name"
        // "https://example.com - Browser Name"
        // "Page Title | https://example.com"
        // "Browser Name - Page Title"

        // Try to extract URL directly from title
        var urlMatch = Regex.Match(windowTitle, @"https?://([^\s\-|]+)", RegexOptions.IgnoreCase);
        if (urlMatch.Success)
        {
            var url = urlMatch.Value;
            var domain = ExtractDomain(url);
            return (url, domain);
        }

        // Try to extract domain from common patterns
        var domainMatch = Regex.Match(windowTitle, @"([a-zA-Z0-9][a-zA-Z0-9\-]*[a-zA-Z0-9]*\.(?:[a-zA-Z]{2,}))", RegexOptions.IgnoreCase);
        if (domainMatch.Success)
        {
            var domain = domainMatch.Value;
            return (null, domain);
        }

        return (null, null);
    }

    private static string? ExtractDomain(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return uri.Host;
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }

    private static (string? name, string? title) GetProcessInfo(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            using var proc = Process.GetProcessById((int)pid);
            var name = proc.ProcessName;

            var length = GetWindowTextLength(hWnd);
            var builder = new StringBuilder(length + 1);
            GetWindowText(hWnd, builder, builder.Capacity);
            var title = builder.ToString();

            return (name, title);
        }
        catch
        {
            return (null, null);
        }
    }
}
