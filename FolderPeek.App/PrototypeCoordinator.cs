using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FolderPeek.App;

public sealed class PrototypeCoordinator : IDisposable
{
    private const double DesktopTriggerThreshold = 75;
    private const double PanelTriggerThreshold = 46;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;

    private readonly MainWindow _window;
    private readonly DesktopItemResolver _resolver;
    private readonly GlobalMouseHook _mouseHook;
    private readonly GlobalKeyboardHook _keyboardHook;
    private readonly TrayIconService _trayIcon;
    private readonly PanelManager _panelManager;
    private readonly object _desktopGestureStateLock = new();
    private AboutWindow? _aboutWindow;
    private GesturePreviewWindow? _gesturePreviewWindow;

    private bool _isEnabled = true;
    private bool _isTracking;
    private bool _isTriggered;
    private bool _isSpaceHeld;
    private int _startX;
    private int _startY;
    private GestureOrigin _gestureOrigin;
    private DesktopFolderHit? _currentDesktopHit;
    private PanelFolderHit? _currentPanelHit;
    private DesktopFolderHit? _desktopGestureCandidate;
    private PendingDesktopSuppression? _pendingDesktopSuppression;
    private bool _isDesktopGestureSuppressed;
    private readonly object _pendingMoveLock = new();
    private GlobalMouseEventArgs? _pendingMoveEvent;
    private int _isMoveDispatchQueued;

    public PrototypeCoordinator(MainWindow window, AppSettingsService settingsService)
    {
        _window = window;
        _resolver = new DesktopItemResolver();
        _mouseHook = new GlobalMouseHook();
        _keyboardHook = new GlobalKeyboardHook();
        _trayIcon = new TrayIconService(settingsService);
        _panelManager = new PanelManager(window, settingsService);
    }

    public void Start()
    {
        _window.ShowDesktopInfoRequested += OnShowDesktopInfoRequested;
        _window.ToggleListeningRequested += OnToggleListeningRequested;
        _window.ClosePanelsRequested += OnClosePanelsRequested;
        _trayIcon.ShowRequested += OnTrayShowRequested;
        _trayIcon.ToggleRequested += OnTrayToggleRequested;
        _trayIcon.ClosePanelsRequested += OnClosePanelsRequested;
        _trayIcon.AboutRequested += OnAboutRequested;
        _trayIcon.ExitRequested += OnExitRequested;

        _mouseHook.SuppressPredicate = ShouldSuppressMouseAction;
        _mouseHook.MouseAction += OnMouseAction;
        _mouseHook.Start();
        _keyboardHook.KeyAction += OnKeyAction;
        _keyboardHook.Start();

        _window.SetHookState(true);
        _window.ResetGesture();
        _window.AddLog("Folder Peek 已启动。");
        _window.AddLog("应用现在默认常驻托盘，需要时可从托盘打开状态窗口。");
        _window.AddLog("当前触发方式：按住 Space，再按住鼠标左键拖动。");
        _window.AddLog("现在已支持从桌面文件夹展开第一层，并继续从面板内文件夹级联展开下一层。");
        _window.AddActivity("已在后台启动，可从托盘打开状态窗口。");
        _trayIcon.ShowStartupTip();
        LogDesktopRoots();
    }

    public void Dispose()
    {
        _aboutWindow?.Close();
        _gesturePreviewWindow?.Close();
        _panelManager.Dispose();
        _keyboardHook.Dispose();
        _mouseHook.Dispose();
        _trayIcon.Dispose();
    }

    private bool ShouldSuppressMouseAction(GlobalMouseEventArgs e)
    {
        if (!_isEnabled)
        {
            return false;
        }

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
        if (!_isEnabled && e.ActionType != MouseActionType.LeftButtonUp)
        {
            return;
        }

        if (e.ActionType == MouseActionType.LeftButtonDown &&
            _panelManager.HasActivePanel &&
            !_panelManager.IsPointInsideAnyPanel(e.X, e.Y))
        {
            _panelManager.CloseAll(PanelCloseReason.LostFocus);
        }

        switch (e.ActionType)
        {
            case MouseActionType.LeftButtonDown:
                HandleLeftButtonDown(e.X, e.Y);
                break;
            case MouseActionType.Move:
                HandleMouseMove(e.X, e.Y);
                break;
            case MouseActionType.LeftButtonUp:
                HandleLeftButtonUp();
                break;
        }
    }

