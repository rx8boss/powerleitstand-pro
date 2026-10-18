using System.IO;
using System.Text.Json;

namespace PowerControlProWpf;

internal sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Power Control PRO", "settings.json");

    public string Language { get; set; } = "de";
    public string Theme { get; set; } = "Standard";
    public int SizeIndex { get; set; } = 2;
    public int SizePreferenceVersion { get; set; } = 2;
    public bool WarningEnabled { get; set; } = true;
    public string WarningSound { get; set; } = "system-warning.wav";
    public int WarningVolume { get; set; } = 80;
    public bool RequestAdministratorOnLaunch { get; set; }
    public int? WindowLeftPx { get; set; }
    public int? WindowTopPx { get; set; }
    public string? WindowMonitor { get; set; }

    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            settings.Theme = ThemePalette.NormalizeName(settings.Theme);
            if (settings.SizePreferenceVersion < 2)
            {
                // Version 1 stored Small, Compact, Large, Very large, Automatic.
                // Keep the same choice after Automatic moves to the top. In particular,
                // the original 900 x 650 default remains Compact for existing installs.
                settings.SizeIndex = MigrateLegacySizeIndex(settings.SizeIndex);
                settings.SizePreferenceVersion = 2;
            }
            return settings;
        }
        catch (IOException) { return new AppSettings(); }
        catch (JsonException) { return new AppSettings(); }
        catch (UnauthorizedAccessException) { return new AppSettings(); }
    }

    internal static int MigrateLegacySizeIndex(int index) => index switch
    {
        0 => 1,
        1 => 2,
        2 => 3,
        3 => 4,
        4 => 0,
        _ => 2
    };

    public void Save()
    {
        SizePreferenceVersion = 2;
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, SettingsPath, true);
    }
}
