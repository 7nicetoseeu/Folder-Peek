using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FolderPeek.App;

public partial class MainWindow : Window
{
    private readonly AppThemeService _themeService;
    private readonly AppSettingsService _settingsService;
    private bool _allowClose;
    private bool _isSyncingThemeSelection;
    private bool _isSyncingBehaviorSelection;
    private bool _isSyncingGestureSelection;
    private bool _isBehaviorControlsInitialized;
    private bool _isHookEnabled;

    public MainWindow(AppThemeService themeService, AppSettingsService settingsService)
    {
        _themeService = themeService;
        _settingsService = settingsService;

        InitializeComponent();
        AppIconAssets.ApplyWindowIcon(this);
        LoadPreviewImage(GuidePreviewImage, "gesture-guide.png");
        LoadThemePreviewImages();
        DataContext = this;
        OtherVersionText.Text = $"版本 {GetType().Assembly.GetName().Version?.ToString(4) ?? "未知"}";
        OtherDataPathText.Text = AppStoragePaths.GetDataRootPath();
        _themeService.ThemeChanged += ThemeService_OnThemeChanged;
        SourceInitialized += (_, _) => ApplyWindowChromeStyle();
        SetHookState(false);
        SyncThemeControls();
        SyncBehaviorControls();
        SyncGestureControls();
        _isBehaviorControlsInitialized = true;
        ResetGesture();
    }

    public ObservableCollection<string> LogItems { get; } = new();

    public ObservableCollection<string> RecentActivityItems { get; } = new();

    public event EventHandler? ShowDesktopInfoRequested;

    public event EventHandler? ToggleListeningRequested;

    public event EventHandler? ClosePanelsRequested;

    public event EventHandler<PanelHeightPreviewRequestedEventArgs>? PanelHeightPreviewRequested;

    public void SetHookState(bool isEnabled)
    {
        _isHookEnabled = isEnabled;
        HookStateText.Text = isEnabled ? "监听状态：运行中" : "监听状态：已暂停";
        HomeStatusBadgeText.Text = isEnabled ? "运行中" : "已暂停";
        HomeStatusBadgeBorder.Background = isEnabled
            ? GetBrush("StatusRunningBackgroundBrush")
            : GetBrush("StatusPausedBackgroundBrush");
        HomeStatusBadgeText.Foreground = isEnabled
            ? GetBrush("SuccessBrush")
            : GetBrush("WarningBrush");
        HomeStatusTitleText.Text = isEnabled
            ? "Folder Peek 正在后台运行"
            : "Folder Peek 已暂停监听";
        HomeStatusDetailText.Text = isEnabled
            ? GetExpandModeDescription(_settingsService.ExpandMode)
            : "当前不会响应手势。你可以在这里或托盘里恢复监听。";
        ToggleListeningButtonIcon.Text = isEnabled ? "\uE769" : "\uE768";
        ToggleListeningButtonLabel.Text = isEnabled ? "暂停监听" : "恢复监听";
    }

    public void SetTrackingState(string text)
    {
        TrackingStateText.Text = $"手势状态：{text}";
    }

    public void SetDirection(string text)
    {
        DirectionText.Text = $"方向：{text}";
    }

    public void SetDistance(double distance)
    {
        DistanceText.Text = $"拖动距离：{distance:F1} px";
    }

    public void ShowFolderHit(DesktopFolderHit? hit)
    {
        if (hit is null)
        {
            FolderNameText.Text = "名称：-";
            FolderPathText.Text = "路径：-";
            FolderSourceText.Text = "来源：-";
            return;
        }

        FolderNameText.Text = $"名称：{hit.DisplayName}";
        FolderPathText.Text = $"路径：{hit.FullPath}";
        FolderSourceText.Text = $"来源：{hit.Source}";
    }

    public void AddLog(string message)
    {
        void Append()
        {
            LogItems.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            while (LogItems.Count > 200)
            {
                LogItems.RemoveAt(LogItems.Count - 1);
            }
        }

        if (Dispatcher.CheckAccess())
        {
            Append();
            return;
        }

        _ = Dispatcher.BeginInvoke(Append, DispatcherPriority.Background);
    }