    private void HandleLeftButtonDown(int x, int y)
    {
        if (!IsSpacePressed())
        {
            SetPendingDesktopSuppression(null);
            ResetTrackingState();
            return;
        }

        if (_panelManager.TryHitTestFolderItem(x, y, out var panelHit) && panelHit is not null)
        {
            _startX = x;
            _startY = y;
            _isTracking = true;
            _isTriggered = false;
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
            _window.AddLog($"忽略这次手势：按下左键时命中了面板，但没有命中文件夹项目。坐标=({x},{y})");
            return;
        }

        var hit = TryConsumePendingDesktopSuppression(x, y) ?? _resolver.TryResolveFolderFromSnapshotPoint(x, y);
        if (hit is null)
        {
            SetPendingDesktopSuppression(null);
            ResetTrackingState();
            _window.ShowFolderHit(null);
            _window.SetTrackingState("起点不是桌面文件夹");
            _window.AddLog($"忽略这次手势：按下左键时没有命中桌面文件夹。{_resolver.DescribeScreenPoint(x, y)}");
            return;
        }

        _startX = x;
        _startY = y;
        _isTracking = true;
        _isTriggered = false;
        _gestureOrigin = GestureOrigin.DesktopFolder;
        _currentDesktopHit = hit;
        _currentPanelHit = null;
        SetDesktopGestureCandidate(hit);
        SetDesktopGestureSuppressed(true);
        SetPendingDesktopSuppression(null);

        _window.ShowFolderHit(hit);
        _window.SetTrackingState($"已命中文件夹，等待拖动超过 {DesktopTriggerThreshold:F0}px");
        _window.SetDirection("-");
        _window.SetDistance(0);
        ShowDesktopGesturePreview(hit, GestureDirection.Right, 0);
        _window.AddLog($"开始桌面手势：起点=({x},{y})，文件夹={hit.FullPath}");
    }

