using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Models;
using AdherenceAgent.Shared.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.ServiceProcess;
using AdherenceAgent.Shared.Security;

namespace AdherenceAgent.Tray;

public class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _iconGreen;
    private readonly Icon _iconYellow;
    private readonly Icon _iconRed;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private AgentConfig _config = new();
    private readonly CredentialStore _credentialStore = new();
    private const string ServiceName = "AdherenceAgentService";

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

        _statusTimer = new System.Windows.Forms.Timer { Interval = 30_000 }; // 30s
        _statusTimer.Tick += (_, _) => UpdateStatusIcon();
        _statusTimer.Start();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Status", null, (_, _) => ShowStatus());
        menu.Items.Add("Test API Connection", null, async (_, _) => await TestApiAsync());
        menu.Items.Add("Add Test Event", null, async (_, _) => await AddTestEventAsync());
        menu.Items.Add("Set Credentials...", null, (_, _) => SetCredentials());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start Service", null, (_, _) => ControlService(ServiceControllerStatus.Running));
        menu.Items.Add("Stop Service", null, (_, _) => ControlService(ServiceControllerStatus.Stopped));
        menu.Items.Add("Open Logs Folder", null, (_, _) => OpenLogs());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
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
                EventTimestampUtc = DateTime.UtcNow,
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
        MessageBox.Show(
            $"Service: {svcStatus}\nRegistered: {(creds.HasValue ? "Yes" : "No")}\nEndpoint: {_config.ApiEndpoint}\nPending: {pending}\nFailed: {failed}\nLogs: {PathProvider.LogsDirectory}\nDB: {PathProvider.DatabaseFile}",
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
        _statusTimer.Stop();
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
                MessageBox.Show("Credentials saved.", "Set Credentials", MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateStatusIcon();
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