    public void AddActivity(string message)
    {
        void Append()
        {
            RecentActivityItems.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            while (RecentActivityItems.Count > 8)
            {
                RecentActivityItems.RemoveAt(RecentActivityItems.Count - 1);
            }
        }

        if (Dispatcher.CheckAccess())
        {
            Append();
            return;
        }

        _ = Dispatcher.BeginInvoke(Append, DispatcherPriority.Background);
    }

    public void ResetGesture()
    {
        SetTrackingState("空闲");
        SetDirection("-");
        SetDistance(0);
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    private void ThemeModeButton_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_isSyncingThemeSelection)
        {
            return;
        }

        var requestedMode = sender switch
        {
            System.Windows.Controls.RadioButton radioButton when ReferenceEquals(radioButton, ThemeFollowSystemButton) => AppThemeMode.FollowSystem,
            System.Windows.Controls.RadioButton radioButton when ReferenceEquals(radioButton, ThemeLightButton) => AppThemeMode.Light,
            System.Windows.Controls.RadioButton radioButton when ReferenceEquals(radioButton, ThemeDarkButton) => AppThemeMode.Dark,
            _ => _themeService.CurrentMode
        };

        if (requestedMode == _themeService.CurrentMode)
        {
            SyncThemeControls();
            return;
        }

