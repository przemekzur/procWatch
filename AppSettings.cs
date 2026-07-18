using System.Security.Cryptography;
using System.Text.Json;

namespace ProcLens;

internal sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public bool CaptureCommandLines { get; init; }
    public bool CaptureExecutablePaths { get; init; }
    public int RetentionDays { get; init; } = 14;
    public int SampleIntervalSeconds { get; init; } = 30;
    public int ScanIntervalSeconds { get; init; } = 5;
    public int DashboardPort { get; init; } = 4777;
    public string DashboardToken { get; init; } = CreateToken();

    public static string AppDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcLens");

    public static string SettingsPath => Path.Combine(AppDirectory, "settings.json");
    public static string DataDirectory => Path.Combine(AppDirectory, "data");

    public static AppSettings Load()
    {
        Directory.CreateDirectory(AppDirectory);
        AppSettings settings;
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions()) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            settings = new AppSettings();
        }

        settings = settings with
        {
            RetentionDays = Math.Clamp(settings.RetentionDays, 1, 365),
            SampleIntervalSeconds = Math.Clamp(settings.SampleIntervalSeconds, 5, 300),
            ScanIntervalSeconds = Math.Clamp(settings.ScanIntervalSeconds, 2, 60),
            DashboardPort = Math.Clamp(settings.DashboardPort, 1024, 65535),
            DashboardToken = string.IsNullOrWhiteSpace(settings.DashboardToken) ? CreateToken() : settings.DashboardToken
        };
        settings.Save();
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDirectory);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions()));
        File.Move(temporary, SettingsPath, true);
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
