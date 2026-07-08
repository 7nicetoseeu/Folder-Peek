using System.IO;

namespace FolderPeek.App;

internal static class AppStoragePaths
{
    internal const string DataRootOverrideEnvironmentVariable = "FOLDER_PEEK_DATA_ROOT";

    private const string AppDataFolderName = "FolderPeek";
    private const string SettingsFileName = "FolderPeek.settings.json";
    private const string ThemeFileName = "FolderPeek.theme.json";

    public static string GetDataRootPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(DataRootOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppDataPath))
        {
            return Path.Combine(localAppDataPath, AppDataFolderName);
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    public static string GetSettingsFilePath()
    {
        return Path.Combine(GetDataRootPath(), SettingsFileName);
    }

    public static string GetThemeFilePath()
    {
        return Path.Combine(GetDataRootPath(), ThemeFileName);
    }

    public static string GetFolderIndexCacheRootPath()
    {
        return Path.Combine(GetDataRootPath(), "cache", "folder-index");
    }
}