        _themeService.SetThemeMode(requestedMode);
        AddActivity(_themeService.GetModeActivityText());
    }

    private void StartupToggle_OnCheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingBehaviorSelection)
        {
            return;
        }

        var shouldEnable = StartupToggle.IsChecked == true;
        if (_settingsService.TrySetLaunchAtStartup(shouldEnable, out var errorMessage))
        {
            SyncBehaviorControls();
            AddActivity(shouldEnable ? "已开启开机自启。" : "已关闭开机自启。");
            return;
        }

        AddActivity($"开机自启设置失败：{errorMessage}");
        SyncBehaviorControls();
    }

    private void TrayTipsToggle_OnCheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingBehaviorSelection)
        {
            return;
        }

        var shouldShow = TrayTipsToggle.IsChecked == true;
        _settingsService.SetShowTrayTips(shouldShow);
        SyncBehaviorControls();
        AddActivity(shouldShow ? "已开启托盘提示。" : "已关闭托盘提示。");
    }

    private void PanelVisibleItemCountSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isBehaviorControlsInitialized ||
            _isSyncingBehaviorSelection ||
            PanelVisibleItemCountSlider is null)
        {
            return;
        }

        var itemCount = (int)Math.Round(PanelVisibleItemCountSlider.Value);
        _settingsService.SetPanelVisibleItemCount(itemCount);
        SyncBehaviorControls();
        AddActivity($"展开框高度已设置为显示 {itemCount} 个项目。");
    }

    private void PanelHeightPreviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (PanelHeightPreviewButton is null)
        {
            return;
        }

        var topLeft = PanelHeightPreviewButton.PointToScreen(new System.Windows.Point(0, 0));
        var bottomRight = PanelHeightPreviewButton.PointToScreen(new System.Windows.Point(
            PanelHeightPreviewButton.ActualWidth,
            PanelHeightPreviewButton.ActualHeight));
        PanelHeightPreviewRequested?.Invoke(this, new PanelHeightPreviewRequestedEventArgs(new Rect(topLeft, bottomRight)));
    }

    private void GesturePreviewButton_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_isSyncingGestureSelection || GesturePreviewSummaryText is null)
        {
            return;
        }

        var expandMode = sender switch
        {
            System.Windows.Controls.RadioButton radioButton when ReferenceEquals(radioButton, GestureMiddleDragButton) => FolderExpandMode.MiddleDrag,
            System.Windows.Controls.RadioButton radioButton when ReferenceEquals(radioButton, GestureContextMenuButton) => FolderExpandMode.ContextMenu,
            System.Windows.Controls.RadioButton radioButton when ReferenceEquals(radioButton, GestureLongPressLeftButton) => FolderExpandMode.LongPressLeft,
            System.Windows.Controls.RadioButton radioButton when ReferenceEquals(radioButton, GestureLongPressRightButton) => FolderExpandMode.LongPressRight,
            _ => FolderExpandMode.MiddleDrag
        };

        _settingsService.SetExpandMode(expandMode);
        SyncGestureControls();
        SetHookState(_isHookEnabled);
        AddActivity($"已切换展开方式：{GetExpandModeDisplayText(expandMode)}。");
    }

    private void DefaultGestureImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settingsService.ExpandMode is null)
        {
            return;
        }

        _settingsService.UseDefaultExpandMode();
        SyncGestureControls();
        SetHookState(_isHookEnabled);
        AddActivity("已切换为默认手势：Space + 左键拖动。");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            AddLog("主窗口已隐藏，可从托盘重新打开。");
            return;
        }

        base.OnClosing(e);
    }

    private void ClearLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        LogItems.Clear();
        AddLog("日志已清空。");
        AddActivity("已清空开发日志。");
    }

    private void ShowDesktopInfoButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowDesktopInfoRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleListeningButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleListeningRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ClosePanelsButton_OnClick(object sender, RoutedEventArgs e)
    {
        ClosePanelsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenDataFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataPath = AppStoragePaths.GetDataRootPath();
            Directory.CreateDirectory(dataPath);
            Process.Start(new ProcessStartInfo(dataPath) { UseShellExecute = true });
            AddActivity("已打开运行数据目录。");
        }
        catch (Exception ex)
        {
            AddActivity($"无法打开运行数据目录：{ex.Message}");
        }
    }

    private void OpenProjectPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/7nicetoseeu/Folder-Peek")
            {
                UseShellExecute = true
            });
            AddActivity("已打开 GitHub 项目页面。");
        }
        catch (Exception ex)
        {
            AddActivity($"无法打开项目页面：{ex.Message}");
        }
    }

    private void ThemeService_OnThemeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                SyncThemeControls();
                ApplyWindowChromeStyle();
            }, DispatcherPriority.Background);
            return;
        }

        SyncThemeControls();
        ApplyWindowChromeStyle();
    }

    private void SyncThemeControls()
    {
        if (ThemeModeSummaryText is null ||
            ThemeFollowSystemButton is null ||
            ThemeLightButton is null ||
            ThemeDarkButton is null)
        {
            return;
        }

        _isSyncingThemeSelection = true;
        ThemeFollowSystemButton.IsChecked = _themeService.CurrentMode == AppThemeMode.FollowSystem;
        ThemeLightButton.IsChecked = _themeService.CurrentMode == AppThemeMode.Light;
        ThemeDarkButton.IsChecked = _themeService.CurrentMode == AppThemeMode.Dark;
        ThemeModeSummaryText.Text = $"当前模式：{_themeService.GetModeDisplayText()}";
        _isSyncingThemeSelection = false;
    }

    private void SyncBehaviorControls()
    {
        if (StartupToggle is null ||
            TrayTipsToggle is null ||
            StartupSummaryText is null ||
            TrayTipsSummaryText is null ||
            PanelVisibleItemCountSlider is null ||
            PanelVisibleItemCountValueText is null ||
            PanelVisibleItemCountSummaryText is null)
        {
            return;
        }

        var startupEnabled = _settingsService.IsLaunchAtStartupEnabled();
        _isSyncingBehaviorSelection = true;
        StartupToggle.IsChecked = startupEnabled;
        TrayTipsToggle.IsChecked = _settingsService.ShowTrayTips;
        PanelVisibleItemCountSlider.Minimum = AppSettingsService.MinPanelVisibleItemCount;
        PanelVisibleItemCountSlider.Maximum = AppSettingsService.MaxPanelVisibleItemCount;
        PanelVisibleItemCountSlider.Value = _settingsService.PanelVisibleItemCount;
        _isSyncingBehaviorSelection = false;

        StartupSummaryText.Text = startupEnabled
            ? "已写入当前用户启动项。"
            : "登录 Windows 后不会自动启动。";
        TrayTipsSummaryText.Text = _settingsService.ShowTrayTips
            ? "启动、暂停和恢复时显示托盘气泡。"
            : "保留托盘图标，但不弹出气泡提示。";
        PanelVisibleItemCountValueText.Text = $"{_settingsService.PanelVisibleItemCount} 个";
        PanelVisibleItemCountSummaryText.Text = $"超过 {_settingsService.PanelVisibleItemCount} 个项目后，展开框内部滚动。";
    }

    private void SyncGestureControls()
    {
        if (GesturePreviewSummaryText is null ||
            GestureMiddleDragButton is null ||
            GestureContextMenuButton is null ||
            GestureLongPressLeftButton is null ||
            GestureLongPressRightButton is null)
        {
            return;
        }

        _isSyncingGestureSelection = true;
        GestureMiddleDragButton.IsChecked = _settingsService.ExpandMode == FolderExpandMode.MiddleDrag;
        GestureContextMenuButton.IsChecked = _settingsService.ExpandMode == FolderExpandMode.ContextMenu;
        GestureLongPressLeftButton.IsChecked = _settingsService.ExpandMode == FolderExpandMode.LongPressLeft;
        GestureLongPressRightButton.IsChecked = _settingsService.ExpandMode == FolderExpandMode.LongPressRight;
        _isSyncingGestureSelection = false;

        GesturePreviewSummaryText.Text = GetExpandModeUsageText(_settingsService.ExpandMode);
        SyncDefaultGestureImage();
    }

    private void SyncDefaultGestureImage()
    {
        if (GuidePreviewImage is null ||
            GuidePreviewDimOverlay is null ||
            DefaultGestureImageButton is null)
        {
            return;
        }

        var isDefaultGesture = _settingsService.ExpandMode is null;
        GuidePreviewImage.Opacity = isDefaultGesture ? 1 : 0.55;
        GuidePreviewImage.Effect = isDefaultGesture
            ? null
            : new System.Windows.Media.Effects.BlurEffect { Radius = 5 };
        GuidePreviewDimOverlay.Visibility = isDefaultGesture ? Visibility.Collapsed : Visibility.Visible;
        DefaultGestureImageButton.ToolTip = isDefaultGesture ? "当前默认手势" : "切换为默认手势";
    }

    private static string GetExpandModeDisplayText(FolderExpandMode? expandMode)
    {
        return expandMode switch
        {
            FolderExpandMode.MiddleDrag => "中键拖动",
            FolderExpandMode.ContextMenu => "融合进右键菜单",
            FolderExpandMode.LongPressLeft => "长按左键",
            FolderExpandMode.LongPressRight => "长按右键",
            _ => "Space + 左键拖动（兼容模式）"
        };
    }

    private static string GetExpandModeDescription(FolderExpandMode? expandMode)
    {
        return expandMode switch
        {
            FolderExpandMode.MiddleDrag => "按住鼠标中键向任意方向拖动，即可从桌面文件夹展开内容。",
            FolderExpandMode.ContextMenu => "在文件夹右键菜单的“显示更多选项”中选择“使用 Folder Peek 展开”。",
            FolderExpandMode.LongPressLeft => "在桌面文件夹上静止长按左键，达到 500ms 后即可展开内容。",
            FolderExpandMode.LongPressRight => "在桌面文件夹上静止长按右键，达到 500ms 后即可展开内容。",
            _ => "按住 Space，再按住鼠标左键拖动，就可以从桌面文件夹展开内容。"
        };
    }

    private static string GetExpandModeUsageText(FolderExpandMode? expandMode)
    {
        return expandMode switch
        {
            FolderExpandMode.MiddleDrag => "中键拖动：级联面板支持中键拖动和左键单击展开。",
            FolderExpandMode.ContextMenu => "融合进右键菜单：从菜单打开首层，级联面板支持左键单击展开。",
            FolderExpandMode.LongPressLeft => "长按左键：级联面板仅接受短左键单击；按住达到 500ms 不展开。",
            FolderExpandMode.LongPressRight => "长按右键：级联面板仅接受短左键单击；按住达到 500ms 不展开。",
            _ => "Space + 左键拖动（兼容模式）：级联面板支持左键单击展开。"
        };
    }

    private System.Windows.Media.Brush GetBrush(string key)
    {
        return (System.Windows.Media.Brush)FindResource(key);
    }

    private void ApplyWindowChromeStyle()
    {
        DwmWindowStyler.ApplyMainWindowStyle(this, _themeService);
        Background = GetBrush("PageBackgroundBrush");
    }

    private void LoadThemePreviewImages()
    {
        LoadPreviewImage(DarkThemePreviewImage, "effect.png");
        LoadPreviewImage(LightThemePreviewImage, "effect-light.png");
    }

    private static void LoadPreviewImage(System.Windows.Controls.Image image, string fileName)
    {
        var imagePath = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        if (!File.Exists(imagePath))
        {
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            image.Source = bitmap;
        }
        catch
        {
            // Keep the lightweight placeholder when a preview cannot be loaded.
        }
    }
}
