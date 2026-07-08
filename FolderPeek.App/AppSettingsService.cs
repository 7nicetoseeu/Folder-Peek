using Microsoft.Win32;
using System.IO;
using System.Text.Json;

namespace FolderPeek.App;

public sealed class AppSettingsService
{
    public const int MinPanelVisibleItemCount = 4;
    public const int MaxPanelVisibleItemCount = 20;
    public const int DefaultPanelVisibleItemCount = 10;

    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupEntryName = "Folder Peek";

    private readonly string _settingsPath;

    public AppSettingsService()
    {
        _settingsPath = AppStoragePaths.GetSettingsFilePath();
        var settings = LoadSettings();
        ShowTrayTips = settings.ShowTrayTips;
        PanelVisibleItemCount = settings.PanelVisibleItemCount;
    }

    public bool ShowTrayTips { get; private set; } = true;

    public int PanelVisibleItemCount { get; private set; } = DefaultPanelVisibleItemCount;

    public event EventHandler? PanelVisibleItemCountChanged;

    public bool IsLaunchAtStartupEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: false);
            return runKey?.GetValue(StartupEntryName) is string command &&
                   !string.IsNullOrWhiteSpace(command);
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetLaunchAtStartup(bool isEnabled, out string? errorMessage)
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(StartupRegistryPath);
            if (runKey is null)
            {
                errorMessage = "无法打开当前用户的启动项注册表。";
                return false;
            }

            if (isEnabled)
            {
                runKey.SetValue(StartupEntryName, QuotePath(GetExecutablePath()), RegistryValueKind.String);
            }
            else
            {
                runKey.DeleteValue(StartupEntryName, throwOnMissingValue: false);
            }

            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public void SetShowTrayTips(bool showTrayTips)
    {
        if (ShowTrayTips == showTrayTips)
        {
            return;
        }

        ShowTrayTips = showTrayTips;
        SaveSettings();
    }

    public void SetPanelVisibleItemCount(int itemCount)
    {
        var clampedCount = ClampPanelVisibleItemCount(itemCount);
        if (PanelVisibleItemCount == clampedCount)
        {
            return;
        }

        PanelVisibleItemCount = clampedCount;
        SaveSettings();
        PanelVisibleItemCountChanged?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return AppSettings.Default;
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettingsFile>(json);
            return new AppSettings(
                settings?.ShowTrayTips ?? true,
                ClampPanelVisibleItemCount(settings?.PanelVisibleItemCount ?? DefaultPanelVisibleItemCount));
        }
        catch
        {
            return AppSettings.Default;
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var settings = new AppSettingsFile
            {
                ShowTrayTips = ShowTrayTips,
                PanelVisibleItemCount = PanelVisibleItemCount
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // 设置保存失败不影响核心手势能力。
        }
    }

    private static string GetExecutablePath()
    {
        return Environment.ProcessPath ??
               Path.Combine(AppContext.BaseDirectory, "FolderPeek.App.exe");
    }

    private static string QuotePath(string path)
    {
        return $"\"{path}\"";
    }

    private static int ClampPanelVisibleItemCount(int itemCount)
    {
        return Math.Clamp(itemCount, MinPanelVisibleItemCount, MaxPanelVisibleItemCount);
    }

    private sealed record AppSettings(bool ShowTrayTips, int PanelVisibleItemCount)
    {
        public static AppSettings Default { get; } = new(
            ShowTrayTips: true,
            PanelVisibleItemCount: DefaultPanelVisibleItemCount);
    }

    private sealed class AppSettingsFile
    {
        public bool? ShowTrayTips { get; set; }

        public int? PanelVisibleItemCount { get; set; }
    }
}
