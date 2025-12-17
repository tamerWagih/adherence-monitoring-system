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
/// Monitors Microsoft Teams for meeting and chat activity.
/// Detects Teams meeting windows and chat windows via window title patterns.
/// </summary>
public class TeamsMonitor
{
    private readonly ILogger<TeamsMonitor> _logger;
    private readonly IEventBuffer _buffer;
    private readonly TimeSpan _pollInterval;
    private Timer? _timer;
    private bool _isInMeeting;
    private bool _isChatActive;
    private DateTime? _meetingStartUtc;
    private DateTime? _chatStartUtc;
    private string? _lastTeamsWindowTitle;

    // Teams process names
    private static readonly string[] TeamsProcessNames = { "Teams", "ms-teams" };

    // Meeting window title patterns
    private static readonly string[] MeetingPatterns = 
    {
        "*Meeting*",
        "*in a call*",
        "*is presenting*",
        "*is sharing*",
        "*on a call*"
    };

    // Chat window title patterns (when not meeting)
    private static readonly string[] ChatPatterns = 
    {
        "*Chat*",
        "*Conversation*",
        "* - Microsoft Teams"
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public TeamsMonitor(
        ILogger<TeamsMonitor> logger,
        IEventBuffer buffer,
        AgentConfig config)
    {
        _logger = logger;
        _buffer = buffer;
        _pollInterval = TimeSpan.FromSeconds(30); // Poll every 30 seconds
        _isInMeeting = false;
        _isChatActive = false;
    }

    public void Start(CancellationToken token)
    {
        _timer = new Timer(async _ => await TickAsync(token), null, TimeSpan.Zero, _pollInterval);
        _logger.LogInformation("Teams monitoring started (interval {Interval}s).", _pollInterval.TotalSeconds);
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, TimeSpan.Zero);
        _timer?.Dispose();
        _logger.LogInformation("Teams monitoring stopped.");
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
            
            // Check if this is a Teams window
            if (!IsTeamsProcess(procName))
            {
                // If we were tracking Teams activity and window changed away, handle state transitions
                if (_isInMeeting || _isChatActive)
                {
                    await CheckTeamsStateTransitionAsync(null, string.Empty, token);
                }
                return;
            }

            // Check for state changes
            if (windowTitle != _lastTeamsWindowTitle)
            {
                _lastTeamsWindowTitle = windowTitle;
                await CheckTeamsStateTransitionAsync(procName, windowTitle ?? string.Empty, token);
            }
            else if (_isChatActive && !_isInMeeting && !string.IsNullOrEmpty(_lastTeamsWindowTitle))
            {
                // Continue tracking chat activity - create periodic event
                await CreateChatActiveEventAsync(token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TeamsMonitor tick failed.");
        }
    }

    private async Task CheckTeamsStateTransitionAsync(string? procName, string windowTitle, CancellationToken token)
    {
        if (string.IsNullOrEmpty(windowTitle))
        {
            // Window changed away from Teams - end meeting/chat if active
            if (_isInMeeting)
            {
                await EndMeetingAsync(token);
            }
            if (_isChatActive)
            {
                await EndChatAsync(token);
            }
            return;
        }

        bool isMeeting = IsMeetingWindow(windowTitle);
        bool isChat = IsChatWindow(windowTitle) && !isMeeting; // Chat but not meeting

        // Handle meeting state
        if (isMeeting && !_isInMeeting)
        {
            await StartMeetingAsync(windowTitle, token);
        }
        else if (!isMeeting && _isInMeeting)
        {
            await EndMeetingAsync(token);
        }

        // Handle chat state (only if not in meeting)
        if (isChat && !_isInMeeting && !_isChatActive)
        {
            await StartChatAsync(windowTitle, token);
        }
        else if (!isChat && _isChatActive)
        {
            await EndChatAsync(token);
        }
    }

    private async Task StartMeetingAsync(string windowTitle, CancellationToken token)
    {
        _isInMeeting = true;
        _meetingStartUtc = DateTime.UtcNow;
        
        var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
        var evt = new AdherenceEvent
        {
            EventType = EventTypes.TeamsMeetingStart,
            EventTimestampUtc = _meetingStartUtc.Value,
            NtAccount = ntAccount,
            ApplicationName = "Teams",
            WindowTitle = windowTitle,
            IsWorkApplication = true,
            Metadata = new Dictionary<string, object>
            {
                { "window_title", windowTitle ?? string.Empty }
            }
        };

        await _buffer.AddAsync(evt, token);
        _logger.LogInformation("Teams meeting started: {Title}", windowTitle);
    }

    private async Task EndMeetingAsync(CancellationToken token)
    {
        if (!_isInMeeting || !_meetingStartUtc.HasValue)
        {
            return;
        }

        var meetingDuration = DateTime.UtcNow - _meetingStartUtc.Value;
        _isInMeeting = false;
        
        var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
        var evt = new AdherenceEvent
        {
            EventType = EventTypes.TeamsMeetingEnd,
            EventTimestampUtc = DateTime.UtcNow,
            NtAccount = ntAccount,
            ApplicationName = "Teams",
            IsWorkApplication = true,
            Metadata = new Dictionary<string, object>
            {
                { "meeting_duration_minutes", (int)meetingDuration.TotalMinutes },
                { "meeting_start_time", _meetingStartUtc.Value }
            }
        };

        await _buffer.AddAsync(evt, token);
        _logger.LogInformation("Teams meeting ended. Duration: {Duration} minutes", meetingDuration.TotalMinutes);
        
        _meetingStartUtc = null;
    }

    private async Task StartChatAsync(string windowTitle, CancellationToken token)
    {
        _isChatActive = true;
        _chatStartUtc = DateTime.UtcNow;
        
        var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
        var evt = new AdherenceEvent
        {
            EventType = EventTypes.TeamsChatActive,
            EventTimestampUtc = _chatStartUtc.Value,
            NtAccount = ntAccount,
            ApplicationName = "Teams",
            WindowTitle = windowTitle,
            IsWorkApplication = true,
            Metadata = new Dictionary<string, object>
            {
                { "window_title", windowTitle ?? string.Empty }
            }
        };

        await _buffer.AddAsync(evt, token);
        _logger.LogDebug("Teams chat active: {Title}", windowTitle);
    }

    private async Task CreateChatActiveEventAsync(CancellationToken token)
    {
        // Create periodic chat activity event while chat is active
        var ntAccount = WindowsIdentityHelper.GetCurrentNtAccount();
        var evt = new AdherenceEvent
        {
            EventType = EventTypes.TeamsChatActive,
            EventTimestampUtc = DateTime.UtcNow,
            NtAccount = ntAccount,
            ApplicationName = "Teams",
            WindowTitle = _lastTeamsWindowTitle,
            IsWorkApplication = true,
            Metadata = new Dictionary<string, object>
            {
                { "window_title", _lastTeamsWindowTitle ?? string.Empty },
                { "chat_duration_minutes", _chatStartUtc.HasValue ? (int)(DateTime.UtcNow - _chatStartUtc.Value).TotalMinutes : 0 }
            }
        };

        await _buffer.AddAsync(evt, token);
    }

    private async Task EndChatAsync(CancellationToken token)
    {
        if (!_isChatActive)
        {
            return;
        }

        _isChatActive = false;
        _chatStartUtc = null;
        _logger.LogDebug("Teams chat ended");
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

    private static bool IsMeetingWindow(string windowTitle)
    {
        if (string.IsNullOrEmpty(windowTitle))
        {
            return false;
        }

        foreach (var pattern in MeetingPatterns)
        {
            if (MatchesPattern(windowTitle, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsChatWindow(string windowTitle)
    {
        if (string.IsNullOrEmpty(windowTitle))
        {
            return false;
        }

        // Check if it ends with " - Microsoft Teams" (typical chat window pattern)
        if (windowTitle.EndsWith(" - Microsoft Teams", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var pattern in ChatPatterns)
        {
            if (MatchesPattern(windowTitle, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPattern(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        // Convert wildcard pattern to regex
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        
        return Regex.IsMatch(text, regexPattern, RegexOptions.IgnoreCase);
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
