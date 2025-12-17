using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Service.Tray;

/// <summary>
/// Ensures the tray app is running in the active interactive user session.
///
/// Why:
/// - The Windows Service runs as LocalSystem in Session 0 (non-interactive).
/// - Users can end the tray process from Task Manager.
/// - Many "interactive" signals (foreground window, Teams/window titles, browser tabs) only work in the user's session.
///
/// This watchdog periodically checks the active console session and (re)launches the tray app there if missing.
/// </summary>
public sealed class TrayWatchdogService : BackgroundService
{
    private const string TrayProcessName = "AdherenceAgent.Tray";
    private const string TrayExeFileName = "AdherenceAgent.Tray.exe";
    private readonly ILogger<TrayWatchdogService> _logger;

    // Keep this conservative to avoid user annoyance if they intentionally kill it.
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);

    public TrayWatchdogService(ILogger<TrayWatchdogService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay so install/startup storms don't spam process creation
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EnsureTrayRunningInActiveSession();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tray watchdog tick failed");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private void EnsureTrayRunningInActiveSession()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            // No active console session.
            return;
        }

        // Is tray already running in this session?
        try
        {
            var runningInSession = Process.GetProcessesByName(TrayProcessName)
                .Any(p =>
                {
                    try { return p.SessionId == (int)sessionId; }
                    catch { return false; }
                });

            if (runningInSession)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate tray processes");
        }

        var trayPath = ResolveTrayExePath();
        if (string.IsNullOrWhiteSpace(trayPath) || !File.Exists(trayPath))
        {
            _logger.LogWarning("Tray watchdog could not find tray executable at expected path. serviceBase={ServiceBase}", AppContext.BaseDirectory);
            return;
        }

        if (!TryStartProcessInSession((int)sessionId, trayPath, Path.GetDirectoryName(trayPath)!))
        {
            _logger.LogWarning("Tray watchdog failed to start tray in session {SessionId}", sessionId);
        }
        else
        {
            _logger.LogInformation("Tray watchdog started tray in session {SessionId}", sessionId);
        }
    }

    private static string ResolveTrayExePath()
    {
        // Installed layout: <InstallRoot>\Service\ (service base) and <InstallRoot>\Tray\
        // service base is ...\AdherenceAgent\Service\
        var serviceBase = AppContext.BaseDirectory;
        var root = Path.GetFullPath(Path.Combine(serviceBase, ".."));
        var tray = Path.Combine(root, "Tray", TrayExeFileName);
        return tray;
    }

    private bool TryStartProcessInSession(int sessionId, string applicationPath, string workingDirectory)
    {
        // Get the interactive user token for the session
        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            _logger.LogDebug("WTSQueryUserToken failed for session {SessionId}, error={Error}", sessionId, Marshal.GetLastWin32Error());
            return false;
        }

        IntPtr primaryToken = IntPtr.Zero;
        IntPtr env = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        try
        {
            // Duplicate token into a primary token for CreateProcessAsUser
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
            if (!DuplicateTokenEx(
                    userToken,
                    TOKEN_ALL_ACCESS,
                    ref sa,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    TOKEN_TYPE.TokenPrimary,
                    out primaryToken))
            {
                _logger.LogDebug("DuplicateTokenEx failed, error={Error}", Marshal.GetLastWin32Error());
                return false;
            }

            // Build environment for the user
            if (!CreateEnvironmentBlock(out env, primaryToken, false))
            {
                env = IntPtr.Zero; // optional
            }

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default"
            };

            var cmdLine = Quote(applicationPath);

            var flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE;
            if (!CreateProcessAsUser(
                    primaryToken,
                    null,
                    cmdLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    flags,
                    env,
                    workingDirectory,
                    ref si,
                    out pi))
            {
                _logger.LogDebug("CreateProcessAsUser failed, error={Error}", Marshal.GetLastWin32Error());
                return false;
            }

            return true;
        }
        finally
        {
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    private static string Quote(string s) => $"\"{s}\"";

    // --- Native interop ---
    private const int TOKEN_ALL_ACCESS = 0xF01FF;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int SessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        int dwDesiredAccess,
        ref SECURITY_ATTRIBUTES lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL ImpersonationLevel,
        TOKEN_TYPE TokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    private enum TOKEN_TYPE
    {
        TokenPrimary = 1,
        TokenImpersonation = 2
    }

    private enum SECURITY_IMPERSONATION_LEVEL
    {
        SecurityAnonymous = 0,
        SecurityIdentification = 1,
        SecurityImpersonation = 2,
        SecurityDelegation = 3
    }
}

