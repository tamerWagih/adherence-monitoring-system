using System;
using System.Collections.Generic;
using System.Linq;
using AdherenceAgent.Shared.Models;

namespace AdherenceAgent.Shared.Helpers;

/// <summary>
/// Helper class for matching calling apps and detecting call status.
/// Supports both web-based and desktop calling applications.
/// </summary>
public static class CallingAppMatcher
{
    /// <summary>
    /// Find matching calling app for a web-based app (by domain/URL/window title).
    /// Returns the first matching CallingApp, or null if no match.
    /// </summary>
    public static CallingApp? FindMatchingWebCallingApp(
        string? domain,
        string? url,
        string? windowTitle,
        List<CallingApp> callingApps)
    {
        if (callingApps == null || callingApps.Count == 0)
        {
            return null;
        }

        // Filter to web-based apps only
        var webApps = callingApps.Where(a => a.AppType == "WEB").ToList();
        if (webApps.Count == 0)
        {
            return null;
        }

        // Try to extract domain from URL if domain is not provided
        if (string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(url))
        {
            domain = ExtractDomainFromUrl(url);
        }

        // Normalize domain if we have it
        if (!string.IsNullOrWhiteSpace(domain))
        {
            domain = NormalizeDomain(domain);
        }

        // Check each web-based calling app
        foreach (var app in webApps)
        {
            // First try domain/URL matching
            if (!string.IsNullOrWhiteSpace(domain) || !string.IsNullOrWhiteSpace(url))
            {
                if (MatchesWebCallingApp(domain, url, app))
                {
                    return app;
                }
            }

            // Fallback: Check window title pattern if domain/URL matching failed
            // This handles cases like WhatsApp Web where title is "WhatsApp - Google Chrome"
            if (!string.IsNullOrWhiteSpace(windowTitle) && !string.IsNullOrWhiteSpace(app.WindowTitlePattern))
            {
                if (ApplicationClassifier.MatchesPattern(windowTitle, app.WindowTitlePattern))
                {
                    return app;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Find matching calling app for a desktop app (by process name or window title).
    /// Returns the first matching CallingApp, or null if no match.
    /// </summary>
    public static CallingApp? FindMatchingDesktopCallingApp(
        string? processName,
        string? windowTitle,
        List<CallingApp> callingApps)
    {
        if (callingApps == null || callingApps.Count == 0)
        {
            return null;
        }

        // Filter to desktop apps only
        var desktopApps = callingApps.Where(a => a.AppType == "DESKTOP").ToList();
        if (desktopApps.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(processName) && string.IsNullOrWhiteSpace(windowTitle))
        {
            return null;
        }

        // Normalize process name (remove .exe extension, lowercase)
        if (!string.IsNullOrWhiteSpace(processName))
        {
            processName = NormalizeProcessName(processName);
        }

        // Check each desktop calling app
        foreach (var app in desktopApps)
        {
            if (MatchesDesktopCallingApp(processName, windowTitle, app))
            {
                return app;
            }
        }

        return null;
    }

    /// <summary>
    /// Detect call status from window title using call status patterns.
    /// Returns: "in_call", "ringing", "idle", "on_hold", or null if unknown.
    /// </summary>
    public static string? DetectCallStatus(string? windowTitle, CallingApp? callingApp)
    {
        if (string.IsNullOrWhiteSpace(windowTitle) || callingApp == null)
        {
            return null;
        }

        var patterns = callingApp.CallStatusPatterns;
        if (patterns == null)
        {
            return null;
        }

        // Check patterns in order: in_call, ringing, on_hold, idle
        // This ensures we prioritize active call states over idle

        if (patterns.InCall != null)
        {
            foreach (var pattern in patterns.InCall)
            {
                if (ApplicationClassifier.MatchesPattern(windowTitle, pattern))
                {
                    return "in_call";
                }
            }
        }

        if (patterns.Ringing != null)
        {
            foreach (var pattern in patterns.Ringing)
            {
                if (ApplicationClassifier.MatchesPattern(windowTitle, pattern))
                {
                    return "ringing";
                }
            }
        }

        if (patterns.OnHold != null)
        {
            foreach (var pattern in patterns.OnHold)
            {
                if (ApplicationClassifier.MatchesPattern(windowTitle, pattern))
                {
                    return "on_hold";
                }
            }
        }

        if (patterns.Idle != null)
        {
            foreach (var pattern in patterns.Idle)
            {
                if (ApplicationClassifier.MatchesPattern(windowTitle, pattern))
                {
                    return "idle";
                }
            }
        }

        return null; // Unknown status
    }

    /// <summary>
    /// Check if a domain/URL matches a web-based calling app pattern.
    /// </summary>
    private static bool MatchesWebCallingApp(
        string domain,
        string? url,
        CallingApp app)
    {
        if (app.AppType != "WEB")
        {
            return false;
        }

        // Check exact domain match
        if (!string.IsNullOrWhiteSpace(app.Domain))
        {
            var appDomain = NormalizeDomain(app.Domain);
            if (domain.Equals(appDomain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Check subdomain match
            if (domain.EndsWith("." + appDomain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check URL pattern if provided
        if (!string.IsNullOrWhiteSpace(app.UrlPattern) && !string.IsNullOrWhiteSpace(url))
        {
            if (ApplicationClassifier.MatchesPattern(url, app.UrlPattern))
            {
                return true;
            }
        }

        // Check domain pattern if provided
        if (!string.IsNullOrWhiteSpace(app.UrlPattern))
        {
            var domainPattern = ExtractDomainPattern(app.UrlPattern);
            if (!string.IsNullOrWhiteSpace(domainPattern))
            {
                if (ApplicationClassifier.MatchesPattern(domain, domainPattern))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Check if a process name/window title matches a desktop calling app pattern.
    /// </summary>
    private static bool MatchesDesktopCallingApp(
        string? processName,
        string? windowTitle,
        CallingApp app)
    {
        if (app.AppType != "DESKTOP")
        {
            return false;
        }

        // Check process name pattern
        if (!string.IsNullOrWhiteSpace(app.ProcessNamePattern) && !string.IsNullOrWhiteSpace(processName))
        {
            if (ApplicationClassifier.MatchesPattern(processName, app.ProcessNamePattern))
            {
                return true;
            }
        }

        // Check window title pattern
        if (!string.IsNullOrWhiteSpace(app.WindowTitlePattern) && !string.IsNullOrWhiteSpace(windowTitle))
        {
            if (ApplicationClassifier.MatchesPattern(windowTitle, app.WindowTitlePattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extract domain from URL.
    /// </summary>
    private static string? ExtractDomainFromUrl(string url)
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

    /// <summary>
    /// Normalize domain name (lowercase, remove www. prefix).
    /// </summary>
    private static string NormalizeDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return string.Empty;
        }

        domain = domain.ToLowerInvariant().Trim();

        // Remove www. prefix if present
        if (domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            domain = domain.Substring(4);
        }

        return domain;
    }

    /// <summary>
    /// Normalize process name (remove .exe extension, lowercase).
    /// </summary>
    private static string NormalizeProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        processName = processName.ToLowerInvariant().Trim();

        // Remove .exe extension if present
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            processName = processName.Substring(0, processName.Length - 4);
        }

        return processName;
    }

    /// <summary>
    /// Extract domain pattern from URL pattern.
    /// Example: "*.oyorooms.com" -> "*.oyorooms.com"
    /// Example: "https://*.oyorooms.com/*" -> "*.oyorooms.com"
    /// </summary>
    private static string? ExtractDomainPattern(string urlPattern)
    {
        if (string.IsNullOrWhiteSpace(urlPattern))
        {
            return null;
        }

        // Remove protocol if present
        urlPattern = urlPattern.Trim();
        if (urlPattern.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            urlPattern = urlPattern.Substring(8);
        }
        else if (urlPattern.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            urlPattern = urlPattern.Substring(7);
        }

        // Remove path if present (everything after first /)
        var slashIndex = urlPattern.IndexOf('/');
        if (slashIndex >= 0)
        {
            urlPattern = urlPattern.Substring(0, slashIndex);
        }

        // Remove port if present (everything after :)
        var colonIndex = urlPattern.IndexOf(':');
        if (colonIndex >= 0)
        {
            urlPattern = urlPattern.Substring(0, colonIndex);
        }

        return urlPattern.Trim();
    }
}

