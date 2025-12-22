using System;

namespace AdherenceAgent.Shared.Helpers;

/// <summary>
/// Timezone Helper
/// 
/// Provides utilities for converting UTC time to Egypt local time (Africa/Cairo timezone).
/// Egypt uses EET (UTC+2) in standard time and EEST (UTC+3) during daylight saving time.
/// </summary>
public static class TimeZoneHelper
{
    private static readonly TimeZoneInfo EgyptTimeZone;
    
    static TimeZoneHelper()
    {
        // Get Egypt timezone (Africa/Cairo)
        // Falls back to UTC+2 if timezone not found (shouldn't happen on Windows)
        try
        {
            EgyptTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Egypt Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            // Fallback: Create a fixed UTC+2 offset (Egypt doesn't use DST anymore, but this handles edge cases)
            try
            {
                EgyptTimeZone = TimeZoneInfo.FindSystemTimeZoneById("E. Africa Standard Time");
            }
            catch
            {
                // Last resort: Create custom timezone with UTC+2 offset
                EgyptTimeZone = TimeZoneInfo.CreateCustomTimeZone(
                    "Egypt Standard Time",
                    TimeSpan.FromHours(2),
                    "Egypt Standard Time",
                    "Egypt Standard Time");
            }
        }
    }

    /// <summary>
    /// Converts UTC time to Egypt local time.
    /// Returns a DateTime with DateTimeKind.Unspecified that represents Egypt local time.
    /// </summary>
    /// <param name="utcTime">UTC DateTime (should be DateTimeKind.Utc)</param>
    /// <returns>Egypt local time (DateTimeKind.Unspecified)</returns>
    public static DateTime ToEgyptLocalTime(DateTime utcTime)
    {
        if (utcTime.Kind == DateTimeKind.Unspecified)
        {
            // Assume UTC if unspecified
            utcTime = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        }
        else if (utcTime.Kind == DateTimeKind.Local)
        {
            // Convert local to UTC first
            utcTime = utcTime.ToUniversalTime();
        }

        var egyptTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, EgyptTimeZone);
        // Return as Unspecified so it's treated as a "naive" local time
        return DateTime.SpecifyKind(egyptTime, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Formats a DateTime as ISO 8601 string with Egypt timezone offset (+02:00).
    /// This ensures the backend correctly interprets the time as Egypt local time.
    /// </summary>
    /// <param name="egyptLocalTime">Egypt local time (should be from ToEgyptLocalTime)</param>
    /// <returns>ISO 8601 string with +02:00 offset</returns>
    public static string ToEgyptLocalTimeIsoString(DateTime egyptLocalTime)
    {
        // Get the offset for Egypt timezone (typically +02:00)
        var offset = EgyptTimeZone.GetUtcOffset(egyptLocalTime);
        var offsetHours = offset.Hours;
        var offsetMinutes = Math.Abs(offset.Minutes);
        var offsetString = $"{(offsetHours >= 0 ? "+" : "-")}{Math.Abs(offsetHours):D2}:{offsetMinutes:D2}";
        
        // Format as ISO 8601 with timezone offset
        return egyptLocalTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + offsetString;
    }

    /// <summary>
    /// Gets the current time in Egypt local timezone.
    /// </summary>
    public static DateTime GetEgyptLocalNow()
    {
        return ToEgyptLocalTime(DateTime.UtcNow);
    }
}

