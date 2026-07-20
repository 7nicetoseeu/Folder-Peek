using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.IO;
using Forms = System.Windows.Forms;

namespace FolderPeek.App;

public sealed class PrototypeCoordinator : IDisposable
{
    private const double DesktopTriggerThreshold = 75;
    private const double PanelTriggerThreshold = 46;
    private const double LongPressMoveTolerance = 8;
    private const int LongPressDelayMilliseconds = 500;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;

    private readonly MainWindow _window;
    private readonly AppSettingsService _settingsService;
    private readonly DesktopItemResolver _resolver;
    private readonly GlobalMouseHook _mouseHook;
    private readonly GlobalKeyboardHook _keyboardHook;
    private readonly TrayIconService _trayIcon;
    private readonly PanelManager _panelManager;
    private readonly ContextMenuRegistrationService _contextMenuRegistration = new();
    private readonly object _desktopGestureStateLock = new();
    private readonly object _pendingMoveLock = new();
    private readonly DispatcherTimer _longPressTimer;

    private AboutWindow? _aboutWindow;
    private GesturePreviewWindow? _gesturePreviewWindow;
    private bool _isEnabled = true;
    private bool _isTracking;
    private bool _isTriggered;
    private bool _isSpaceHeld;
    private bool _isLongPress;
    private bool _isLongPressReady;
    private bool _longPressMoved;
    private MouseActionType? _trackingButton;
    private int _startX;
    private int _startY;
    private GestureOrigin _gestureOrigin;
    private DesktopFolderHit? _currentDesktopHit;
    private PanelFolderHit? _currentPanelHit;
    private DesktopFolderHit? _desktopGestureCandidate;
    private PendingDesktopSuppression? _pendingDesktopSuppression;
    private bool _isDesktopGestureSuppressed;
    private GlobalMouseEventArgs? _pendingMoveEvent;
    private int _isMoveDispatchQueued;

