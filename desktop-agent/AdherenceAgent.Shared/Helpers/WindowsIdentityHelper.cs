using System;
using System.Security.Principal;

namespace AdherenceAgent.Shared.Helpers;

/// <summary>
/// Windows Identity Helper
/// 
/// Provides utilities for extracting Windows NT account information.
/// Returns sam_account_name only (e.g., z.salah.3613), no domain prefix.
/// </summary>
public static class WindowsIdentityHelper
{
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
                return ExtractSamAccountName(identity.Name);
            }
        }
        catch (Exception)
        {
            // Fall through to next method
        }

        try
        {
            // Method 2: Use Environment.UserName (may not include domain)
            var userName = Environment.UserName;
            if (!string.IsNullOrEmpty(userName))
            {
                // Environment.UserName typically returns sam_account_name without domain
                return userName;
            }
        }
        catch (Exception)
        {
            // Fall through
        }

        // Fallback: Return empty string if unable to determine
        return string.Empty;
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
