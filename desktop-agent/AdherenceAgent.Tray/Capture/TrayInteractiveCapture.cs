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
using Microsoft.Extensions.Logging.Abstractions;

namespace AdherenceAgent.Tray.Capture;

/// <summary>
/// Runs interactive capture monitors inside the user session (tray process).
/// This is required because services run in Session 0 and can't reliably access foreground windows/titles.
/// </summary>
public sealed class TrayInteractiveCapture : IDisposable
{
    private readonly ILogger _logger;
    private readonly IEventBuffer _buffer;
    private readonly ClassificationCache _classificationCache;
    private readonly ClientWebsiteCache _clientWebsiteCache;
    private readonly CallingAppCache _callingAppCache;
    private readonly CancellationTokenSource _cts = new();

    private readonly ActiveWindowMonitor _activeWindow;
    private readonly BrowserTabMonitor _browserTabs;
    private readonly TeamsMonitor _teams;

    public TrayInteractiveCapture(AgentConfig config, IEventBuffer buffer, ClassificationCache classificationCache, ClientWebsiteCache clientWebsiteCache, CallingAppCache callingAppCache, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _buffer = buffer;
        _classificationCache = classificationCache;
        _clientWebsiteCache = clientWebsiteCache;
        _callingAppCache = callingAppCache;

        _activeWindow = new ActiveWindowMonitor(
            _buffer,
            _classificationCache,
            _callingAppCache,
            () => { }, // Success callback (no-op)
            ex => { }); // Failure callback (silent - errors handled by monitors)
        _browserTabs = new BrowserTabMonitor(
            _buffer,
            _clientWebsiteCache,
            _callingAppCache,
            () => { }, // Success callback (no-op)
            ex => { }, // Failure callback (silent - errors handled by monitors)
            _logger);
        _teams = new TeamsMonitor(
            _buffer,
            () => { }, // Success callback (no-op)
            ex => { }); // Failure callback (silent - errors handled by monitors)
    }

    public void Start()
    {
        var token = _cts.Token;
        _activeWindow.Start(token);
        _browserTabs.Start(token);
        _teams.Start(token);
        _logger.LogInformation("Tray interactive capture started (window/browser/teams).");
    }