    public PrototypeCoordinator(MainWindow window, AppSettingsService settingsService)
    {
        _window = window;
        _settingsService = settingsService;
        _resolver = new DesktopItemResolver();
        _mouseHook = new GlobalMouseHook();
        _keyboardHook = new GlobalKeyboardHook();
        _trayIcon = new TrayIconService(settingsService);
        _panelManager = new PanelManager(window, settingsService);
        _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressDelayMilliseconds) };
        _longPressTimer.Tick += OnLongPressTimerTick;
    }

    public void Start()
    {
        _window.ShowDesktopInfoRequested += OnShowDesktopInfoRequested;
        _window.ToggleListeningRequested += OnToggleListeningRequested;
        _window.ClosePanelsRequested += OnClosePanelsRequested;
        _window.PanelHeightPreviewRequested += OnPanelHeightPreviewRequested;
        _trayIcon.ShowRequested += OnTrayShowRequested;
        _trayIcon.ToggleRequested += OnTrayToggleRequested;
        _trayIcon.ClosePanelsRequested += OnClosePanelsRequested;
        _trayIcon.AboutRequested += OnAboutRequested;
        _trayIcon.ExitRequested += OnExitRequested;
        _settingsService.ExpandModeChanged += OnExpandModeChanged;

        _resolver.PrimeSnapshot();
        _mouseHook.SuppressPredicate = ShouldSuppressMouseAction;
        _mouseHook.MouseAction += OnMouseAction;
        _mouseHook.Start();
        _keyboardHook.KeyAction += OnKeyAction;
        _keyboardHook.Start();

        ApplyContextMenuRegistration();
        _window.SetHookState(true);
        _window.ResetGesture();
        _window.AddLog("Folder Peek 已启动。");
        _window.AddLog($"当前触发方式：{GetExpandModeDisplayText(_settingsService.ExpandMode)}。");
        _window.AddLog("已支持从桌面文件夹展开第一层，并继续从面板内文件夹级联展开下一层。");
        _window.AddActivity("已在后台启动，可从托盘打开状态窗口。");
        _trayIcon.ShowStartupTip();
        LogDesktopRoots();
    }

    public void OpenFolderFromShell(string fullPath)
    {
        if (!Directory.Exists(fullPath))
        {
            _window.AddActivity($"无法展开不存在的文件夹：{fullPath}");
            return;
        }

        var point = TryGetCursorScreenPoint(out var cursorPoint)
            ? cursorPoint
            : new NativePoint { X = 0, Y = 0 };
        var hit = new DesktopFolderHit(
            Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            fullPath,
            "shell-context-menu",
            new Rect(point.X, point.Y, 1, 1));
        var direction = ResolveAutomaticDirection(hit.Bounds);

        _panelManager.CloseAll(PanelCloseReason.NewPanelTriggered);
        _window.ShowFolderHit(hit);
        _window.AddLog($"右键菜单展开：方向={DirectionToText(direction)}，文件夹={fullPath}");
        _ = _panelManager.ShowPanelAsync(hit, direction);
    }

    public void Dispose()
    {
        _settingsService.ExpandModeChanged -= OnExpandModeChanged;
        _longPressTimer.Stop();
        _aboutWindow?.Close();
        _gesturePreviewWindow?.Close();
        _panelManager.Dispose();
        _keyboardHook.Dispose();
        _mouseHook.Dispose();
        _trayIcon.Dispose();
    }

    private bool ShouldSuppressMouseAction(GlobalMouseEventArgs e)
    {
        if (!_isEnabled || e.ExtraInfo == MouseInputReplayer.Marker)
        {
            return false;
        }

        if (_settingsService.ExpandMode is null)
        {
            return ShouldSuppressLegacySpaceGesture(e);
        }

        if (_settingsService.ExpandMode != FolderExpandMode.LongPressRight)
        {
            return false;
        }

        if (e.ActionType == MouseActionType.RightButtonUp)
        {
            return IsDesktopGestureSuppressed();
        }

        if (e.ActionType != MouseActionType.RightButtonDown)
        {
            return false;
        }

        if (_panelManager.IsPointInsideAnyPanel(e.X, e.Y))
        {
            return false;
        }

        var hit = _resolver.TryResolveFolderFromSnapshotPoint(e.X, e.Y);
        if (hit is null)
        {
            return false;
        }

        SetPendingDesktopSuppression(new PendingDesktopSuppression(hit, e.X, e.Y));
        SetDesktopGestureSuppressed(true);
        return true;
    }

    private bool ShouldSuppressLegacySpaceGesture(GlobalMouseEventArgs e)
    {
        if (e.ActionType != MouseActionType.LeftButtonDown || !IsSpacePressed())
        {
            return false;
        }

        if (!TryGetDesktopGestureCandidateAtPoint(e.X, e.Y, out var hit) || hit is null)
        {
            return false;
        }

        SetPendingDesktopSuppression(new PendingDesktopSuppression(hit, e.X, e.Y));
        SetDesktopGestureSuppressed(true);
        return true;
    }

    private void OnMouseAction(object? sender, GlobalMouseEventArgs e)
    {
        if (e.ActionType == MouseActionType.Move)
        {
            QueueLatestMouseMove(e);
            return;
        }

        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(
            () => HandleMouseAction(e),
            DispatcherPriority.Input);
    }

    private void OnKeyAction(object? sender, GlobalKeyEventArgs e)
    {
        if (e.VirtualKey == VkSpace)
        {
            _isSpaceHeld = e.ActionType == KeyActionType.Down;
        }

        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(
            () => HandleKeyAction(e),
            DispatcherPriority.Input);
    }

    private void HandleMouseAction(GlobalMouseEventArgs e)
    {
        if (IsTrackingButtonUp(e.ActionType))
        {
            ProcessPendingMouseMove();
        }

        if (!_isEnabled && !IsTrackingButtonUp(e.ActionType))
        {
            return;
        }

        if (e.ActionType == MouseActionType.LeftButtonDown &&
            _panelManager.HasActivePanel &&
            !_panelManager.IsPointInsideAnyPanel(e.X, e.Y))
        {
            _panelManager.CloseAll(PanelCloseReason.LostFocus);
        }

        switch (_settingsService.ExpandMode)
        {
            case null:
                HandleLegacyMouseAction(e);
                break;
            case FolderExpandMode.MiddleDrag:
                HandleMiddleDragMouseAction(e);
                break;
            case FolderExpandMode.LongPressLeft:
                HandleLongPressMouseAction(e, MouseActionType.LeftButtonDown, MouseActionType.LeftButtonUp);
                break;
            case FolderExpandMode.LongPressRight:
                HandleLongPressMouseAction(e, MouseActionType.RightButtonDown, MouseActionType.RightButtonUp);
                break;
        }
    }

    private void HandleLegacyMouseAction(GlobalMouseEventArgs e)
    {
        switch (e.ActionType)
        {
            case MouseActionType.LeftButtonDown:
                HandleLegacyLeftButtonDown(e.X, e.Y);
                break;
            case MouseActionType.Move:
                HandleMouseMove(e.X, e.Y);
                break;
            case MouseActionType.LeftButtonUp:
                FinishDragGesture();
                break;
        }
    }

    private void HandleMiddleDragMouseAction(GlobalMouseEventArgs e)
    {
        switch (e.ActionType)
        {
            case MouseActionType.MiddleButtonDown:
                StartDragGesture(e.X, e.Y, MouseActionType.MiddleButtonDown);
                break;
            case MouseActionType.Move:
                HandleMouseMove(e.X, e.Y);
                break;
            case MouseActionType.MiddleButtonUp when _trackingButton == MouseActionType.MiddleButtonDown:
                FinishDragGesture();
                break;
        }
    }

    private void HandleLongPressMouseAction(GlobalMouseEventArgs e, MouseActionType downAction, MouseActionType upAction)
    {
        switch (e.ActionType)
        {
            case var action when action == downAction:
                StartLongPress(e.X, e.Y, downAction);
                break;
            case MouseActionType.Move:
                HandleMouseMove(e.X, e.Y);
                break;
            case var action when action == upAction && _trackingButton == downAction:
                FinishLongPress(downAction == MouseActionType.RightButtonDown);
                break;
        }
    }

    private void HandleLegacyLeftButtonDown(int x, int y)
    {
        if (!IsSpacePressed())
        {
            SetPendingDesktopSuppression(null);
            ResetTrackingState();
            return;
        }

        StartDragGesture(x, y, MouseActionType.LeftButtonDown, TryConsumePendingDesktopSuppression(x, y));
    }

    private void StartDragGesture(int x, int y, MouseActionType buttonDown, DesktopFolderHit? knownDesktopHit = null)
    {
        if (_panelManager.TryHitTestFolderItem(x, y, out var panelHit) && panelHit is not null)
        {
            _startX = x;
            _startY = y;
            _trackingButton = buttonDown;
            _isTracking = true;
            _isTriggered = false;
            _isLongPress = false;
            _gestureOrigin = GestureOrigin.PanelFolder;
            _currentPanelHit = panelHit;
            _currentDesktopHit = null;
            _panelManager.PrewarmFolder(panelHit.Item.FullPath);
            _window.ShowFolderHit(new DesktopFolderHit(panelHit.Item.DisplayName, panelHit.Item.FullPath, "panel-folder-hit", panelHit.Bounds));
            _window.SetTrackingState($"已命中面板内文件夹，等待拖动超过 {PanelTriggerThreshold:F0}px");
            _window.SetDirection("-");
            _window.SetDistance(0);
            _window.AddLog($"开始级联手势：起点=({x},{y})，层级={panelHit.Level + 1}，文件夹={panelHit.Item.FullPath}");
            return;
        }

        if (_panelManager.HasActivePanel && _panelManager.IsPointInsideAnyPanel(x, y))
        {
            ResetTrackingState();
            _window.SetTrackingState("起点不是面板内文件夹");
            return;
        }

        var hit = knownDesktopHit ?? _resolver.TryResolveFolderFromSnapshotPoint(x, y);
        if (hit is null)
        {
            ResetTrackingState();
            _window.ShowFolderHit(null);
            _window.SetTrackingState("起点不是桌面文件夹");
            return;
        }

        _startX = x;
        _startY = y;
        _trackingButton = buttonDown;
        _isTracking = true;
        _isTriggered = false;
        _isLongPress = false;
        _gestureOrigin = GestureOrigin.DesktopFolder;
        _currentDesktopHit = hit;
        _currentPanelHit = null;
        SetDesktopGestureCandidate(hit);
        SetDesktopGestureSuppressed(buttonDown == MouseActionType.LeftButtonDown && _settingsService.ExpandMode is null);
        _window.ShowFolderHit(hit);
        _window.SetTrackingState($"已命中文件夹，等待拖动超过 {DesktopTriggerThreshold:F0}px");
        _window.SetDirection("-");
        _window.SetDistance(0);
        ShowDesktopGesturePreview(hit, GestureDirection.Right, 0);
        _window.AddLog($"开始桌面手势：起点=({x},{y})，文件夹={hit.FullPath}");
    }

    private void StartLongPress(int x, int y, MouseActionType buttonDown)
    {
        if (_panelManager.IsPointInsideAnyPanel(x, y))
        {
            return;
        }

        DesktopFolderHit? hit;
        if (buttonDown == MouseActionType.RightButtonDown)
        {
            hit = TryConsumePendingDesktopSuppression(x, y);
            if (hit is null)
            {
                return;
            }
        }
        else
        {
            hit = _resolver.TryResolveFolderFromSnapshotPoint(x, y);
        }

        if (hit is null)
        {
            return;
        }

        _startX = x;
        _startY = y;
        _trackingButton = buttonDown;
        _isTracking = true;
        _isTriggered = false;
        _isLongPress = true;
        _isLongPressReady = false;
        _longPressMoved = false;
        _gestureOrigin = GestureOrigin.DesktopFolder;
        _currentDesktopHit = hit;
        _currentPanelHit = null;
        _window.ShowFolderHit(hit);
        _window.SetTrackingState($"已命中文件夹，静止按住 {LongPressDelayMilliseconds}ms 后展开");
        _window.SetDirection("自动");
        _window.SetDistance(0);
        _longPressTimer.Start();
    }

    private void HandleMouseMove(int x, int y)
    {
        if (!_isTracking || _gestureOrigin == GestureOrigin.None)
        {
            if (_settingsService.ExpandMode is null && _isSpaceHeld)
            {
                UpdateDesktopGestureCandidateAtPoint(x, y);
            }

            return;
        }

        var deltaX = x - _startX;
        var deltaY = y - _startY;
        var direction = ResolveDirection(deltaX, deltaY);
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        _window.SetDistance(distance);
        _window.SetDirection(DirectionToText(direction));

        if (_isLongPress)
        {
            if (!_isLongPressReady && distance > LongPressMoveTolerance)
            {
                _longPressMoved = true;
                _longPressTimer.Stop();
                _window.SetTrackingState("已移动，取消长按判定");
            }

            return;
        }

        var threshold = ResolveTriggerThreshold(_gestureOrigin);
        var triggerDistance = ResolveTriggerDistance(deltaX, deltaY, direction, _gestureOrigin);
        _window.SetDistance(triggerDistance);

        if (_gestureOrigin == GestureOrigin.DesktopFolder && _currentDesktopHit is not null)
        {
            if (_isTriggered)
            {
                return;
            }

            var progress = Math.Clamp(triggerDistance / threshold, 0, 1);
            if (triggerDistance > threshold)
            {
                TriggerDesktopPanel(direction, triggerDistance);
                return;
            }

            ShowDesktopGesturePreview(_currentDesktopHit, direction, progress);
            _window.SetTrackingState(progress <= 0
                ? $"已命中文件夹，等待拖动超过 {DesktopTriggerThreshold:F0}px"
                : $"正在准备展开第一层（{progress:P0}）");
            return;
        }

        if (_gestureOrigin == GestureOrigin.PanelFolder && _currentPanelHit is not null)
        {
            var progress = Math.Clamp(triggerDistance / threshold, 0, 1);
            _panelManager.UpdateChildDragPreview(_currentPanelHit, direction, progress);
            _window.SetTrackingState(progress <= 0
                ? $"已命中面板内文件夹，等待拖动超过 {PanelTriggerThreshold:F0}px"
                : $"正在准备展开下一层（{progress:P0}）");
        }

        if (_isTriggered || triggerDistance <= threshold)
        {
            return;
        }

        _isTriggered = true;
        _window.SetTrackingState("已触发");
        if (_currentPanelHit is not null)
        {
            _window.ShowFolderHit(new DesktopFolderHit(_currentPanelHit.Item.DisplayName, _currentPanelHit.Item.FullPath, "panel-folder-hit", _currentPanelHit.Bounds));
            _window.AddLog($"触发级联展开：方向={DirectionToText(direction)}，距离={triggerDistance:F1}px，文件夹={_currentPanelHit.Item.FullPath}");
            _ = _panelManager.ShowChildPanelAsync(_currentPanelHit, direction);
        }
    }

    private void TriggerDesktopPanel(GestureDirection direction, double distance)
    {
        if (_currentDesktopHit is null)
        {
            return;
        }

        _isTriggered = true;
        _window.SetTrackingState("已触发");
        DismissDesktopGesturePreview(immediate: false);
        _window.ShowFolderHit(_currentDesktopHit);
        _window.AddLog($"触发成功：方向={DirectionToText(direction)}，距离={distance:F1}px，文件夹={_currentDesktopHit.FullPath}");
        _ = _panelManager.ShowPanelAsync(_currentDesktopHit, direction);
    }

    private void OnLongPressTimerTick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        if (!_isTracking || !_isLongPress || _longPressMoved || _currentDesktopHit is null)
        {
            return;
        }

        _isLongPressReady = true;
        var direction = ResolveAutomaticDirection(_currentDesktopHit.Bounds);
        _window.SetDirection($"自动{DirectionToText(direction)}");
        _window.SetTrackingState("长按已确认，正在展开");
        TriggerDesktopPanel(direction, 0);
    }

    private void FinishLongPress(bool replayShortRightClick)
    {
        _longPressTimer.Stop();
        if (!_isLongPressReady && replayShortRightClick)
        {
            MouseInputReplayer.ReplayRightClick();
        }

        ResetTrackingState();
        if (_panelManager.TryActivatePanel())
        {
            _window.AddLog("手势已结束，重新激活展开面板。");
        }
    }

    private void FinishDragGesture()
    {
        if (_isTracking && !_isTriggered)
        {
            _window.AddLog("手势结束：拖动距离还未到阈值。");
        }

        ResetTrackingState();
        _window.ResetGesture();
        if (_panelManager.TryActivatePanel())
        {
            _window.AddLog("手势已结束，重新激活展开面板。");
        }
    }

    private void HandleKeyAction(GlobalKeyEventArgs e)
    {
        if (e.VirtualKey == VkSpace)
        {
            HandleSpaceKeyAction(e.ActionType);
            return;
        }

        if (e.VirtualKey != VkEscape || e.ActionType != KeyActionType.Down)
        {
            return;
        }

        if (_isTracking)
        {
            _window.AddLog("手势已取消：按下 Esc。");
            ResetTrackingState();
            _window.ResetGesture();
            _window.ShowFolderHit(null);
        }

        if (_panelManager.HasActivePanel)
        {
            _panelManager.CloseAll(PanelCloseReason.EscapeKey);
        }
    }

    private void HandleSpaceKeyAction(KeyActionType actionType)
    {
        if (_settingsService.ExpandMode is not null)
        {
            return;
        }

        if (actionType == KeyActionType.Down)
        {
            _resolver.PrimeSnapshot();
            if (TryGetCursorScreenPoint(out var cursorPoint))
            {
                UpdateDesktopGestureCandidateAtPoint(cursorPoint.X, cursorPoint.Y);
            }

            return;
        }

        _resolver.InvalidateSnapshot();
        SetDesktopGestureCandidate(null);
        SetPendingDesktopSuppression(null);
        if (_gestureOrigin == GestureOrigin.DesktopFolder && _isTracking)
        {
            ResetTrackingState();
        }
    }

    private void OnExpandModeChanged(object? sender, EventArgs e)
    {
        ResetTrackingState();
        _resolver.PrimeSnapshot();
        ApplyContextMenuRegistration();
        _window.SetHookState(_isEnabled);
        _window.AddLog($"已切换触发方式：{GetExpandModeDisplayText(_settingsService.ExpandMode)}。");
    }

    private void ApplyContextMenuRegistration()
    {
        var shouldEnable = _settingsService.ExpandMode == FolderExpandMode.ContextMenu;
        if (_contextMenuRegistration.TrySetEnabled(shouldEnable, out var errorMessage))
        {
            _window.AddLog(shouldEnable ? "已注册文件夹右键菜单。" : "已注销文件夹右键菜单。");
            return;
        }

        _window.AddActivity($"右键菜单设置失败：{errorMessage}");
    }

    private void OnTrayShowRequested(object? sender, EventArgs e) => ShowStatusWindow();

    private void OnTrayToggleRequested(object? sender, EventArgs e) => ToggleListening();

    private void OnToggleListeningRequested(object? sender, EventArgs e) => ToggleListening();

    private void OnClosePanelsRequested(object? sender, EventArgs e)
    {
        _panelManager.CloseAll(PanelCloseReason.TrayMenu, logWhenNoPanel: true);
        _window.AddActivity("已关闭全部展开面板。");
    }

    private void OnPanelHeightPreviewRequested(object? sender, PanelHeightPreviewRequestedEventArgs e)
    {
        var previewFolderPath = Path.Combine(AppContext.BaseDirectory, "Assets", "height-preview");
        if (!Directory.Exists(previewFolderPath))
        {
            _window.AddActivity("展开框高度预览不可用：测试目录缺失。");
            _window.AddLog($"展开框高度预览失败：未找到测试目录 {previewFolderPath}");
            return;
        }

        var hit = new DesktopFolderHit("展开框高度预览", previewFolderPath, "panel-height-preview", e.AnchorBounds);
        var direction = ResolveAutomaticDirection(e.AnchorBounds);
        _window.ShowFolderHit(hit);
        _window.AddLog($"打开展开框高度预览：方向={DirectionToText(direction)}，目录={previewFolderPath}");
        _ = _panelManager.ShowPanelAsync(hit, direction);
    }

    private void OnAboutRequested(object? sender, EventArgs e)
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow();
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show();
        _aboutWindow.Activate();
    }

    private void OnShowDesktopInfoRequested(object? sender, EventArgs e)
    {
        _resolver.InvalidateSnapshot();
        _resolver.PrimeSnapshot();
        LogDesktopRoots();
        _window.AddActivity("已刷新桌面路径信息。");
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        _panelManager.CloseAll(PanelCloseReason.AppExit);
        _window.AllowClose();
        System.Windows.Application.Current.Shutdown();
    }

    private void LogDesktopRoots()
    {
        foreach (var root in _resolver.DesktopRoots)
        {
            _window.AddLog($"桌面目录：{root}");
        }
    }

    private void ShowStatusWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ToggleListening()
    {
        _isEnabled = !_isEnabled;
        _window.SetHookState(_isEnabled);
        _trayIcon.SetEnabledState(_isEnabled);
        _window.AddLog(_isEnabled ? "已恢复监听。" : "已暂停监听。");
        _window.AddActivity(_isEnabled ? "已恢复监听。" : "已暂停监听。");

        if (!_isEnabled)
        {
            ResetTrackingState();
            SetDesktopGestureCandidate(null);
            SetPendingDesktopSuppression(null);
            _resolver.InvalidateSnapshot();
            _panelManager.CloseAll(PanelCloseReason.ListeningPaused);
            _window.ResetGesture();
            _window.ShowFolderHit(null);
        }
    }

    private void ShowDesktopGesturePreview(DesktopFolderHit hit, GestureDirection direction, double progress)
    {
        EnsurePreviewWindow();
        _gesturePreviewWindow!.UpdatePreview(hit, direction, progress);
    }

    private void DismissDesktopGesturePreview(bool immediate) => _gesturePreviewWindow?.Dismiss(immediate);

    private void EnsurePreviewWindow()
    {
        if (_gesturePreviewWindow is null)
        {
            _gesturePreviewWindow = new GesturePreviewWindow();
        }
    }

    private void UpdateDesktopGestureCandidateAtPoint(int x, int y)
    {
        if (_isTracking || _panelManager.IsPointInsideAnyPanel(x, y))
        {
            return;
        }

        var hit = _resolver.TryResolveFolderFromSnapshotPoint(x, y);
        SetDesktopGestureCandidate(hit);
        if (hit is not null)
        {
            _window.ShowFolderHit(hit);
            _window.SetTrackingState($"已命中文件夹，等待拖动超过 {DesktopTriggerThreshold:F0}px");
        }
    }

    private bool TryGetDesktopGestureCandidateAtPoint(int x, int y, out DesktopFolderHit? hit)
    {
        lock (_desktopGestureStateLock)
        {
            if (_desktopGestureCandidate is not null && _desktopGestureCandidate.Bounds.Contains(x, y))
            {
                hit = _desktopGestureCandidate;
                return true;
            }
        }

        hit = null;
        return false;
    }

    private void SetDesktopGestureCandidate(DesktopFolderHit? hit)
    {
        lock (_desktopGestureStateLock)
        {
            _desktopGestureCandidate = hit;
        }
    }

    private void SetPendingDesktopSuppression(PendingDesktopSuppression? suppression)
    {
        lock (_desktopGestureStateLock)
        {
            _pendingDesktopSuppression = suppression;
        }
    }

    private DesktopFolderHit? TryConsumePendingDesktopSuppression(int x, int y)
    {
        lock (_desktopGestureStateLock)
        {
            if (_pendingDesktopSuppression is null)
            {
                return null;
            }

            var suppression = _pendingDesktopSuppression;
            _pendingDesktopSuppression = null;
            return suppression.IsForPoint(x, y) ? suppression.Hit : null;
        }
    }

    private bool IsDesktopGestureSuppressed()
    {
        lock (_desktopGestureStateLock)
        {
            return _isDesktopGestureSuppressed;
        }
    }

    private void SetDesktopGestureSuppressed(bool isSuppressed)
    {
        lock (_desktopGestureStateLock)
        {
            _isDesktopGestureSuppressed = isSuppressed;
        }
    }

    private static GestureDirection ResolveDirection(int deltaX, int deltaY)
    {
        return Math.Abs(deltaX) >= Math.Abs(deltaY)
            ? deltaX >= 0 ? GestureDirection.Right : GestureDirection.Left
            : deltaY >= 0 ? GestureDirection.Down : GestureDirection.Up;
    }

    private static GestureDirection ResolveAutomaticDirection(Rect bounds)
    {
        var center = new System.Drawing.Point((int)bounds.Left, (int)bounds.Top);
        var screen = Forms.Screen.FromPoint(center);
        if (screen is null)
        {
            return GestureDirection.Right;
        }

        var leftSpace = bounds.Left - screen.WorkingArea.Left;
        var rightSpace = screen.WorkingArea.Right - bounds.Right;
        return rightSpace >= leftSpace ? GestureDirection.Right : GestureDirection.Left;
    }

    private static string DirectionToText(GestureDirection direction)
    {
        return direction switch
        {
            GestureDirection.Right => "向右",
            GestureDirection.Left => "向左",
            GestureDirection.Down => "向下",
            GestureDirection.Up => "向上",
            _ => "-"
        };
    }

    private static string GetExpandModeDisplayText(FolderExpandMode? mode)
    {
        return mode switch
        {
            FolderExpandMode.MiddleDrag => "中键拖动",
            FolderExpandMode.ContextMenu => "融合进右键菜单",
            FolderExpandMode.LongPressLeft => "长按左键",
            FolderExpandMode.LongPressRight => "长按右键",
            _ => "Space + 左键拖动（兼容模式）"
        };
    }

    private static double ResolveTriggerThreshold(GestureOrigin origin) => origin == GestureOrigin.PanelFolder ? PanelTriggerThreshold : DesktopTriggerThreshold;

    private static double ResolveTriggerDistance(int deltaX, int deltaY, GestureDirection direction, GestureOrigin origin)
    {
        if (origin != GestureOrigin.PanelFolder)
        {
            return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        }

        return direction switch
        {
            GestureDirection.Right => Math.Max(0, deltaX),
            GestureDirection.Left => Math.Max(0, -deltaX),
            GestureDirection.Down => Math.Max(0, deltaY),
            GestureDirection.Up => Math.Max(0, -deltaY),
            _ => 0
        };
    }

    private static bool IsSpacePressed() => (GetAsyncKeyState(VkSpace) & 0x8000) != 0;

    private static bool IsTrackingButtonUp(MouseActionType action) => action is MouseActionType.LeftButtonUp or MouseActionType.MiddleButtonUp or MouseActionType.RightButtonUp;

    private void ResetTrackingState()
    {
        _longPressTimer.Stop();
        _panelManager.ClearDragPreview();
        DismissDesktopGesturePreview(immediate: true);
        SetDesktopGestureSuppressed(false);
        _isTracking = false;
        _isTriggered = false;
        _isLongPress = false;
        _isLongPressReady = false;
        _longPressMoved = false;
        _trackingButton = null;
        _gestureOrigin = GestureOrigin.None;
        _currentDesktopHit = null;
        _currentPanelHit = null;
        if (!_isSpaceHeld)
        {
            SetDesktopGestureCandidate(null);
        }

        SetPendingDesktopSuppression(null);
        lock (_pendingMoveLock)
        {
            _pendingMoveEvent = null;
        }
    }

    private void QueueLatestMouseMove(GlobalMouseEventArgs e)
    {
        lock (_pendingMoveLock)
        {
            _pendingMoveEvent = e;
        }

        if (Interlocked.Exchange(ref _isMoveDispatchQueued, 1) != 0)
        {
            return;
        }

        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(ProcessPendingMouseMove, DispatcherPriority.Input);
    }

    private void ProcessPendingMouseMove()
    {
        GlobalMouseEventArgs? moveEvent;
        lock (_pendingMoveLock)
        {
            moveEvent = _pendingMoveEvent;
            _pendingMoveEvent = null;
        }

        if (moveEvent is not null)
        {
            HandleMouseAction(moveEvent);
        }

        Interlocked.Exchange(ref _isMoveDispatchQueued, 0);
        lock (_pendingMoveLock)
        {
            if (_pendingMoveEvent is null || Interlocked.Exchange(ref _isMoveDispatchQueued, 1) != 0)
            {
                return;
            }
        }

        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(ProcessPendingMouseMove, DispatcherPriority.Input);
    }

    private static bool TryGetCursorScreenPoint(out NativePoint point) => GetCursorPos(out point);

    private enum GestureOrigin
    {
        None,
        DesktopFolder,
        PanelFolder
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private sealed record PendingDesktopSuppression(DesktopFolderHit Hit, int X, int Y)
    {
        public bool IsForPoint(int x, int y) => X == x && Y == y;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);
}
