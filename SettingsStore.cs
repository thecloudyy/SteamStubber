using System.IO;
using System.Text.Json;

namespace SteamStubber;

/// <summary>Persisted settings: last AppID, scan options, window size.</summary>
internal sealed class AppSettings
{
    public string? LastAppId { get; set; }
    public bool Recursive { get; set; } = true;
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
}

internal static class SettingsStore
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SteamStubber", "settings.json");

    internal static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    internal static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch { }
    }
}
