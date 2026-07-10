namespace Yib;

internal enum SourceFolder
{
    Downloads,
    Desktop,
    Custom
}

internal static class FilePicker
{
    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload",
        ".tmp",
        ".part",
    };

    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini",
    };

    // Each slot resolves to its user-chosen override when one is set, otherwise to its
    // built-in default. The Custom slot has no built-in folder, so it falls back to the
    // user profile root only when nothing has been picked yet.
    public static string GetFolderPath(SourceFolder folder, string? downloadsPath, string? desktopPath, string? customPath) => folder switch
    {
        SourceFolder.Downloads => string.IsNullOrEmpty(downloadsPath) ? DefaultDownloads() : downloadsPath,
        SourceFolder.Desktop => string.IsNullOrEmpty(desktopPath) ? DefaultDesktop() : desktopPath,
        SourceFolder.Custom => string.IsNullOrEmpty(customPath) ? DefaultHome() : customPath,
        _ => throw new ArgumentOutOfRangeException(nameof(folder)),
    };

    public static string DefaultDownloads() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string DefaultDesktop() => Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    private static string DefaultHome() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static List<string> GetRecentFiles(string folderPath, int maxCount)
    {
        if (!Directory.Exists(folderPath))
        {
            return new List<string>();
        }

        return new DirectoryInfo(folderPath)
            .GetFiles()
            .Where(f => !ExcludedExtensions.Contains(f.Extension) && !ExcludedNames.Contains(f.Name))
            .OrderByDescending(f => f.LastWriteTime)
            .Take(maxCount)
            .Select(f => f.FullName)
            .ToList();
    }
}
