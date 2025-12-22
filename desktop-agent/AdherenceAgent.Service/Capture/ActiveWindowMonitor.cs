using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Helpers;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Capture;

/// <summary>
/// Polls the active foreground window and emits WINDOW_CHANGE events when it changes.
/// Filters out browsers and Teams (covered by specialized monitors) to reduce redundancy.
/// Focuses on desktop applications (Excel, Word, Outlook, custom apps, etc.).
/// </summary>
public class ActiveWindowMonitor
{
    private readonly ILogger<ActiveWindowMonitor> _logger;
    private readonly IEventBuffer _buffer;
    private readonly ClassificationCache _classificationCache;
    private readonly TimeSpan _pollInterval;
    private Timer? _timer;
    private IntPtr _lastHandle = IntPtr.Zero;
    private string? _lastWindowTitle;
    private string? _lastProcessPath;

    // Browser process names (filtered - covered by BrowserTabMonitor)
    private static readonly string[] BrowserProcessNames = 
    {
        "chrome",
        "msedge",
        "firefox",
        "brave",
        "opera"
    };

    // Teams process names (filtered - covered by TeamsMonitor)
    private static readonly string[] TeamsProcessNames = { "Teams", "ms-teams" };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public ActiveWindowMonitor(
        ILogger<ActiveWindowMonitor> logger,
        IEventBuffer buffer,
        AgentConfig config,
        ClassificationCache classificationCache)
    {
        _logger = logger;
        _buffer = buffer;
        _classificationCache = classificationCache;
        // Use 60 seconds interval (filtered to desktop apps only, reducing event volume)
        _pollInterval = TimeSpan.FromSeconds(60);
    }

    public void Start(CancellationToken token)
    {
        _timer = new Timer(async _ => await TickAsync(token), null, TimeSpan.Zero, _pollInterval);
        _logger.LogInformation("Active window monitoring started (interval {Interval}s, filtering browsers and Teams).", _pollInterval.TotalSeconds);
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _logger.LogInformation("Active window monitoring stopped.");
    }

    private async Task TickAsync(CancellationToken token)
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero || hWnd == _lastHandle)
            {
                return;
            }

            var title = GetWindowTitle(hWnd);
            var (procName, procPath) = GetProcessInfo(hWnd);

            // Skip browsers - covered by BrowserTabMonitor
            if (IsBrowserProcess(procName))
            {
                return;
            }

            // Skip Teams - covered by TeamsMonitor
            if (IsTeamsProcess(procName))
            {
                return;
            }

            // Debounce identical window titles/paths even if handle changed (rare)
            if (title == _lastWindowTitle && procPath == _lastProcessPath)
            {
                _lastHandle = hWnd;
                return;
            }

            _lastHandle = hWnd;
            _lastWindowTitle = title;
            _lastProcessPath = procPath;

            // Classify application
            var classifications = _classificationCache.GetClassifications();
            var isWorkApplication = ApplicationClassifier.ClassifyApplication(
                procName,
                procPath,
                title,
                classifications);

            var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
            var evt = new AdherenceEvent
            {
                EventType = EventTypes.WindowChange,
                EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                NtAccount = ntAccount,
                ApplicationName = procName,
                ApplicationPath = procPath,
                WindowTitle = title,
                IsWorkApplication = isWorkApplication,
                Metadata = new Dictionary<string, object>
                {
                    { "process_name", procName ?? string.Empty },
                    { "process_path", procPath ?? string.Empty }
                }
            };

            await _buffer.AddAsync(evt, token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ActiveWindowMonitor tick failed.");
        }
    }

    private static string? GetWindowTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        var builder = new StringBuilder(length + 1);
        GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
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

    private static bool IsTeamsProcess(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        foreach (var teamsName in TeamsProcessNames)
        {
            if (processName.Contains(teamsName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static (string? name, string? path) GetProcessInfo(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            using var proc = Process.GetProcessById((int)pid);
            var name = proc.ProcessName;
            string? path = null;
            try
            {
                path = proc.MainModule?.FileName;
            }
            catch
            {
                // Access denied; skip path
            }
            return (name, path);
        }
        catch
        {
            return (null, null);
        }
    }
}

