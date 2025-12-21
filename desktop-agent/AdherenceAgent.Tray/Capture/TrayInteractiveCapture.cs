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
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Threading.Timer _statsTimer;
    private long _windowOk, _windowFail, _browserOk, _browserFail, _teamsOk, _teamsFail;
    private int _windowLogged, _browserLogged, _teamsLogged;

    private readonly ActiveWindowMonitor _activeWindow;
    private readonly BrowserTabMonitor _browserTabs;
    private readonly TeamsMonitor _teams;

    public TrayInteractiveCapture(AgentConfig config, IEventBuffer buffer, ClassificationCache classificationCache, ClientWebsiteCache clientWebsiteCache, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _buffer = buffer;
        _classificationCache = classificationCache;
        _clientWebsiteCache = clientWebsiteCache;

        _activeWindow = new ActiveWindowMonitor(
            _buffer,
            _classificationCache,
            () => Interlocked.Increment(ref _windowOk),
            ex => LogFailureOnce("window", ex, ref _windowFail, ref _windowLogged));
        _browserTabs = new BrowserTabMonitor(
            _buffer,
            _clientWebsiteCache,
            () => Interlocked.Increment(ref _browserOk),
            ex => LogFailureOnce("browser", ex, ref _browserFail, ref _browserLogged));
        _teams = new TeamsMonitor(
            _buffer,
            () => Interlocked.Increment(ref _teamsOk),
            ex => LogFailureOnce("teams", ex, ref _teamsFail, ref _teamsLogged));

        _statsTimer = new System.Threading.Timer(_ => WriteStats(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
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
        _statsTimer.Dispose();
        _cts.Dispose();
    }

    private void WriteStats()
    {
        try
        {
            var logPath = Path.Combine(PathProvider.LogsDirectory, "tray.log");
            var line =
                $"{DateTime.UtcNow:o} capture-stats window(ok={_windowOk},fail={_windowFail}) " +
                $"browser(ok={_browserOk},fail={_browserFail}) teams(ok={_teamsOk},fail={_teamsFail})\n";
            File.AppendAllText(logPath, line);
        }
        catch { }
    }

    private void LogFailureOnce(string kind, Exception ex, ref long failCounter, ref int loggedCounter)
    {
        Interlocked.Increment(ref failCounter);
        // Log only first few failures per kind to avoid spamming.
        if (Interlocked.Increment(ref loggedCounter) <= 5)
        {
            try
            {
                var logPath = Path.Combine(PathProvider.LogsDirectory, "tray.log");
                File.AppendAllText(logPath, $"{DateTime.UtcNow:o} {kind}-capture-fail: {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { }
        }
    }

    // ---- Minimal monitor implementations (tray-side) ----

    private sealed class ActiveWindowMonitor
    {
        private readonly IEventBuffer _buffer;
        private readonly ClassificationCache _classificationCache;
        private readonly Action _ok;
        private readonly Action<Exception> _fail;
        private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
        private System.Threading.Timer? _timer;
        private IntPtr _lastHandle = IntPtr.Zero;
        private string? _lastWindowTitle;
        private string? _lastProcessPath;

        // Browser process names (covered by BrowserTabMonitor)
        private static readonly string[] BrowserProcessNames = { "chrome", "msedge", "firefox", "brave", "opera" };
        // Teams process names (covered by TeamsMonitor)
        private static readonly string[] TeamsProcessNames = { "Teams", "ms-teams" };

        public ActiveWindowMonitor(IEventBuffer buffer, ClassificationCache classificationCache, Action ok, Action<Exception> fail)
        {
            _buffer = buffer;
            _classificationCache = classificationCache;
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

                if (title == _lastWindowTitle && procPath == _lastProcessPath)
                {
                    _lastHandle = hWnd;
                    return;
                }

                _lastHandle = hWnd;
                _lastWindowTitle = title;
                _lastProcessPath = procPath;

                var isWork = ApplicationClassifier.ClassifyApplication(
                    procName,
                    procPath,
                    title,
                    _classificationCache.GetClassifications());

                await _buffer.AddAsync(new AdherenceEvent
                {
                    EventType = EventTypes.WindowChange,
                    EventTimestampUtc = DateTime.UtcNow,
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
            }
            catch (Exception ex)
            {
                // keep tray stable; errors are non-fatal
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
        private readonly Action _ok;
        private readonly Action<Exception> _fail;
        private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
        private readonly object _lock = new();
        private System.Threading.Timer? _timer;
        private string? _lastTitle;
        private string? _lastDomain;
        private DateTime _lastEventUtc = DateTime.MinValue;
        private readonly TimeSpan _minEventInterval = TimeSpan.FromSeconds(5);

        private static readonly string[] BrowserProcessNames = { "chrome", "msedge", "firefox", "brave", "opera" };

        public BrowserTabMonitor(IEventBuffer buffer, ClientWebsiteCache clientWebsiteCache, Action ok, Action<Exception> fail)
        {
            _buffer = buffer;
            _clientWebsiteCache = clientWebsiteCache;
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
                    EventTimestampUtc = DateTime.UtcNow,
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
            var domainMatch = Regex.Match(title, @"([a-zA-Z0-9][a-zA-Z0-9\-]*[a-zA-Z0-9]*\.(?:[a-zA-Z]{2,}))", RegexOptions.IgnoreCase);
            if (domainMatch.Success) return (null, domainMatch.Value);
            
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
                    EventTimestampUtc = DateTime.UtcNow,
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
                            EventTimestampUtc = DateTime.UtcNow,
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
                        EventTimestampUtc = DateTime.UtcNow,
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
                        EventTimestampUtc = DateTime.UtcNow,
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