    public void Stop()
    {
        _cts.Cancel();
        _activeWindow.Stop();
        _browserTabs.Stop();
        _teams.Stop();
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    // ---- Minimal monitor implementations (tray-side) ----

    private sealed class ActiveWindowMonitor
    {
        private readonly IEventBuffer _buffer;
        private readonly ClassificationCache _classificationCache;
        private readonly CallingAppCache _callingAppCache;
        private readonly Action _ok;
        private readonly Action<Exception> _fail;
        private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
        private readonly TimeSpan _minEventInterval = TimeSpan.FromSeconds(3); // Throttle: max 1 event per 3 seconds
        private System.Threading.Timer? _timer;
        private IntPtr _lastHandle = IntPtr.Zero;
        private string? _lastWindowTitle;
        private string? _lastProcessPath;
        private DateTime _lastEventTime = DateTime.MinValue;

        // Browser process names (covered by BrowserTabMonitor)
        private static readonly string[] BrowserProcessNames = { "chrome", "msedge", "firefox", "brave", "opera" };
        // Teams process names (covered by TeamsMonitor)
        private static readonly string[] TeamsProcessNames = { "Teams", "ms-teams" };
        // Processes to skip for WINDOW_CHANGE events (system/installer processes)
        private static readonly string[] SkipWindowChangeProcesses = { "msiexec", "explorer" };

        public ActiveWindowMonitor(IEventBuffer buffer, ClassificationCache classificationCache, CallingAppCache callingAppCache, Action ok, Action<Exception> fail)
        {
            _buffer = buffer;
            _classificationCache = classificationCache;
            _callingAppCache = callingAppCache;
            _ok = ok;
            _fail = fail;
        }

        public void Start(CancellationToken token)
        {
            _timer = new System.Threading.Timer(async _ => await TickAsync(token), null, TimeSpan.Zero, _pollInterval);
        }

        public void Stop()
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _timer?.Dispose();
        }

        private async Task TickAsync(CancellationToken token)
        {
            try
            {
                var hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero || hWnd == _lastHandle) return;

                var title = GetWindowTitle(hWnd);
                var (procName, procPath) = GetProcessInfo(hWnd);

                if (IsMatch(procName, BrowserProcessNames) || IsMatch(procName, TeamsProcessNames)) return;
                
                // Skip system/installer processes for WINDOW_CHANGE events
                if (IsMatch(procName, SkipWindowChangeProcesses)) return;

                if (title == _lastWindowTitle && procPath == _lastProcessPath)
                {
                    _lastHandle = hWnd;
                    return;
                }

                // Throttle: don't create events more frequently than minEventInterval
                var now = DateTime.UtcNow;
                if ((now - _lastEventTime) < _minEventInterval)
                {
                    // Update last seen but don't create event yet (will be picked up on next poll if still changed)
                    _lastHandle = hWnd;
                    _lastWindowTitle = title;
                    _lastProcessPath = procPath;
                    return;
                }

                _lastHandle = hWnd;
                _lastWindowTitle = title;
                _lastProcessPath = procPath;
                _lastEventTime = now;

                var isWork = ApplicationClassifier.ClassifyApplication(
                    procName,
                    procPath,
                    title,
                    _classificationCache.GetClassifications());

                await _buffer.AddAsync(new AdherenceEvent
                {
                    EventType = EventTypes.WindowChange,
                    EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                    NtAccount = WindowsIdentityHelper.GetCurrentNtAccount(),
                    ApplicationName = procName,
                    ApplicationPath = procPath,
                    WindowTitle = title,
                    IsWorkApplication = isWork,
                    Metadata = new Dictionary<string, object>
                    {
                        { "process_name", procName ?? string.Empty },
                        { "process_path", procPath ?? string.Empty }
                    }
                }, token);
                _ok();

                // Check if this is a desktop calling app and create calling app events
                await CheckAndCreateDesktopCallingAppEventAsync(procName, title, token);
            }
            catch (Exception ex)
            {
                // keep tray stable; errors are non-fatal
                _fail(ex);
            }
        }

