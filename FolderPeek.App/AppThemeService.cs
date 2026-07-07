using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace FolderPeek.App;

public interface IThemePaletteProvider
{
    AppThemeStyle Style { get; }

    string DisplayName { get; }

    IReadOnlyDictionary<string, string> GetPalette(AppThemeMode effectiveMode);
}

public sealed class AppThemeService : IDisposable
{
    private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string SettingsFileName = "FolderPeek.theme.json";

    private readonly System.Windows.Application _application;
    private readonly string _settingsPath;
    private readonly IThemePaletteProvider _paletteProvider = new OriginalThemePaletteProvider();

    public AppThemeService(System.Windows.Application application)
    {
        _application = application;
        _settingsPath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);

        CurrentMode = LoadSettings().ThemeMode;

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplyTheme(CurrentMode, persist: false, raiseEvent: false);
    }

    public AppThemeStyle CurrentStyle => _paletteProvider.Style;

    public AppThemeMode CurrentMode { get; private set; } = AppThemeMode.FollowSystem;

    public bool IsEffectiveDarkMode => ResolveEffectiveMode() == AppThemeMode.Dark;

    public event EventHandler? ThemeChanged;

    public void SetThemeMode(AppThemeMode mode)
    {
        ApplyTheme(mode, persist: true, raiseEvent: true);
    }

    public string GetStyleDisplayText()
    {
        return _paletteProvider.DisplayName;
    }

    public string GetModeDisplayText()
    {
        return CurrentMode switch
        {
            AppThemeMode.FollowSystem => $"跟随系统（当前{GetEffectiveModeName()}）",
            AppThemeMode.Light => "浅色模式",
            AppThemeMode.Dark => "深色模式",
            _ => "跟随系统"
        };
    }

    public MediaColor GetResourceColor(string resourceKey)
    {
        if (_application.Resources[resourceKey] is SolidColorBrush brush)
        {
            return brush.Color;
        }

        if (_application.Resources[resourceKey] is MediaColor color)
        {
            return color;
        }

        return IsEffectiveDarkMode
            ? MediaColor.FromRgb(0x08, 0x0B, 0x12)
            : MediaColor.FromRgb(0xF8, 0xF3, 0xEA);
    }

    public string GetModeActivityText()
    {
        return CurrentMode switch
        {
            AppThemeMode.FollowSystem => $"已切换为跟随系统，当前使用{GetEffectiveModeName()}。",
            AppThemeMode.Light => "已切换为浅色模式。",
            AppThemeMode.Dark => "已切换为深色模式。",
            _ => "已更新主题模式。"
        };
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (CurrentMode != AppThemeMode.FollowSystem)
        {
            return;
        }

        _ = _application.Dispatcher.BeginInvoke(() => ApplyTheme(CurrentMode, persist: false, raiseEvent: true));
    }

    private void ApplyTheme(AppThemeMode mode, bool persist, bool raiseEvent)
    {
        CurrentMode = mode;

        foreach (var entry in _paletteProvider.GetPalette(ResolveEffectiveMode()))
        {
            SetResourceColor(entry.Key, entry.Value);
        }

        if (persist)
        {
            SaveSettings();
        }

        if (raiseEvent)
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private AppThemeMode ResolveEffectiveMode()
    {
        return CurrentMode switch
        {
            AppThemeMode.Light => AppThemeMode.Light,
            AppThemeMode.Dark => AppThemeMode.Dark,
            _ => IsSystemLightTheme() ? AppThemeMode.Light : AppThemeMode.Dark
        };
    }

    private string GetEffectiveModeName()
    {
        return ResolveEffectiveMode() == AppThemeMode.Dark ? "深色" : "浅色";
    }

    private void SetResourceColor(string resourceKey, string colorHex)
    {
        var color = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);

        if (_application.Resources[resourceKey] is SolidColorBrush brush)
        {
            if (brush.IsFrozen)
            {
                brush = brush.Clone();
                brush.Color = color;
                _application.Resources[resourceKey] = brush;
                return;
            }

            brush.Color = color;
            return;
        }

        if (_application.Resources[resourceKey] is MediaColor)
        {
            _application.Resources[resourceKey] = color;
            return;
        }

        _application.Resources[resourceKey] = new SolidColorBrush(color);
    }

    private ThemeSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return ThemeSettings.Default;
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<ThemeSettingsFile>(json);
            return new ThemeSettings(ParseThemeMode(settings?.ThemeMode));
        }
        catch
        {
            return ThemeSettings.Default;
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new ThemeSettingsFile
            {
                ThemeMode = CurrentMode.ToString()
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // 主题保存失败不应影响主功能，下一次启动回到默认主题即可。
        }
    }

    private static AppThemeMode ParseThemeMode(string? value)
    {
        return Enum.TryParse<AppThemeMode>(value, ignoreCase: true, out var mode)
            ? mode
            : AppThemeMode.FollowSystem;
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var personalizeKey = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);
            var value = personalizeKey?.GetValue("AppsUseLightTheme");
            return value is int intValue ? intValue != 0 : true;
        }
        catch
        {
            return true;
        }
    }

    private sealed record ThemeSettings(AppThemeMode ThemeMode)
    {
        public static ThemeSettings Default { get; } = new(AppThemeMode.FollowSystem);
    }

    private sealed class ThemeSettingsFile
    {
        public string? ThemeMode { get; set; }
    }

    private sealed class OriginalThemePaletteProvider : IThemePaletteProvider
    {
        private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppShadowColor"] = "#5D4D3C",
            ["PageBackgroundBrush"] = "#F8F3EA",
            ["SurfaceBrush"] = "#FFFDF8",
            ["SoftSurfaceBrush"] = "#FBF6ED",
            ["SurfaceMutedBrush"] = "#F4EBDD",
            ["SurfaceElevatedBrush"] = "#FFF9F0",
            ["WindowHeaderBrush"] = "#FFF7EA",
            ["BorderBrushSoft"] = "#E4D8C8",
            ["BorderBrushStrong"] = "#D4C4AF",
            ["DividerBrush"] = "#EEE3D5",
            ["ItemBorderBrush"] = "#E8DCCE",
            ["TextPrimaryBrush"] = "#302A23",
            ["TextSecondaryBrush"] = "#65594B",
            ["TextMutedBrush"] = "#7D7061",
            ["AccentBrush"] = "#B66A2C",
            ["AccentSoftBrush"] = "#F8EAD9",
            ["AccentHoverBrush"] = "#FCF1E4",
            ["AccentPressedBrush"] = "#F2DFC9",
            ["AccentBorderHoverBrush"] = "#DFAE7F",
            ["TabHoverBrush"] = "#F3E8DA",
            ["SuccessBrush"] = "#238558",
            ["StatusRunningBackgroundBrush"] = "#E9F5EA",
            ["WarningBrush"] = "#A35D16",
            ["StatusPausedBackgroundBrush"] = "#FFF1DC",
            ["HeaderBadgeBrush"] = "#F7E8D4",
            ["HeaderBadgeTextBrush"] = "#84532B",
            ["SegmentBackgroundBrush"] = "#F0E4D5",
            ["SegmentSelectedBrush"] = "#FFFDF8",
            ["SegmentSelectedBorderBrush"] = "#DEB184",
            ["SegmentSelectedTextBrush"] = "#9A5724",
            ["FolderAccentBrush"] = "#FFD768",
            ["PanelBackgroundBrush"] = "#FFFDF8",
            ["PanelHeaderBrush"] = "#FFF5E6",
            ["PanelBorderBrush"] = "#E2D4C2",
            ["PanelDividerBrush"] = "#EEE0CF",
            ["PanelMutedBrush"] = "#746859",
            ["PanelSecondaryBrush"] = "#8B7E6D",
            ["PanelHoverBrush"] = "#FAF0E3",
            ["PanelSelectedBrush"] = "#F7E6D2",
            ["PanelExpandedBrush"] = "#F0D9BE",
            ["PanelExpandedBorderBrush"] = "#D9AA78",
            ["PanelPreviewBrush"] = "#D99152",
            ["PanelPreviewTextBrush"] = "#71451F",
            ["PanelNoticeBackgroundBrush"] = "#FFF4E1",
            ["PanelNoticeBorderBrush"] = "#ECC48B",
            ["PanelNoticeTitleBrush"] = "#895A05",
            ["PanelNoticeTextBrush"] = "#745A35",
            ["PanelScrollTrackBrush"] = "#F0E4D5",
            ["PanelScrollThumbBrush"] = "#CEBDA8",
            ["PanelScrollThumbHoverBrush"] = "#B9A58D",
            ["PanelScrollButtonHoverBrush"] = "#E9DCCB",
            ["PanelScrollGlyphBrush"] = "#81715F"
        };

        private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppShadowColor"] = "#000000",
            ["PageBackgroundBrush"] = "#080B12",
            ["SurfaceBrush"] = "#101622",
            ["SoftSurfaceBrush"] = "#151D2B",
            ["SurfaceMutedBrush"] = "#1A2433",
            ["SurfaceElevatedBrush"] = "#121A27",
            ["WindowHeaderBrush"] = "#0F1724",
            ["BorderBrushSoft"] = "#223047",
            ["BorderBrushStrong"] = "#31435F",
            ["DividerBrush"] = "#1E2A3D",
            ["ItemBorderBrush"] = "#233149",
            ["TextPrimaryBrush"] = "#F4F7FF",
            ["TextSecondaryBrush"] = "#C4CDDC",
            ["TextMutedBrush"] = "#9EAABD",
            ["AccentBrush"] = "#6FA6FF",
            ["AccentSoftBrush"] = "#172A49",
            ["AccentHoverBrush"] = "#1A2638",
            ["AccentPressedBrush"] = "#21334C",
            ["AccentBorderHoverBrush"] = "#527CBE",
            ["TabHoverBrush"] = "#182334",
            ["SuccessBrush"] = "#55D98F",
            ["StatusRunningBackgroundBrush"] = "#102D25",
            ["WarningBrush"] = "#FFC16A",
            ["StatusPausedBackgroundBrush"] = "#342816",
            ["HeaderBadgeBrush"] = "#152846",
            ["HeaderBadgeTextBrush"] = "#B9D4FF",
            ["SegmentBackgroundBrush"] = "#151E2D",
            ["SegmentSelectedBrush"] = "#1E2B3F",
            ["SegmentSelectedBorderBrush"] = "#527CBE",
            ["SegmentSelectedTextBrush"] = "#DCEAFF",
            ["FolderAccentBrush"] = "#6FA6FF",
            ["PanelBackgroundBrush"] = "#0E1521",
            ["PanelHeaderBrush"] = "#121C2B",
            ["PanelBorderBrush"] = "#25334C",
            ["PanelDividerBrush"] = "#1E2A3D",
            ["PanelMutedBrush"] = "#A5B1C4",
            ["PanelSecondaryBrush"] = "#8795AA",
            ["PanelHoverBrush"] = "#172337",
            ["PanelSelectedBrush"] = "#1B2D48",
            ["PanelExpandedBrush"] = "#233A5D",
            ["PanelExpandedBorderBrush"] = "#527CBE",
            ["PanelPreviewBrush"] = "#4F86D7",
            ["PanelPreviewTextBrush"] = "#D5E6FF",
            ["PanelNoticeBackgroundBrush"] = "#312817",
            ["PanelNoticeBorderBrush"] = "#665128",
            ["PanelNoticeTitleBrush"] = "#FFD48C",
            ["PanelNoticeTextBrush"] = "#F0D8AE",
            ["PanelScrollTrackBrush"] = "#151E2D",
            ["PanelScrollThumbBrush"] = "#40516C",
            ["PanelScrollThumbHoverBrush"] = "#536783",
            ["PanelScrollButtonHoverBrush"] = "#19263A",
            ["PanelScrollGlyphBrush"] = "#A7B4C8"
        };

        public AppThemeStyle Style => AppThemeStyle.Original;

        public string DisplayName => "原主题";

        public IReadOnlyDictionary<string, string> GetPalette(AppThemeMode effectiveMode)
        {
            return effectiveMode == AppThemeMode.Dark ? DarkPalette : LightPalette;
        }
    }
}
