using System.Text.Json;

namespace Yib;

internal sealed class Settings
{
    public ShakeSensitivity Sensitivity { get; set; } = ShakeSensitivity.Medium;
    public SourceFolder SourceFolder { get; set; } = SourceFolder.Downloads;

    // Per-slot folder overrides. null = use the slot's built-in default (real Downloads /
    // Desktop folder); a value re-points that slot at any folder the user picks. The Custom
    // slot has no built-in default, so an empty CustomFolderPath means "not yet chosen".
    public string? DownloadsFolderPath { get; set; }
    public string? DesktopFolderPath { get; set; }
    public string? CustomFolderPath { get; set; }

    /// <summary>The override path stored for a slot, or null when it uses its default.</summary>
    public string? PathFor(SourceFolder slot) => slot switch
    {
        SourceFolder.Downloads => DownloadsFolderPath,
        SourceFolder.Desktop => DesktopFolderPath,
        SourceFolder.Custom => CustomFolderPath,
        _ => null,
    };

    public void SetPathFor(SourceFolder slot, string? path)
    {
        switch (slot)
        {
            case SourceFolder.Downloads: DownloadsFolderPath = path; break;
            case SourceFolder.Desktop: DesktopFolderPath = path; break;
            case SourceFolder.Custom: CustomFolderPath = path; break;
        }
    }
    public bool SoundEnabled { get; set; } = true;
    public int SoundVolume { get; set; } = 80;
    public int SoundOutputDeviceIndex { get; set; } = AudioPlayer.DefaultDevice;
    public bool ShowDlPill { get; set; } = true;
    public bool ShowDeskPill { get; set; } = true;
    public bool ShowCustomPill { get; set; } = true;
    public int DialMultiplier { get; set; } = 1;
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yib", "settings.json");

    public static Settings Load()
    {
        try
        {
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    public void Save()
    {
        string path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}
