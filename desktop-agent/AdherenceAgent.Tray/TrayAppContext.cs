using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Helpers;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.ServiceProcess;
using AdherenceAgent.Shared.Security;
using System.Security.Principal;
using System.Media;
using System.Linq;
using AdherenceAgent.Tray.Capture;

namespace AdherenceAgent.Tray;

public class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _iconGreen;
    private readonly Icon _iconYellow;
    private readonly Icon _iconRed;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _breakNotificationTimer;
    private readonly System.Windows.Forms.Timer _menuRefreshTimer;
    private AgentConfig _config = new();
    private readonly CredentialStore _credentialStore = new();
    private readonly BreakScheduleCache _breakScheduleCache = new();
    private readonly ClassificationCache _classificationCache = new();
    private readonly ClientWebsiteCache _clientWebsiteCache = new();
    private readonly CallingAppCache _callingAppCache = new();
    private const string ServiceName = "AdherenceAgentService";
    private long _lastProcessedBreakEventId = 0; // Track last processed break event to avoid duplicates
    private DateTime _trayAppStartTime = DateTime.UtcNow; // Track when tray app started to avoid showing old events
    private TrayInteractiveCapture? _interactiveCapture;
    private SQLiteEventBuffer? _trayBuffer;

    public TrayAppContext()
    {
        _iconGreen = CreateCircleIcon(Color.LimeGreen);
        _iconYellow = CreateCircleIcon(Color.Gold);
        _iconRed = CreateCircleIcon(Color.IndianRed);

        _notifyIcon = new NotifyIcon
        {
            Icon = _iconGreen,
            Text = "Adherence Agent",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        LoadConfig();
        UpdateStatusIcon(); // initial

        // Start interactive capture in the tray process (user session).
        // This fixes "MSI installed service only captures APP_START/END" due to Session 0 isolation.
        try
        {
            _trayBuffer = new SQLiteEventBuffer(_config, NullLogger<SQLiteEventBuffer>.Instance);
            // Retry initialization (service may hold the DB briefly on startup)
            var start = DateTime.UtcNow;
            Exception? last = null;
            while ((DateTime.UtcNow - start).TotalSeconds < 15)
            {
                try
                {
                    _trayBuffer.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    System.Threading.Thread.Sleep(500);
                }
            }
            if (last != null) throw last;

            _interactiveCapture = new TrayInteractiveCapture(_config, _trayBuffer, _classificationCache, _clientWebsiteCache, _callingAppCache);
            _interactiveCapture.Start();
        }
        catch (Exception)
        {
            // Keep tray stable even if capture fails; service/process monitoring still works.
            // Errors are logged by the service's agent.log file via Serilog.
        }

        _statusTimer = new System.Windows.Forms.Timer { Interval = 30_000 }; // 30s
        _statusTimer.Tick += (_, _) => UpdateStatusIcon();
        _statusTimer.Start();

        _breakNotificationTimer = new System.Windows.Forms.Timer { Interval = 10_000 }; // 10s
        _breakNotificationTimer.Tick += (_, _) => CheckBreakEvents();
        _breakNotificationTimer.Start();

        _menuRefreshTimer = new System.Windows.Forms.Timer { Interval = 60_000 }; // 60s - refresh menu every minute
        _menuRefreshTimer.Tick += (_, _) => RefreshMenu();
        _menuRefreshTimer.Start();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var isAdmin = IsCurrentUserAdministrator();
        
        // Status is always visible (read-only information)
        menu.Items.Add("Status", null, (_, _) => ShowStatus());
        
        // Add break schedule information
        AddBreakScheduleMenuItems(menu);
        
        menu.Items.Add(new ToolStripSeparator());
        
        // Set Credentials is available to all users (not just admins)
        menu.Items.Add("Set Credentials...", null, (_, _) => SetCredentials());
        
        // Admin-only actions (but hide Exit and Stop Service since any user can run as admin)
        if (isAdmin)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Test API Connection", null, async (_, _) => await TestApiAsync());
            menu.Items.Add("Add Test Event", null, async (_, _) => await AddTestEventAsync());
            menu.Items.Add("Start Service", null, (_, _) => ControlService(ServiceControllerStatus.Running));
            menu.Items.Add("Open Logs Folder", null, (_, _) => OpenLogs());
        }
        // Note: Exit and Stop Service are intentionally hidden - any user can run as admin,
        // so we don't want to allow stopping the service or exiting the tray app
        
        return menu;
    }

    /// <summary>
    /// Add break schedule menu items (upcoming and ongoing breaks).
    /// </summary>
    private void AddBreakScheduleMenuItems(ContextMenuStrip menu)
    {
        try
        {
            var schedules = _breakScheduleCache.GetSchedules();
            if (schedules.Count == 0)
            {
                return;
            }

            var currentTime = DateTime.Now;
            var currentTimeOfDay = currentTime.TimeOfDay;
            var upcomingBreaks = new List<BreakSchedule>();
            var ongoingBreaks = new List<BreakSchedule>();

            foreach (var schedule in schedules)
            {
                if (TryParseTime(schedule.StartTime, out var startTime) &&
                    TryParseTime(schedule.EndTime, out var endTime))
                {
                    if (currentTimeOfDay >= startTime && currentTimeOfDay <= endTime)
                    {
                        ongoingBreaks.Add(schedule);
                    }
                    else if (currentTimeOfDay < startTime)
                    {
                        upcomingBreaks.Add(schedule);
                    }
                }
            }

            // Sort upcoming breaks by start time
            upcomingBreaks = upcomingBreaks.OrderBy(b => TryParseTime(b.StartTime, out var st) ? st : TimeSpan.MaxValue).ToList();

            if (ongoingBreaks.Count > 0 || upcomingBreaks.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
                
                if (ongoingBreaks.Count > 0)
                {
                    menu.Items.Add("🟢 Ongoing Breaks:", null, null).Enabled = false;
                    foreach (var breakSchedule in ongoingBreaks)
                    {
                        var timeRemaining = "";
                        if (TryParseTime(breakSchedule.EndTime, out var endTime))
                        {
                            var remaining = endTime - currentTimeOfDay;
                            if (remaining.TotalMinutes > 0)
                            {
                                timeRemaining = $" ({remaining.TotalMinutes:F0} min remaining)";
                            }
                        }
                        menu.Items.Add($"  • {breakSchedule.StartTime} - {breakSchedule.EndTime}{timeRemaining}", null, null).Enabled = false;
                    }
                }

                if (upcomingBreaks.Count > 0)
                {
                    if (ongoingBreaks.Count > 0)
                    {
                        menu.Items.Add(new ToolStripSeparator());
                    }
                    menu.Items.Add("⏰ Upcoming Breaks:", null, null).Enabled = false;
                    foreach (var breakSchedule in upcomingBreaks.Take(5)) // Show max 5 upcoming
                    {
                        var timeUntil = "";
                        if (TryParseTime(breakSchedule.StartTime, out var startTime))
                        {
                            var until = startTime - currentTimeOfDay;
                            if (until.TotalMinutes > 0)
                            {
                                if (until.TotalHours >= 1)
                                {
                                    timeUntil = $" (in {until.Hours}h {until.Minutes}m)";
                                }
                                else
                                {
                                    timeUntil = $" (in {until.TotalMinutes:F0} min)";
                                }
                            }
                        }
                        menu.Items.Add($"  • {breakSchedule.StartTime} - {breakSchedule.EndTime}{timeUntil}", null, null).Enabled = false;
                    }
                }
            }
        }
        catch
        {
            // Silently ignore errors loading break schedules
        }
    }

    /// <summary>
    /// Parse time string (HH:mm:ss or HH:mm) to TimeSpan.
    /// </summary>
    private bool TryParseTime(string timeStr, out TimeSpan timeSpan)
    {
        timeSpan = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(timeStr))
        {
            return false;
        }

        var parts = timeStr.Split(':');
        if (parts.Length < 2)
        {
            return false;
        }

        if (int.TryParse(parts[0], out var hours) &&
            int.TryParse(parts[1], out var minutes))
        {
            var seconds = parts.Length > 2 && int.TryParse(parts[2], out var s) ? s : 0;
            timeSpan = new TimeSpan(hours, minutes, seconds);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Refresh the context menu (to update break schedule information).
    /// </summary>
    private void RefreshMenu()
    {
        // Force cache reload so new schedules/classifications reflect without restart.
        _breakScheduleCache.ClearCache();
        _notifyIcon.ContextMenuStrip = BuildMenu();
    }
    
    private static bool IsCurrentUserAdministrator()
    {
        try
        {
            var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // If we can't determine, assume non-admin for security
            return false;
        }
    }

    private void LoadConfig()
    {
        // Priority: env override -> ProgramData config.json -> Service appsettings.json -> defaults
        var envApi = Environment.GetEnvironmentVariable("AGENT_API_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(envApi))
        {
            _config.ApiEndpoint = envApi!;
        }

        // ProgramData config.json (if present)
        try
        {
            if (File.Exists(PathProvider.ConfigFile))
            {
                var json = File.ReadAllText(PathProvider.ConfigFile);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Agent", out var agent))
                {
                    _config.ApiEndpoint = agent.GetProperty("ApiEndpoint").GetString() ?? _config.ApiEndpoint;
                    _config.BatchSize = agent.GetProperty("BatchSize").GetInt32();
                    _config.SyncIntervalSeconds = agent.GetProperty("SyncIntervalSeconds").GetInt32();
                    _config.MaxBufferSize = agent.GetProperty("MaxBufferSize").GetInt32();
                    _config.MaxRetryAttempts = agent.GetProperty("MaxRetryAttempts").GetInt32();
                }
            }
        }
        catch { /* ignore */ }

        // Service appsettings.json (search up to solution root)
        try
        {
            var servicePath = FindServiceAppSettings();
            if (!string.IsNullOrEmpty(servicePath) && File.Exists(servicePath))
            {
                var json = File.ReadAllText(servicePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Agent", out var agent))
                {
                    _config.ApiEndpoint = agent.GetProperty("ApiEndpoint").GetString() ?? _config.ApiEndpoint;
                    _config.BatchSize = agent.GetProperty("BatchSize").GetInt32();
                    _config.SyncIntervalSeconds = agent.GetProperty("SyncIntervalSeconds").GetInt32();
                    _config.MaxBufferSize = agent.GetProperty("MaxBufferSize").GetInt32();
                    _config.MaxRetryAttempts = agent.GetProperty("MaxRetryAttempts").GetInt32();
                }
            }
        }
        catch { /* ignore */ }
    }

    private string? FindServiceAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "AdherenceAgent.Service", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private (int pending, int failed) GetBufferCounts()
    {
        try
        {
            using var connection = new System.Data.SQLite.SQLiteConnection($"Data Source={PathProvider.DatabaseFile};Pooling=true");
            connection.Open();
            int pending = 0, failed = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM event_buffer WHERE status = 'PENDING';";
                pending = Convert.ToInt32(cmd.ExecuteScalar());
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM event_buffer WHERE status = 'FAILED';";
                failed = Convert.ToInt32(cmd.ExecuteScalar());
            }
            return (pending, failed);
        }
        catch
        {
            return (-1, -1);
        }
    }

    private void UpdateStatusIcon()
    {
        var (pending, failed) = GetBufferCounts();

        var hasCreds = _credentialStore.Load().HasValue;

        if (!hasCreds)
        {
            _notifyIcon.Icon = _iconRed;
            _notifyIcon.Text = "Adherence Agent: Not registered (set credentials)";
            return;
        }

        if (pending < 0 || failed < 0)
        {
            _notifyIcon.Icon = _iconRed;
            _notifyIcon.Text = "Adherence Agent: status unknown (DB unavailable)";
            return;
        }

        if (failed > 0)
        {
            _notifyIcon.Icon = _iconRed;
            _notifyIcon.Text = $"Adherence Agent: Errors ({failed} failed, {pending} pending)";
        }
        else if (pending > 0)
        {
            _notifyIcon.Icon = _iconYellow;
            _notifyIcon.Text = $"Adherence Agent: Buffering ({pending} pending)";
        }
        else
        {
            _notifyIcon.Icon = _iconGreen;
            _notifyIcon.Text = "Adherence Agent: Online";
        }
    }

    private async Task TestApiAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            // Prefer a lightweight health endpoint; fallback to base URL HEAD
            var baseUri = new Uri(_config.ApiEndpoint.TrimEnd('/') + "/");
            var healthUri = new Uri(baseUri, "health");
            HttpResponseMessage resp;
            try
            {
                resp = await client.GetAsync(healthUri, HttpCompletionOption.ResponseHeadersRead);
            }
            catch
            {
                // fallback to HEAD on base
                var head = new HttpRequestMessage(HttpMethod.Head, baseUri);
                resp = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead);
            }

            MessageBox.Show($"API reachable ({(int)resp.StatusCode} {resp.StatusCode})\nEndpoint: {(resp.RequestMessage?.RequestUri ?? healthUri)}", "API Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"API test failed: {ex.Message}", "API Test", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task AddTestEventAsync()
    {
        try
        {
            var cfg = _config;
            var buffer = new SQLiteEventBuffer(cfg, NullLogger<SQLiteEventBuffer>.Instance);
            await buffer.InitializeAsync(CancellationToken.None);
            await buffer.AddAsync(new AdherenceEvent
            {
                EventType = EventTypes.Login,
                EventTimestampUtc = TimeZoneHelper.ToEgyptLocalTime(DateTime.UtcNow),
                Metadata = new Dictionary<string, object> { { "note", "tray_test_event" } }
            }, CancellationToken.None);

            MessageBox.Show("Test event buffered.", "Add Test Event", MessageBoxButtons.OK, MessageBoxIcon.Information);
            UpdateStatusIcon();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to add test event: {ex.Message}", "Add Test Event", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowStatus()
    {
        var (pending, failed) = GetBufferCounts();
        var svcStatus = GetServiceStatus();
        var creds = _credentialStore.Load();
        // Don't expose DB and log file paths for security/privacy
        MessageBox.Show(
            $"Service: {svcStatus}\nRegistered: {(creds.HasValue ? "Yes" : "No")}\nEndpoint: {_config.ApiEndpoint}\nPending: {pending}\nFailed: {failed}",
            "Adherence Agent Status",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void OpenLogs()
    {
        try
        {
            PathProvider.EnsureDirectories();
            Process.Start("explorer.exe", PathProvider.LogsDirectory);
        }
        catch
        {
            // Swallow errors; this is a convenience action.
        }
    }

    protected override void ExitThreadCore()
    {
        _statusTimer?.Stop();
        _breakNotificationTimer?.Stop();
        _menuRefreshTimer?.Stop();
        try { _interactiveCapture?.Dispose(); } catch { }
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _iconGreen.Dispose();
        _iconYellow.Dispose();
        _iconRed.Dispose();
        base.ExitThreadCore();
    }

    private void SetCredentials()
    {
        using var form = new Form
        {
            Text = "Set Credentials",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(360, 180),
            MinimizeBox = false,
            MaximizeBox = false
        };

        var lblWs = new Label { Text = "Workstation ID:", Left = 12, Top = 20, Width = 120 };
        var txtWs = new TextBox { Left = 140, Top = 16, Width = 200 };
        var lblKey = new Label { Text = "API Key:", Left = 12, Top = 60, Width = 120 };
        var txtKey = new TextBox { Left = 140, Top = 56, Width = 200 };
        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 140, Top = 110, Width = 80 };
        var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 230, Top = 110, Width = 80 };

        var existing = _credentialStore.Load();
        if (existing.HasValue)
        {
            txtWs.Text = existing.Value.workstationId;
            txtKey.Text = existing.Value.apiKey;
        }

        form.Controls.AddRange(new Control[] { lblWs, txtWs, lblKey, txtKey, btnOk, btnCancel });
        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        if (form.ShowDialog() == DialogResult.OK)
        {
            try
            {
                _credentialStore.Save(txtWs.Text.Trim(), txtKey.Text.Trim());
                MessageBox.Show("Credentials saved. Configuration sync will occur within 30 seconds.", "Set Credentials", MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateStatusIcon();
                // Note: ConfigSyncService checks for credentials every 30 seconds when missing,
                // so sync will happen automatically. No need to trigger manually.
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save credentials: {ex.Message}", "Set Credentials", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void ControlService(ServiceControllerStatus target)
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Refresh();
            if (target == ServiceControllerStatus.Running)
            {
                if (sc.Status != ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.StartPending)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                }
            }
            else if (target == ServiceControllerStatus.Stopped)
            {
                if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                }
            }
            MessageBox.Show($"Service {ServiceName} is now {sc.Status}.", "Service Control", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Service control failed: {ex.Message}\nEnsure the service is installed and you have rights.", "Service Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string GetServiceStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Refresh();
            return sc.Status.ToString();
        }
        catch
        {
            return "Not installed / unknown";
        }
    }

    /// <summary>
    /// Check for new break events and show notifications.
    /// </summary>
    private void CheckBreakEvents()
    {
        try
        {
            using var connection = new System.Data.SQLite.SQLiteConnection($"Data Source={PathProvider.DatabaseFile};Pooling=true");
            connection.Open();

            // Query for break events newer than last processed AND created after tray app started
            // This prevents showing notifications for old events when the tray app restarts
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, event_type, event_timestamp, metadata 
                FROM event_buffer 
                WHERE event_type IN ('BREAK_START', 'BREAK_END') 
                  AND id > @lastId
                  AND datetime(event_timestamp) > datetime(@startTime)
                ORDER BY id ASC
                LIMIT 10";

            cmd.Parameters.AddWithValue("@lastId", _lastProcessedBreakEventId);
            cmd.Parameters.AddWithValue("@startTime", _trayAppStartTime.ToString("yyyy-MM-dd HH:mm:ss"));
            
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var eventId = reader.GetInt64(0);
                var eventType = reader.GetString(1);
                var eventTimestamp = reader.GetString(2);
                var metadataJson = reader.IsDBNull(3) ? null : reader.GetString(3);

                _lastProcessedBreakEventId = Math.Max(_lastProcessedBreakEventId, eventId);

                // Parse metadata
                Dictionary<string, object>? metadata = null;
                if (!string.IsNullOrEmpty(metadataJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(metadataJson);
                        metadata = new Dictionary<string, object>();
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.String)
                                metadata[prop.Name] = prop.Value.GetString() ?? string.Empty;
                            else if (prop.Value.ValueKind == JsonValueKind.Number)
                                metadata[prop.Name] = prop.Value.GetInt32();
                            else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                                metadata[prop.Name] = prop.Value.GetBoolean();
                        }
                    }
                    catch
                    {
                        // Ignore metadata parsing errors
                    }
                }

                // Check if this is an alert or actual break detection
                bool isAlert = false;
                if (metadata != null && metadata.TryGetValue("is_alert", out var alertFlag))
                {
                    isAlert = alertFlag?.ToString()?.ToLower() == "true";
                }

                // Show notification based on event type
                if (eventType == EventTypes.BreakStart)
                {
                    if (isAlert)
                    {
                        ShowBreakWindowAlertNotification(metadata, true);
                    }
                    else
                    {
                        ShowBreakStartNotification(eventTimestamp, metadata);
                    }
                }
                else if (eventType == EventTypes.BreakEnd)
                {
                    if (isAlert)
                    {
                        ShowBreakWindowAlertNotification(metadata, false);
                    }
                    else
                    {
                        ShowBreakEndNotification(metadata);
                    }
                }
            }
        }
        catch
        {
            // Silently ignore errors (database might be locked or unavailable)
        }
    }

    /// <summary>
    /// Show alert notification when break window starts/ends (scheduled break reminder).
    /// </summary>
    private void ShowBreakWindowAlertNotification(Dictionary<string, object>? metadata, bool isStart)
    {
        string title = isStart ? "Break Window Started" : "Break Window Ended";
        string message = isStart 
            ? "Your scheduled break window has started." 
            : "Your scheduled break window has ended.";

        if (metadata != null)
        {
            if (isStart)
            {
                if (metadata.TryGetValue("scheduled_start_time", out var startTime) && startTime != null)
                {
                    message = $"Break window started at {startTime}.";
                }

                if (metadata.TryGetValue("scheduled_duration_minutes", out var duration) && duration != null)
                {
                    var durationStr = duration.ToString();
                    if (int.TryParse(durationStr, out var durationMinutes))
                    {
                        message += $" Duration: {durationMinutes} minutes.";
                    }
                }
            }
            else
            {
                if (metadata.TryGetValue("scheduled_end_time", out var endTime) && endTime != null)
                {
                    message = $"Break window ended at {endTime}.";
                }
            }
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        
        // Play sound notification
        SystemSounds.Asterisk.Play();
        
        _notifyIcon.ShowBalloonTip(5000); // Show for 5 seconds
        
        // Refresh menu to show updated break status
        RefreshMenu();
    }

    /// <summary>
    /// Show notification when break starts (actual break detection based on idle).
    /// </summary>
    private void ShowBreakStartNotification(string eventTimestamp, Dictionary<string, object>? metadata)
    {
        string title = "Break Started";
        string message = "Your break has been detected.";

        // Parse the actual break start time from event_timestamp
        if (DateTime.TryParse(eventTimestamp, out var actualStartTime))
        {
            var localTime = actualStartTime.ToLocalTime();
            message = $"Break started at {localTime:HH:mm:ss}.";
        }

        // Add scheduled break information if available
        if (metadata != null)
        {
            if (metadata.TryGetValue("scheduled_start_time", out var scheduledStartTime) && scheduledStartTime != null)
            {
                message += $" Scheduled break: {scheduledStartTime}";
            }

            if (metadata.TryGetValue("scheduled_duration_minutes", out var duration) && duration != null)
            {
                var durationStr = duration.ToString();
                if (int.TryParse(durationStr, out var durationMinutes))
                {
                    message += $" ({durationMinutes} min).";
                }
            }
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        
        // Play sound notification
        SystemSounds.Exclamation.Play();
        
        _notifyIcon.ShowBalloonTip(5000); // Show for 5 seconds
        
        // Refresh menu to show updated break status
        RefreshMenu();
    }

    /// <summary>
    /// Show notification when break ends.
    /// </summary>
    private void ShowBreakEndNotification(Dictionary<string, object>? metadata)
    {
        string title = "Break Ended";
        string message = "You've returned from break.";

        if (metadata != null)
        {
            if (metadata.TryGetValue("break_duration_minutes", out var duration) && duration != null)
            {
                var durationStr = duration.ToString();
                if (int.TryParse(durationStr, out var durationMinutes))
                {
                    message = $"Break duration: {durationMinutes} minutes.";

                    // Check if break exceeded scheduled duration
                    if (metadata.TryGetValue("exceeded_minutes", out var exceeded) && exceeded != null)
                    {
                        var exceededStr = exceeded.ToString();
                        if (int.TryParse(exceededStr, out var exceededMinutes) && exceededMinutes > 0)
                        {
                            title = "Break Ended - Warning";
                            message += $" ⚠️ Exceeded scheduled duration by {exceededMinutes} minutes.";
                            _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
                        }
                        else
                        {
                            _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                        }
                    }
                    else
                    {
                        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                    }
                }
            }
        }
        else
        {
            _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        
        // Play sound notification (Warning sound for break end with exceeded duration)
        if (_notifyIcon.BalloonTipIcon == ToolTipIcon.Warning)
        {
            SystemSounds.Hand.Play();
        }
        else
        {
            SystemSounds.Asterisk.Play();
        }
        
        _notifyIcon.ShowBalloonTip(5000); // Show for 5 seconds
        
        // Refresh menu to show updated break status
        RefreshMenu();
    }

    private static Icon CreateCircleIcon(Color color)
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 14, 14);
            using var pen = new Pen(Color.Black, 1);
            g.DrawEllipse(pen, 1, 1, 14, 14);
        }
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}