        private async Task CheckAndCreateDesktopCallingAppEventAsync(string? processName, string? windowTitle, CancellationToken token)
        {
            try
            {
                // Get calling apps from cache
                var callingApps = _callingAppCache.GetCallingApps();
                if (callingApps == null || callingApps.Count == 0)
                {
                    return; // No calling apps configured
                }

                // Check for desktop calling apps
                var matchingApp = CallingAppMatcher.FindMatchingDesktopCallingApp(processName, windowTitle, callingApps);
                if (matchingApp != null)
                {
                    // Detect call status from window title
                    var callStatus = CallingAppMatcher.DetectCallStatus(windowTitle, matchingApp);
                    
                    // Use "idle" as default if call status cannot be determined
                    // This ensures we track calling app usage even when status is unknown
                    if (string.IsNullOrWhiteSpace(callStatus))
                    {
                        callStatus = "idle";
                    }

                    // Create CALLING_APP_IN_CALL event when calling app is detected
                    await _buffer.AddAsync(new AdherenceEvent
                    {
                        EventType = EventTypes.CallingAppInCall,
                        EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                        NtAccount = WindowsIdentityHelper.GetCurrentNtAccount(),
                        ApplicationName = processName,
                        WindowTitle = windowTitle,
                        Metadata = new Dictionary<string, object>
                        {
                            { "app_name", matchingApp.AppName },
                            { "app_type", matchingApp.AppType },
                            { "client_name", matchingApp.ClientName ?? string.Empty },
                            { "call_status", callStatus },
                            { "process_name", processName ?? string.Empty }
                        }
                    }, token);
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail - calling app detection is non-critical
                _fail(ex);
            }
        }

        private static bool IsMatch(string? name, string[] needles)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (var n in needles)
            {
                if (name.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static string? GetWindowTitle(IntPtr hWnd)
        {
            var length = GetWindowTextLength(hWnd);
            var builder = new StringBuilder(length + 1);
            GetWindowText(hWnd, builder, builder.Capacity);
            return builder.ToString();
        }

        private static (string? name, string? path) GetProcessInfo(IntPtr hWnd)
        {
            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                using var proc = Process.GetProcessById((int)pid);
                var name = proc.ProcessName;
                string? path = null;
                try { path = proc.MainModule?.FileName; } catch { }
                return (name, path);
            }
            catch { return (null, null); }
        }
    }

    private sealed class BrowserTabMonitor
    {
        private readonly IEventBuffer _buffer;
        private readonly ClientWebsiteCache _clientWebsiteCache;
        private readonly CallingAppCache _callingAppCache;
        private readonly Action _ok;
        private readonly Action<Exception> _fail;
        private readonly ILogger? _logger;
        private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
        private readonly object _lock = new();
        private System.Threading.Timer? _timer;
        private string? _lastTitle;
        private string? _lastDomain;
        private DateTime _lastEventUtc = DateTime.MinValue;
        private readonly TimeSpan _minEventInterval = TimeSpan.FromSeconds(5);

        private static readonly string[] BrowserProcessNames = { "chrome", "msedge", "firefox", "brave", "opera" };

        public BrowserTabMonitor(IEventBuffer buffer, ClientWebsiteCache clientWebsiteCache, CallingAppCache callingAppCache, Action ok, Action<Exception> fail, ILogger? logger = null)
        {
            _buffer = buffer;
            _clientWebsiteCache = clientWebsiteCache;
            _callingAppCache = callingAppCache;
            _ok = ok;
            _fail = fail;
            _logger = logger;
        }

        public void Start(CancellationToken token)
        {
            _timer = new System.Threading.Timer(async _ => await TickAsync(token), null, TimeSpan.Zero, _pollInterval);
        }

        public void Stop()
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _timer?.Dispose();
        }

        private async Task TickAsync(CancellationToken token)
        {
            try
            {
                var hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero) return;

                var (procName, title) = GetProcessInfo(hWnd);
                if (!IsBrowser(procName))
                {
                    lock (_lock) { _lastTitle = null; _lastDomain = null; }
                    return;
                }

                bool shouldEmit = false;
                string? domain = null;
                string? url = null;
                lock (_lock)
                {
                    if (title != _lastTitle)
                    {
                        var extracted = ExtractUrlAndDomainFromTitle(title);
                        url = extracted.url;
                        domain = extracted.domain;
                        var now = DateTime.UtcNow;
                        var domainChanged = domain != _lastDomain;
                        var enoughTime = (now - _lastEventUtc) >= _minEventInterval;
                        if (domainChanged || enoughTime)
                        {
                            _lastTitle = title;
                            _lastDomain = domain;
                            _lastEventUtc = now;
                            shouldEmit = true;
                        }
                        else
                        {
                            _lastTitle = title;
                        }
                    }
                }

                if (!shouldEmit) return;

                await _buffer.AddAsync(new AdherenceEvent
                {
                    EventType = EventTypes.BrowserTabChange,
                    EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                    NtAccount = WindowsIdentityHelper.GetCurrentNtAccount(),
                    ApplicationName = procName,
                    WindowTitle = title,
                    Metadata = new Dictionary<string, object>
                    {
                        { "url", url ?? string.Empty },
                        { "domain", domain ?? string.Empty },
                        { "window_title", title ?? string.Empty }
                    }
                }, token);
                _ok();

                // Check if this is a client website and create CLIENT_WEBSITE_ACCESS event
                await CheckAndCreateClientWebsiteEventAsync(domain, url, title, token);

                // Check if this is a calling app and create calling app events
                await CheckAndCreateCallingAppEventAsync(domain, url, title, procName, token);
            }
            catch (Exception ex)
            {
                _fail(ex);
            }
        }

