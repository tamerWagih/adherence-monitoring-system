using System;
using System.Collections.Generic;
using System.Linq;
using AdherenceAgent.Shared.Models;

namespace AdherenceAgent.Shared.Helpers;

/// <summary>
/// Helper class for classifying applications based on classification rules.
/// Supports wildcard pattern matching with priority-based rule evaluation.
/// </summary>
public static class ApplicationClassifier
{
    /// <summary>
    /// Classify an application based on name, path, and window title.
    /// 
    /// Returns:
    /// - true: WORK application
    /// - false: NON_WORK application
    /// - null: NEUTRAL or no match
    /// </summary>
    /// <param name="name">Application name (e.g., "chrome.exe")</param>
    /// <param name="path">Application path (e.g., "C:\Program Files\Chrome\chrome.exe")</param>
    /// <param name="windowTitle">Window title (e.g., "Google - Chrome")</param>
    /// <param name="rules">List of classification rules (should be sorted by priority DESC)</param>
    /// <returns>bool? - true for WORK, false for NON_WORK, null for NEUTRAL or no match</returns>
    public static bool? ClassifyApplication(
        string? name,
        string? path,
        string? windowTitle,
        List<ApplicationClassification> rules)
    {
        if (rules == null || rules.Count == 0)
        {
            return null; // No rules available
        }

        // Filter to active rules only
        var activeRules = rules
            .Where(r => r.IsActive)
            .OrderByDescending(r => r.Priority)
            .ToList();

        if (activeRules.Count == 0)
        {
            return null;
        }

        // Check each rule in priority order (highest first)
        foreach (var rule in activeRules)
        {
            if (MatchesRule(name, path, windowTitle, rule))
            {
                // Return classification based on rule type
                return rule.Classification switch
                {
                    "WORK" => true,
                    "NON_WORK" => false,
                    "NEUTRAL" => null,
                    _ => null
                };
            }
        }

        // No match found
        return null;
    }

    /// <summary>
    /// Check if an application matches a classification rule.
    /// A rule matches if ANY of its patterns (name, path, window title) match.
    /// </summary>
    private static bool MatchesRule(
        string? name,
        string? path,
        string? windowTitle,
        ApplicationClassification rule)
    {
        // Rule matches if at least one pattern is provided and matches
        bool nameMatches = false;
        bool pathMatches = false;
        bool titleMatches = false;

        // Check name pattern
        if (!string.IsNullOrWhiteSpace(rule.NamePattern))
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false; // Pattern requires name but name is null
            }
            nameMatches = MatchesPattern(name, rule.NamePattern);
        }

        // Check path pattern
        if (!string.IsNullOrWhiteSpace(rule.PathPattern))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false; // Pattern requires path but path is null
            }
            pathMatches = MatchesPattern(path, rule.PathPattern);
        }

        // Check window title pattern
        if (!string.IsNullOrWhiteSpace(rule.WindowTitlePattern))
        {
            if (string.IsNullOrWhiteSpace(windowTitle))
            {
                return false; // Pattern requires title but title is null
            }
            titleMatches = MatchesPattern(windowTitle, rule.WindowTitlePattern);
        }

        // Rule matches if at least one pattern is provided and matches
        // If no patterns provided, rule doesn't match (invalid rule)
        bool hasPatterns = !string.IsNullOrWhiteSpace(rule.NamePattern) ||
                          !string.IsNullOrWhiteSpace(rule.PathPattern) ||
                          !string.IsNullOrWhiteSpace(rule.WindowTitlePattern);

        if (!hasPatterns)
        {
            return false; // Invalid rule (no patterns)
        }

        // Match if any provided pattern matches
        return nameMatches || pathMatches || titleMatches;
    }

    /// <summary>
    /// Check if a value matches a pattern with wildcard support.
    /// Supports:
    /// - * (asterisk): matches any sequence of characters (including empty)
    /// - ? (question mark): matches any single character
    /// - Case-insensitive matching
    /// </summary>
    /// <param name="value">Value to check (e.g., "chrome.exe")</param>
    /// <param name="pattern">Pattern with wildcards (e.g., "*chrome*", "chrome.exe")</param>
    /// <returns>true if value matches pattern</returns>
    public static bool MatchesPattern(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        // Convert to lowercase for case-insensitive matching
        value = value.ToLowerInvariant();
        pattern = pattern.ToLowerInvariant();

        // Use dynamic programming approach for wildcard matching
        return MatchesPatternInternal(value, pattern, 0, 0);
    }

    /// <summary>
    /// Internal recursive helper for pattern matching with memoization.
    /// </summary>
    private static bool MatchesPatternInternal(string value, string pattern, int valueIndex, int patternIndex)
    {
        // If pattern is exhausted, value must also be exhausted
        if (patternIndex >= pattern.Length)
        {
            return valueIndex >= value.Length;
        }

        // If value is exhausted but pattern has more (non-*) characters, no match
        if (valueIndex >= value.Length)
        {
            // Only * wildcards can match empty string
            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                patternIndex++;
            }
            return patternIndex >= pattern.Length;
        }

        char patternChar = pattern[patternIndex];

        if (patternChar == '*')
        {
            // * can match:
            // 1. Zero characters (skip the *)
            // 2. One or more characters (consume one character from value)
            patternIndex++;

            // Try matching zero characters first
            if (MatchesPatternInternal(value, pattern, valueIndex, patternIndex))
            {
                return true;
            }

            // Try matching one or more characters
            while (valueIndex < value.Length)
            {
                if (MatchesPatternInternal(value, pattern, valueIndex + 1, patternIndex))
                {
                    return true;
                }
                valueIndex++;
            }

            return false;
        }
        else if (patternChar == '?')
        {
            // ? matches exactly one character
            return MatchesPatternInternal(value, pattern, valueIndex + 1, patternIndex + 1);
        }
        else
        {
            // Literal character must match exactly
            if (value[valueIndex] != patternChar)
            {
                return false;
            }
            return MatchesPatternInternal(value, pattern, valueIndex + 1, patternIndex + 1);
        }
    }
}
