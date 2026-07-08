using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace FolderPeek.App;

public partial class MainWindow : Window
{
    private readonly AppThemeService _themeService;
    private readonly AppSettingsService _settingsService;
    private bool _allowClose;
    private bool _isSyncingThemeSelection;
    private bool _isSyncingBehaviorSelection;
    private bool _isBehaviorControlsInitialized;

    public MainWindow(AppThemeService themeService, AppSettingsService settingsService)
    {
        _themeService = themeService;
        _settingsService = settingsService;

        InitializeComponent();
        AppIconAssets.ApplyWindowIcon(this);
        DataContext = this;
        _themeService.ThemeChanged += ThemeService_OnThemeChanged;
        SourceInitialized += (_, _) => ApplyWindowChromeStyle();
        SetHookState(false);
        SyncThemeControls();
        SyncBehaviorControls();
        _isBehaviorControlsInitialized = true;
        ResetGesture();
    }

    public ObservableCollection<string> LogItems { get; } = new();

    public ObservableCollection<string> RecentActivityItems { get; } = new();

    public event EventHandler? ShowDesktopInfoRequested;

    public event EventHandler? ToggleListeningRequested;

    public event EventHandler? ClosePanelsRequested;

    public void SetHookState(bool isEnabled)
    {
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
            ? "按住 Space，再按住鼠标左键拖动，就可以从桌面文件夹展开内容。"
            : "当前不会响应手势。你可以在这里或托盘里恢复监听。";
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

    private System.Windows.Media.Brush GetBrush(string key)
    {
        return (System.Windows.Media.Brush)FindResource(key);
    }

    private void ApplyWindowChromeStyle()
    {
        DwmWindowStyler.ApplyMainWindowStyle(this, _themeService);
        Background = GetBrush("PageBackgroundBrush");
    }
}