        private static bool IsBrowser(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (var b in BrowserProcessNames)
            {
                if (name.Contains(b, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static (string? url, string? domain) ExtractUrlAndDomainFromTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return (null, null);
            
            // Try to extract URL directly from title
            var urlMatch = Regex.Match(title, @"https?://([^\s\-|]+)", RegexOptions.IgnoreCase);
            if (urlMatch.Success)
            {
                var url = urlMatch.Value;
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    return (url, uri.Host);
                }
            }
            
            // Try to extract domain from common patterns
            // Improved regex: matches full domain including subdomain and TLD
            // Pattern: (subdomain.)domain.tld where tld is 2+ letters
            // Examples: app.oyorooms.com, mycompany.salesforce.com
            var domainPattern = @"([a-zA-Z0-9](?:[a-zA-Z0-9\-]*[a-zA-Z0-9])*(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9\-]*[a-zA-Z0-9])*)*\.[a-zA-Z]{2,})";
            var domainMatches = Regex.Matches(title, domainPattern, RegexOptions.IgnoreCase);
            
            // Return the longest match (most complete domain)
            if (domainMatches.Count > 0)
            {
                string? longestDomain = null;
                foreach (Match match in domainMatches)
                {
                    var domain = match.Value;
                    if (longestDomain == null || domain.Length > longestDomain.Length)
                    {
                        longestDomain = domain;
                    }
                }
                return (null, longestDomain);
            }
            
            return (null, null);
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
            catch { return (null, null); }
        }

        private async Task CheckAndCreateClientWebsiteEventAsync(string? domain, string? url, string? title, CancellationToken token)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(domain))
                {
                    return; // No domain to check
                }

                // Get client websites from cache
                var clientWebsites = _clientWebsiteCache.GetClientWebsites();
                if (clientWebsites == null || clientWebsites.Count == 0)
                {
                    return; // No client websites configured
                }

                // Find matching client website
                var matchingWebsite = ClientWebsiteMatcher.FindMatchingClientWebsite(domain, url, clientWebsites);
                if (matchingWebsite == null)
                {
                    return; // Not a client website
                }

                // Create CLIENT_WEBSITE_ACCESS event
                await _buffer.AddAsync(new AdherenceEvent
                {
                    EventType = EventTypes.ClientWebsiteAccess,
                    EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                    NtAccount = WindowsIdentityHelper.GetCurrentNtAccount(),
                    WindowTitle = title,
                    Metadata = new Dictionary<string, object>
                    {
                        { "client_name", matchingWebsite.ClientName },
                        { "domain", domain ?? string.Empty },
                        { "url", url ?? string.Empty },
                        { "website_url", matchingWebsite.WebsiteUrl }
                    }
                }, token);
            }
            catch (Exception ex)
            {
                // Log but don't fail - client website detection is non-critical
                _fail(ex);
            }
        }

        private async Task CheckAndCreateCallingAppEventAsync(string? domain, string? url, string? title, string? processName, CancellationToken token)
        {
            try
            {
                // Get calling apps from cache
                var callingApps = _callingAppCache.GetCallingApps();
                if (callingApps == null || callingApps.Count == 0)
                {
                    _logger?.LogDebug("No calling apps configured in cache");
                    return; // No calling apps configured
                }

                _logger?.LogDebug("Checking calling app match for domain: {Domain}, url: {Url}, title: {Title}", domain, url, title);

                // Check for web-based calling apps
                // Note: We check even if domain/url are empty, as window title pattern matching can still work
                var matchingApp = CallingAppMatcher.FindMatchingWebCallingApp(domain, url, title, callingApps);
                if (matchingApp != null)
                {
                    _logger?.LogDebug("Found matching calling app: {AppName} (Type: {AppType}, Client: {ClientName})", 
                        matchingApp.AppName, matchingApp.AppType, matchingApp.ClientName ?? "None");

                    // Detect call status from window title
                    var callStatus = CallingAppMatcher.DetectCallStatus(title, matchingApp);
                    
                    // Use "idle" as default if call status cannot be determined
                    // This ensures we track calling app usage even when status is unknown
                    if (string.IsNullOrWhiteSpace(callStatus))
                    {
                        callStatus = "idle";
                        _logger?.LogDebug("Call status not detected, defaulting to 'idle'");
                    }
                    else
                    {
                        _logger?.LogDebug("Detected call status: {CallStatus}", callStatus);
                    }

                    // Create CALLING_APP_IN_CALL event when calling app is detected
                    await _buffer.AddAsync(new AdherenceEvent
                    {
                        EventType = EventTypes.CallingAppInCall,
                        EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                        NtAccount = WindowsIdentityHelper.GetCurrentNtAccount(),
                        ApplicationName = processName,
                        WindowTitle = title,
                        Metadata = new Dictionary<string, object>
                        {
                            { "app_name", matchingApp.AppName },
                            { "app_type", matchingApp.AppType },
                            { "client_name", matchingApp.ClientName ?? string.Empty },
                            { "call_status", callStatus },
                            { "domain", domain ?? string.Empty },
                            { "url", url ?? string.Empty }
                        }
                    }, token);
                    
                    _logger?.LogDebug("Created CALLING_APP_IN_CALL event for {AppName}", matchingApp.AppName);
                }
                else
                {
                    _logger?.LogDebug("No matching calling app found for domain: {Domain}, title: {Title}", domain, title);
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail - calling app detection is non-critical
                _logger?.LogError(ex, "Error checking calling app for domain: {Domain}", domain);
                _fail(ex);
            }
        }
    }

    private sealed class TeamsMonitor
    {
        private readonly IEventBuffer _buffer;
        private readonly Action _ok;
        private readonly Action<Exception> _fail;
        private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(3);
        private System.Threading.Timer? _timer;
        private bool _isMeeting;
        private string? _lastTitle;

        private static readonly string[] TeamsProcessNames = { "Teams", "ms-teams" };

        public TeamsMonitor(IEventBuffer buffer, Action ok, Action<Exception> fail)
        {
            _buffer = buffer;
            _ok = ok;
            _fail = fail;
        }

        public void Start(CancellationToken token)
        {
            _timer = new System.Threading.Timer(async _ => await TickAsync(token), null, TimeSpan.Zero, _pollInterval);
        }

        public void Stop()
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _timer?.Dispose();
        }

        private async Task TickAsync(CancellationToken token)
        {
            try
            {
                var hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero) return;

                var (procName, title) = GetProcessInfo(hWnd);
                if (!IsTeams(procName))
                {
                    // leaving Teams
                    if (_isMeeting)
                    {
                        _isMeeting = false;
                        await _buffer.AddAsync(new AdherenceEvent
                        {
                            EventType = EventTypes.TeamsMeetingEnd,
                            EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                            NtAccount = WindowsIdentityHelper.GetCurrentNtAccount(),
                            ApplicationName = "Teams",
                            IsWorkApplication = true
                        }, token);
                        _ok();
                    }
                    _lastTitle = null;
                    return;
                }

                if (title == _lastTitle) return;
                _lastTitle = title;

                var meeting = !string.IsNullOrEmpty(title) &&
                              (title.Contains("Meeting", StringComparison.OrdinalIgnoreCase) ||
                               title.Contains("in a call", StringComparison.OrdinalIgnoreCase) ||
                               title.Contains("on a call", StringComparison.OrdinalIgnoreCase));

                if (meeting && !_isMeeting)
                {
                    _isMeeting = true;
                    await _buffer.AddAsync(new AdherenceEvent
                    {
                        EventType = EventTypes.TeamsMeetingStart,
                        EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                        NtAccount = WindowsIdentityHelper.GetCurrentNtAccount(),
                        ApplicationName = "Teams",
                        WindowTitle = title,
                        IsWorkApplication = true
                    }, token);
                    _ok();
                }
                else if (!meeting && _isMeeting)
                {
                    _isMeeting = false;
                    await _buffer.AddAsync(new AdherenceEvent
                    {
                        EventType = EventTypes.TeamsMeetingEnd,
                        EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                        NtAccount = WindowsIdentityHelper.GetCurrentNtAccount(),
                        ApplicationName = "Teams",
                        IsWorkApplication = true
                    }, token);
                    _ok();
                }
            }
            catch (Exception ex)
            {
                _fail(ex);
            }
        }

        private static bool IsTeams(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (var t in TeamsProcessNames)
            {
                if (name.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
            catch { return (null, null); }
        }
    }
}