    private void HandleMouseMove(int x, int y)
    {
        if (!_isTracking || _gestureOrigin == GestureOrigin.None)
        {
            if (_isSpaceHeld && !_isTracking)
            {
                UpdateDesktopGestureCandidateAtPoint(x, y);
            }

            return;
        }

        var deltaX = x - _startX;
        var deltaY = y - _startY;
        var direction = ResolveDirection(deltaX, deltaY);
        var threshold = ResolveTriggerThreshold(_gestureOrigin);
        var distance = ResolveTriggerDistance(deltaX, deltaY, direction, _gestureOrigin);

        _window.SetDistance(distance);
        _window.SetDirection(DirectionToText(direction));

        if (_gestureOrigin == GestureOrigin.DesktopFolder && _currentDesktopHit is not null)
        {
            if (_isTriggered)
            {
                return;
            }

            var progress = Math.Clamp(distance / threshold, 0, 1);

            if (distance > threshold)
            {
                _isTriggered = true;
                _window.SetTrackingState("已触发");
                DismissDesktopGesturePreview(immediate: false);
                _window.ShowFolderHit(_currentDesktopHit);
                _window.AddLog($"触发成功：方向={DirectionToText(direction)}，距离={distance:F1}px，文件夹={_currentDesktopHit.FullPath}");
                _ = _panelManager.ShowPanelAsync(_currentDesktopHit, direction);
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
            var progress = Math.Clamp(distance / threshold, 0, 1);
            _panelManager.UpdateChildDragPreview(_currentPanelHit, direction, progress);
            _window.SetTrackingState(progress <= 0
                ? $"已命中面板内文件夹，等待拖动超过 {PanelTriggerThreshold:F0}px"
                : $"正在准备展开下一层（{progress:P0}）");
        }

        if (_isTriggered || distance <= threshold)
        {
            return;
        }

        _isTriggered = true;

        _window.SetTrackingState("已触发");
        switch (_gestureOrigin)
        {
            case GestureOrigin.DesktopFolder when _currentDesktopHit is not null:
                DismissDesktopGesturePreview(immediate: false);
                _window.ShowFolderHit(_currentDesktopHit);
                _window.AddLog($"触发成功：方向={DirectionToText(direction)}，距离={distance:F1}px，文件夹={_currentDesktopHit.FullPath}");
                _ = _panelManager.ShowPanelAsync(_currentDesktopHit, direction);
                break;
            case GestureOrigin.PanelFolder when _currentPanelHit is not null:
                _window.ShowFolderHit(new DesktopFolderHit(_currentPanelHit.Item.DisplayName, _currentPanelHit.Item.FullPath, "panel-folder-hit", _currentPanelHit.Bounds));
                _window.AddLog($"触发级联展开：方向={DirectionToText(direction)}，距离={distance:F1}px，文件夹={_currentPanelHit.Item.FullPath}");
                _ = _panelManager.ShowChildPanelAsync(_currentPanelHit, direction);
                break;
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

        if (_gestureOrigin == GestureOrigin.DesktopFolder && _isTracking)
        {
            _window.AddLog("桌面首层手势已取消：按下 Esc。");
            CancelDesktopGesturePreview();
        }

        if (_panelManager.HasActivePanel)
        {
            _panelManager.CloseAll(PanelCloseReason.EscapeKey);
        }
    }

    private void HandleSpaceKeyAction(KeyActionType actionType)
    {
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
            _window.AddLog("桌面首层手势已取消：Space 已松开。");
            CancelDesktopGesturePreview();
        }
    }

    private void HandleLeftButtonUp()
    {
        if (_isTracking && !_isTriggered)
        {
            _window.AddLog("手势结束：Space + 左键已按下，但拖动距离还没到阈值。");
        }

        var trackedDesktopGesture = _gestureOrigin == GestureOrigin.DesktopFolder;
        ResetTrackingState();
        _window.ResetGesture();

        if (trackedDesktopGesture && !_isSpaceHeld)
        {
            _window.ShowFolderHit(null);
        }

        if (_panelManager.TryActivatePanel())
        {
            _window.AddLog("手势已结束，重新激活展开面板。");
        }
    }

    private void OnTrayShowRequested(object? sender, EventArgs e)
    {
        ShowStatusWindow();
    }

    private void OnTrayToggleRequested(object? sender, EventArgs e)
    {
        ToggleListening();
    }

    private void OnToggleListeningRequested(object? sender, EventArgs e)
    {
        ToggleListening();
    }

    private void OnClosePanelsRequested(object? sender, EventArgs e)
    {
        _panelManager.CloseAll(PanelCloseReason.TrayMenu, logWhenNoPanel: true);
        _window.AddActivity("已关闭全部展开面板。");
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

    private void DismissDesktopGesturePreview(bool immediate)
    {
        _gesturePreviewWindow?.Dismiss(immediate);
    }

    private void EnsurePreviewWindow()
    {
        if (_gesturePreviewWindow is not null)
        {
            return;
        }

        _gesturePreviewWindow = new GesturePreviewWindow();
    }

    private void CancelDesktopGesturePreview()
    {
        ResetTrackingState();
        _window.ResetGesture();
        _window.ShowFolderHit(null);
    }

    private void UpdateDesktopGestureCandidateAtPoint(int x, int y)
    {
        if (!_isSpaceHeld || _isTracking)
        {
            return;
        }

        if (_panelManager.IsPointInsideAnyPanel(x, y))
        {
            SetDesktopGestureCandidate(null);
            return;
        }

        var hit = _resolver.TryResolveFolderFromSnapshotPoint(x, y);
        SetDesktopGestureCandidate(hit);

        if (hit is null)
        {
            _window.ShowFolderHit(null);
            _window.SetTrackingState("起点不是桌面文件夹");
            return;
        }

        _window.ShowFolderHit(hit);
        _window.SetTrackingState($"已命中文件夹，等待拖动超过 {DesktopTriggerThreshold:F0}px");
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
        if (Math.Abs(deltaX) >= Math.Abs(deltaY))
        {
            return deltaX >= 0 ? GestureDirection.Right : GestureDirection.Left;
        }

        return deltaY >= 0 ? GestureDirection.Down : GestureDirection.Up;
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

    private static double ResolveTriggerThreshold(GestureOrigin origin)
    {
        return origin == GestureOrigin.PanelFolder ? PanelTriggerThreshold : DesktopTriggerThreshold;
    }

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

    private static bool IsSpacePressed()
    {
        return (GetAsyncKeyState(VkSpace) & 0x8000) != 0;
    }

    private void ResetTrackingState()
    {
        _panelManager.ClearDragPreview();
        DismissDesktopGesturePreview(immediate: true);
        SetDesktopGestureSuppressed(false);
        _isTracking = false;
        _isTriggered = false;
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

        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(
            ProcessPendingMouseMove,
            DispatcherPriority.Background);
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

        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(
            ProcessPendingMouseMove,
            DispatcherPriority.Background);
    }

    private static bool TryGetCursorScreenPoint(out NativePoint point)
    {
        return GetCursorPos(out point);
    }

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
        public bool IsForPoint(int x, int y)
        {
            return X == x && Y == y;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);
}
