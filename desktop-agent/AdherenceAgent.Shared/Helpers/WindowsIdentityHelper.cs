using System;
using System.Security.Principal;
using System.Runtime.InteropServices;

namespace AdherenceAgent.Shared.Helpers;

/// <summary>
/// Windows Identity Helper
/// 
/// Provides utilities for extracting Windows NT account information.
/// Returns sam_account_name only (e.g., z.salah.3613), no domain prefix.
/// </summary>
public static class WindowsIdentityHelper
{
    private static readonly HashSet<string> ServiceAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYSTEM",
        "LOCAL SERVICE",
        "NETWORK SERVICE"
    };

    /// <summary>
    /// Gets the current NT account (sam_account_name) from the current Windows session.
    /// 
    /// Returns format: z.salah.3613 (sam_account_name only, no domain prefix like OCTOPUS\)
    /// </summary>
    public static string GetCurrentNtAccount()
    {
        try
        {
            // Method 1: Try WindowsIdentity.GetCurrent().Name
            var identity = WindowsIdentity.GetCurrent();
            if (identity != null && !string.IsNullOrEmpty(identity.Name))
            {
                var sam = ExtractSamAccountName(identity.Name);
                if (!string.IsNullOrWhiteSpace(sam) && !ServiceAccounts.Contains(sam))
                {
                    return sam;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to next method
        }

        // If we're running as a Windows Service, WindowsIdentity/Environment often return SYSTEM.
        // Try to resolve the active interactive console user instead.
        try
        {
            var activeUser = GetActiveConsoleUserName();
            if (!string.IsNullOrWhiteSpace(activeUser) && !ServiceAccounts.Contains(activeUser))
            {
                return activeUser;
            }
        }
        catch (Exception)
        {
            // Fall through
        }

        try
        {
            // Method 2: Use Environment.UserName (may not include domain)
            var userName = Environment.UserName;
            if (!string.IsNullOrEmpty(userName))
            {
                // Environment.UserName typically returns sam_account_name without domain
                if (!ServiceAccounts.Contains(userName))
                {
                    return userName;
                }
            }
        }
        catch (Exception)
        {
            // Fall through
        }

        // Fallback: Return empty string if unable to determine
        return string.Empty;
    }

    // --- WTS (Terminal Services) helpers to get the active console user ---
    private const int WTS_CURRENT_SERVER_HANDLE = 0;
    private static readonly IntPtr WTS_CURRENT_SERVER = IntPtr.Zero;

    private enum WTS_INFO_CLASS
    {
        WTSUserName = 5,
        WTSDomainName = 7,
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        uint sessionId,
        WTS_INFO_CLASS wtsInfoClass,
        out IntPtr ppBuffer,
        out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pointer);

    private static string GetActiveConsoleUserName()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return string.Empty;

        var user = QueryWtsString(sessionId, WTS_INFO_CLASS.WTSUserName);
        if (string.IsNullOrWhiteSpace(user)) return string.Empty;

        // We intentionally return sam account name only (no domain prefix).
        return user;
    }

    private static string QueryWtsString(uint sessionId, WTS_INFO_CLASS infoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        uint bytesReturned = 0;
        try
        {
            if (!WTSQuerySessionInformation(WTS_CURRENT_SERVER, sessionId, infoClass, out buffer, out bytesReturned))
            {
                return string.Empty;
            }

            if (buffer == IntPtr.Zero || bytesReturned == 0) return string.Empty;
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    /// <summary>
    /// Extracts sam_account_name from a Windows identity string.
    /// Handles formats like:
    /// - OCTOPUS\z.salah.3613 -> z.salah.3613
    /// - z.salah.3613 -> z.salah.3613
    /// - DOMAIN\username -> username
    /// </summary>
    public static string ExtractSamAccountName(string identityName)
    {
        if (string.IsNullOrEmpty(identityName))
        {
            return string.Empty;
        }

        // Check if it contains a backslash (domain\username format)
        var backslashIndex = identityName.LastIndexOf('\\');
        if (backslashIndex >= 0 && backslashIndex < identityName.Length - 1)
        {
            // Extract username part after backslash
            return identityName.Substring(backslashIndex + 1);
        }

        // No domain prefix, return as-is
        return identityName;
    }

    /// <summary>
    /// Validates that the NT account format is correct (sam_account_name only, no domain prefix).
    /// </summary>
    public static bool IsValidNtAccount(string ntAccount)
    {
        if (string.IsNullOrWhiteSpace(ntAccount))
        {
            return false;
        }

        // Should not contain backslash (domain separator)
        if (ntAccount.Contains('\\'))
        {
            return false;
        }

        // Should not contain @ (UPN format)
        if (ntAccount.Contains('@'))
        {
            return false;
        }

        return true;
    }
}
