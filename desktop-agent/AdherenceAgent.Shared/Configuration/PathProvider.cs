using System.Runtime.InteropServices;

namespace AdherenceAgent.Shared.Configuration;

public static class PathProvider
{
    private static readonly string ProgramDataRoot = ResolveProgramData();

    public static string BaseDirectory => Path.Combine(ProgramDataRoot, "AdherenceAgent");

    public static string LogsDirectory => Path.Combine(BaseDirectory, "logs");

    public static string DatabaseFile => Path.Combine(BaseDirectory, "events.db");

    public static string ConfigFile => Path.Combine(BaseDirectory, "config.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string ResolveProgramData()
    {
        try
        {
            var path = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Environment.GetEnvironmentVariable("ProgramData");
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                path = @"C:\ProgramData";
            }
            return path;
        }
        catch
        {
            return @"C:\ProgramData";
        }
    }
}

