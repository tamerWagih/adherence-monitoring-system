using System;
using System.Collections.Generic;
using System.Linq;
using AdherenceAgent.Shared.Models;

namespace AdherenceAgent.Shared.Helpers;

/// <summary>
/// Helper class for matching domains/URLs against client website patterns.
/// Supports wildcard pattern matching for domain detection.
/// </summary>
public static class ClientWebsiteMatcher
{
    /// <summary>
    /// Find matching client website for a given domain or URL.
    /// 
    /// Returns the first matching ClientWebsite, or null if no match.
    /// </summary>
    /// <param name="domain">Domain name (e.g., "oyorooms.com")</param>
    /// <param name="url">Full URL (optional, e.g., "https://app.oyorooms.com/login")</param>
    /// <param name="clientWebsites">List of client websites to check against</param>
    /// <returns>Matching ClientWebsite or null</returns>
    public static ClientWebsite? FindMatchingClientWebsite(
        string? domain,
        string? url,
        List<ClientWebsite> clientWebsites)
    {
        if (clientWebsites == null || clientWebsites.Count == 0)
        {
            return null; // No client websites configured
        }

        if (string.IsNullOrWhiteSpace(domain) && string.IsNullOrWhiteSpace(url))
        {
            return null; // No domain or URL to match
        }

        // Try to extract domain from URL if domain is not provided
        if (string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(url))
        {
            domain = ExtractDomainFromUrl(url);
        }

        if (string.IsNullOrWhiteSpace(domain))
        {
            return null; // Still no domain after extraction
        }

        // Normalize domain (lowercase, remove www. prefix)
        domain = NormalizeDomain(domain);

        // Check each client website
        foreach (var clientWebsite in clientWebsites)
        {
            if (MatchesClientWebsite(domain, url, clientWebsite))
            {
                return clientWebsite;
            }
        }

        return null; // No match found
    }

    /// <summary>
    /// Check if a domain/URL matches a client website pattern.
    /// </summary>
    private static bool MatchesClientWebsite(
        string domain,
        string? url,
        ClientWebsite clientWebsite)
    {
        // Normalize client website domain
        var clientDomain = NormalizeDomain(clientWebsite.Domain);

        // Check exact domain match first
        if (domain.Equals(clientDomain, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if domain ends with client domain (subdomain match)
        // e.g., "app.oyorooms.com" matches "oyorooms.com"
        if (domain.EndsWith("." + clientDomain, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check URL pattern if provided
        if (!string.IsNullOrWhiteSpace(clientWebsite.UrlPattern) && !string.IsNullOrWhiteSpace(url))
        {
            if (MatchesUrlPattern(url, clientWebsite.UrlPattern))
            {
                return true;
            }
        }

        // Check domain pattern if provided (wildcard matching)
        if (!string.IsNullOrWhiteSpace(clientWebsite.UrlPattern))
        {
            // Extract domain pattern from URL pattern (e.g., "*.oyorooms.com" -> "oyorooms.com")
            var domainPattern = ExtractDomainPattern(clientWebsite.UrlPattern);
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

    /// <summary>
    /// Check if URL matches a URL pattern with wildcard support.
    /// </summary>
    private static bool MatchesUrlPattern(string url, string pattern)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        // Normalize URLs (lowercase)
        url = url.ToLowerInvariant();
        pattern = pattern.ToLowerInvariant();

        // Use ApplicationClassifier's pattern matching
        return ApplicationClassifier.MatchesPattern(url, pattern);
    }
}

