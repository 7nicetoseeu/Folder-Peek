using System.Runtime.InteropServices;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace FolderPeek.App;

public sealed class PanelManager : IDisposable
{
    private const double PanelMinWidth = 310;
    private const double PanelDefaultWidth = 380;
    private const double PanelMaxWidth = 520;
    private const double PanelGap = 8;
    private const double PanelStatusMinHeight = 220;

    private readonly MainWindow _debugWindow;
    private readonly AppSettingsService _settingsService;
    private readonly FolderContentProvider _contentProvider;
    private readonly ShellLauncher _shellLauncher;
    private readonly List<PanelContext> _panelStack = new();

    private CancellationTokenSource? _loadCts;
    private long _loadVersion;
    private bool _isPinned;

    public bool HasActivePanel => _panelStack.Count > 0;

    public PanelManager(MainWindow debugWindow, AppSettingsService settingsService)
    {
        _debugWindow = debugWindow;
        _settingsService = settingsService;
        _settingsService.PanelVisibleItemCountChanged += SettingsService_OnPanelVisibleItemCountChanged;
        var iconProvider = new ShellIconProvider();
        _contentProvider = new FolderContentProvider(iconProvider, _debugWindow.AddLog);
        _shellLauncher = new ShellLauncher();
    }

    public Task ShowPanelAsync(DesktopFolderHit hit, GestureDirection direction)
    {
        CloseAll(PanelCloseReason.NewPanelTriggered);
        return ShowPanelCoreAsync(hit.DisplayName, hit.FullPath, hit.Bounds, direction, level: 0, parentLevel: null, parentItem: null);
    }

    public Task ShowChildPanelAsync(PanelFolderHit hit, GestureDirection direction)
    {
        if (!hit.Item.IsFolder)
        {
            return Task.CompletedTask;
        }

        ClosePanelsAfterLevel(hit.Level);
        return ShowPanelCoreAsync(
            hit.Item.DisplayName,
            hit.Item.FullPath,
            hit.Bounds,
            direction,
            level: hit.Level + 1,
            parentLevel: hit.Level,
            parentItem: hit.Item);
    }

    public void CloseAll(PanelCloseReason reason = PanelCloseReason.Unknown, bool logWhenNoPanel = false)
    {
        CancelPendingLoad();

        if (_panelStack.Count == 0)
        {
            if (logWhenNoPanel)
            {
                _debugWindow.AddLog($"收到关闭面板请求，但当前没有展开面板。原因：{DescribeCloseReason(reason)}");
            }
            return;
        }

        _debugWindow.AddLog($"关闭全部展开面板。原因：{DescribeCloseReason(reason)}");
        if (ShouldBlockClose(reason))
        {
            ShowPinnedNotice(reason);
            return;
        }

        foreach (var context in _panelStack.OrderByDescending(item => item.Level).ToArray())
        {
            ClosePanelContext(context);
        }

        _panelStack.Clear();
        _isPinned = false;
    }

    public void Dispose()
    {
        _settingsService.PanelVisibleItemCountChanged -= SettingsService_OnPanelVisibleItemCountChanged;
        CloseAll(PanelCloseReason.Dispose);
    }

    public void PrewarmFolder(string fullPath)
    {
        _contentProvider.Prewarm(fullPath);
    }

    public bool TryActivatePanel()
    {
        var context = _panelStack.OrderByDescending(item => item.Level).FirstOrDefault();
        if (context is null)
        {
            return false;
        }

        context.Window.Activate();
        context.Window.Focus();
        return true;
    }

    public bool IsPointInsideAnyPanel(int x, int y)
    {
        foreach (var context in _panelStack)
        {
            if (!TryGetWindowRect(context.Window, out var rect))
            {
                continue;
            }

            if (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    public bool TryHitTestFolderItem(int x, int y, out PanelFolderHit? hit)
    {
        foreach (var context in _panelStack.OrderByDescending(item => item.Level))
        {
            if (context.Window.TryHitTestFolderItem(x, y, out hit))
            {
                return true;
            }
        }

        hit = null;
        return false;
    }

    public void UpdateChildDragPreview(PanelFolderHit hit, GestureDirection direction, double progress)
    {
        if (!TryGetContext(hit.Level, out var context) || context is null)
        {
            return;
        }

        context.Window.SetDragPreviewItem(hit.Item, direction, progress);
        _contentProvider.Prewarm(hit.Item.FullPath);
    }

    public void ClearDragPreview()
    {
        foreach (var context in _panelStack)
        {
            context.Window.ClearDragPreview();
        }
    }

    private async Task ShowPanelCoreAsync(
        string displayName,
        string fullPath,
        Rect sourceBoundsPx,
        GestureDirection direction,
        int level,
        int? parentLevel,
        FolderPanelItem? parentItem)
    {
        CancelPendingLoad();
        ClosePanelsFromLevel(level);

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var loadVersion = ++_loadVersion;
        FolderPanelWindow? panel = null;

        try
        {
            panel = new FolderPanelWindow
            {
                Level = level,
                VisibleItemLimit = _settingsService.PanelVisibleItemCount
            };
            panel.ShowLoadingState(displayName, fullPath);

            var loadingPlacement = CalculatePlacement(sourceBoundsPx, direction, PanelStatusMinHeight, PanelDefaultWidth);
            ApplyPlacement(panel, loadingPlacement);

            panel.CloseRequested += OnPanelCloseRequested;
            panel.FileRequested += OnPanelFileRequested;
            panel.FolderRequested += OnPanelFolderRequested;
            panel.PinStateChanged += OnPanelPinStateChanged;
            panel.Closed += OnPanelClosed;

            var context = new PanelContext(level, parentLevel, parentItem, panel);
            _panelStack.Add(context);

            if (parentLevel.HasValue && parentItem is not null && TryGetContext(parentLevel.Value, out var parentContext))
            {
                parentContext!.Window.SetExpandedItem(parentItem);
            }

            SyncPinStateToPanels();

            panel.Show();
            panel.BeginShowAnimation(direction);
            panel.Activate();

            _debugWindow.AddLog($"正在展开第 {level + 1} 层面板：{fullPath}");

            var items = await _contentProvider.GetItemsAsync(fullPath, cts.Token);
            if (cts.IsCancellationRequested || loadVersion != _loadVersion)
            {
                return;
            }

            panel.Bind(displayName, fullPath, items);
            var desiredWidth = CalculatePanelWidth(displayName, items);
            var desiredHeight = panel.GetPreferredWindowHeight();
            ApplyPlacement(panel, CalculatePlacement(sourceBoundsPx, direction, desiredHeight, desiredWidth));

            _debugWindow.AddLog($"已展开第 {level + 1} 层面板：{fullPath}，项目数={items.Count}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (panel is not null && IsPanelTracked(panel))
            {
                panel.ShowErrorState(ex.Message);
            }

            _debugWindow.AddLog($"展开面板失败：{ex.Message}");
            _debugWindow.AddLog(ex.ToString());
        }
        finally
        {
            if (_loadCts == cts)
            {
                _loadCts = null;
            }

            cts.Dispose();
        }
    }

    private void OnPanelCloseRequested(object? sender, PanelCloseRequestEventArgs e)
    {
        CloseAll(e.Reason);
    }

    private void OnPanelPinStateChanged(object? sender, PanelPinnedChangedEventArgs e)
    {
        _isPinned = e.IsPinned;
        SyncPinStateToPanels();

        if (sender is FolderPanelWindow panel)
        {
            panel.ShowTransientNotice(
                e.IsPinned ? "已钉住展开栏" : "已取消钉住",
                e.IsPinned ? "点击外部、按 Esc 或打开文件后都不会自动关闭。" : "展开栏已恢复默认关闭行为。");
            panel.Activate();
        }
    }

    private void SettingsService_OnPanelVisibleItemCountChanged(object? sender, EventArgs e)
    {
        foreach (var context in _panelStack)
        {
            context.Window.SetVisibleItemLimit(_settingsService.PanelVisibleItemCount);
        }
    }

    private void OnPanelFileRequested(object? sender, string fullPath)
    {
        if (_shellLauncher.TryOpen(fullPath, out var errorMessage))
        {
            _debugWindow.AddLog($"已打开文件：{fullPath}");
            _debugWindow.AddActivity($"已打开文件：{Path.GetFileName(fullPath)}");
            if (_isPinned)
            {
                ShowPinnedNotice(PanelCloseReason.FileOpened);
            }
            else
            {
                CloseAll(PanelCloseReason.FileOpened);
            }
            return;
        }

        _debugWindow.AddLog($"打开文件失败：{fullPath}；{errorMessage}");
        _debugWindow.AddActivity($"打开文件失败：{Path.GetFileName(fullPath)}");
        if (sender is FolderPanelWindow panel)
        {
            var detail = string.IsNullOrWhiteSpace(errorMessage)
                ? "系统没有返回更多信息。"
                : errorMessage;
            panel.ShowTransientNotice("打开失败", detail);
            panel.Activate();
        }
    }

    private void OnPanelFolderRequested(object? sender, PanelFolderHit hit)
    {
        var direction = ResolveClickExpandDirection(hit);
        _debugWindow.AddLog($"点击展开第 {hit.Level + 2} 层面板：{hit.Item.FullPath}");
        _ = ShowChildPanelAsync(hit, direction);
    }

    private void OnPanelClosed(object? sender, EventArgs e)
    {
        if (sender is not FolderPanelWindow panel)
        {
            return;
        }

        var context = _panelStack.FirstOrDefault(item => ReferenceEquals(item.Window, panel));
        if (context is null)
        {
            DetachPanel(panel);
            return;
        }

        _panelStack.Remove(context);
        DetachPanel(panel);

        if (context.ParentLevel.HasValue && TryGetContext(context.ParentLevel.Value, out var parentContext))
        {
            parentContext!.Window.ClearExpandedState();
        }

        ClosePanelsFromLevel(context.Level + 1);
        if (_panelStack.Count == 0)
        {
            _isPinned = false;
        }

        SyncPinStateToPanels();
    }

    private void ClosePanelsAfterLevel(int level)
    {
        CancelPendingLoad();
        ClosePanelsFromLevel(level + 1);

        if (TryGetContext(level, out var context))
        {
            context!.Window.ClearExpandedState();
        }

        SyncPinStateToPanels();
    }

    private void ClosePanelsFromLevel(int startLevel)
    {
        foreach (var context in _panelStack
                     .Where(item => item.Level >= startLevel)
                     .OrderByDescending(item => item.Level)
                     .ToArray())
        {
            _panelStack.Remove(context);
            ClosePanelContext(context);
        }

        if (_panelStack.Count == 0)
        {
            _isPinned = false;
        }
    }

    private void ClosePanelContext(PanelContext context)
    {
        if (context.ParentLevel.HasValue && TryGetContext(context.ParentLevel.Value, out var parentContext))
        {
            parentContext!.Window.ClearExpandedState();
        }

        DetachPanel(context.Window);
        context.Window.CloseAnimated();
    }

    private void DetachPanel(FolderPanelWindow panel)
    {
        panel.CloseRequested -= OnPanelCloseRequested;
        panel.FileRequested -= OnPanelFileRequested;
        panel.FolderRequested -= OnPanelFolderRequested;
        panel.PinStateChanged -= OnPanelPinStateChanged;
        panel.Closed -= OnPanelClosed;
    }

    private bool TryGetContext(int level, out PanelContext? context)
    {
        context = _panelStack.FirstOrDefault(item => item.Level == level);
        return context is not null;
    }

    private void CancelPendingLoad()
    {
        if (_loadCts is null)
        {
            return;
        }

        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = null;
    }

    private static void ApplyPlacement(FolderPanelWindow panel, PanelPlacement placement)
    {
        panel.Left = placement.Left;
        panel.Top = placement.Top;
        panel.Width = placement.Width;
        panel.MaxHeight = placement.MaxHeight;
    }

    private bool IsPanelTracked(FolderPanelWindow panel)
    {
        return _panelStack.Any(item => ReferenceEquals(item.Window, panel));
    }

    private bool ShouldBlockClose(PanelCloseReason reason)
    {
        return _isPinned && reason is PanelCloseReason.LostFocus or PanelCloseReason.EscapeKey or PanelCloseReason.FileOpened;
    }

    private void ShowPinnedNotice(PanelCloseReason reason)
    {
        var context = _panelStack.OrderByDescending(item => item.Level).FirstOrDefault();
        if (context is null)
        {
            return;
        }

        context.Window.ShowTransientNotice("当前已钉住", DescribePinnedBlockReason(reason));
        context.Window.Activate();
        _debugWindow.AddLog($"已拦截关闭请求：{DescribeCloseReason(reason)}");
    }

    private void SyncPinStateToPanels()
    {
        if (_panelStack.Count == 0)
        {
            return;
        }

        var deepestLevel = _panelStack.Max(item => item.Level);
        foreach (var context in _panelStack)
        {
            context.Window.SetPinState(context.Level == deepestLevel, _isPinned);
        }
    }

    private static string DescribeCloseReason(PanelCloseReason reason)
    {
        return reason switch
        {
            PanelCloseReason.LostFocus => "点到面板外部或窗口失去焦点",
            PanelCloseReason.EscapeKey => "按下 Esc",
            PanelCloseReason.FileOpened => "成功打开文件后自动关闭",
            PanelCloseReason.TrayMenu => "托盘菜单请求关闭",
            PanelCloseReason.ListeningPaused => "暂停监听时自动关闭",
            PanelCloseReason.NewPanelTriggered => "触发新面板前关闭旧面板",
            PanelCloseReason.AppExit => "应用退出时关闭",
            PanelCloseReason.Dispose => "应用释放资源时关闭",
            _ => "未标记原因"
        };
    }

    private static string DescribePinnedBlockReason(PanelCloseReason reason)
    {
        return reason switch
        {
            PanelCloseReason.LostFocus => "点击外部不会关闭。先取消钉住，再点面板外部即可收起。",
            PanelCloseReason.EscapeKey => "当前展开栏已钉住。先取消钉住，再按 Esc 关闭。",
            PanelCloseReason.FileOpened => "文件已经打开，但当前展开栏保持展开。先取消钉住后才会恢复自动收起。",
            _ => "当前展开栏已钉住。先取消钉住后才能自动关闭。"
        };
    }

    private static GestureDirection ResolveClickExpandDirection(PanelFolderHit hit)
    {
        var center = new System.Drawing.Point(
            (int)(hit.Bounds.Left + (hit.Bounds.Width / 2)),
            (int)(hit.Bounds.Top + (hit.Bounds.Height / 2)));
        var screen = Forms.Screen.FromPoint(center);
        var rightSpace = screen.WorkingArea.Right - hit.Bounds.Right;
        var leftSpace = hit.Bounds.Left - screen.WorkingArea.Left;

        if (rightSpace >= PanelDefaultWidth + PanelGap || rightSpace >= leftSpace)
        {
            return GestureDirection.Right;
        }

        return GestureDirection.Left;
    }

    private static double CalculatePanelWidth(string displayName, IReadOnlyList<FolderPanelItem> items)
    {
        var widestHeader = MeasureTextWidth(displayName, 15, FontWeights.SemiBold) + 44;
        var widestRow = items.Count == 0
            ? PanelDefaultWidth
            : items.Max(item =>
                68 +
                MeasureTextWidth(item.DisplayName, 13, FontWeights.Normal) +
                (string.IsNullOrWhiteSpace(item.SecondaryText)
                    ? 0
                    : 12 + MeasureTextWidth(item.SecondaryText, 11, FontWeights.Normal)));

        var desiredWidth = Math.Max(widestHeader, widestRow);
        return Math.Clamp(desiredWidth, PanelMinWidth, PanelMaxWidth);
    }

    private static double MeasureTextWidth(string text, double fontSize, FontWeight fontWeight)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, fontWeight, FontStretches.Normal),
            fontSize,
            System.Windows.Media.Brushes.Black,
            1.0);

        return Math.Ceiling(formatted.WidthIncludingTrailingWhitespace);
    }

    private static PanelPlacement CalculatePlacement(Rect sourceBoundsPx, GestureDirection direction, double desiredHeight, double width)
    {
        var center = new System.Windows.Point(
            sourceBoundsPx.Left + (sourceBoundsPx.Width / 2),
            sourceBoundsPx.Top + (sourceBoundsPx.Height / 2));

        var scale = GetScaleForPoint(center);
        var sourceBounds = new Rect(
            sourceBoundsPx.Left / scale,
            sourceBoundsPx.Top / scale,
            sourceBoundsPx.Width / scale,
            sourceBoundsPx.Height / scale);

        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)center.X, (int)center.Y));
        var workingArea = ResolveWorkingArea(screen, scale, width);

        var maxHeight = Math.Max(PanelStatusMinHeight, workingArea.Height - 24);
        desiredHeight = Math.Min(desiredHeight, maxHeight);

        var left = sourceBounds.Left;
        var top = sourceBounds.Top;

        switch (direction)
        {
            case GestureDirection.Right:
                left = sourceBounds.Right + PanelGap;
                top = sourceBounds.Top - 6;
                if (left + width > workingArea.Right && sourceBounds.Left - PanelGap - width >= workingArea.Left)
                {
                    left = sourceBounds.Left - PanelGap - width;
                }
                break;
            case GestureDirection.Left:
                left = sourceBounds.Left - PanelGap - width;
                top = sourceBounds.Top - 6;
                if (left < workingArea.Left && sourceBounds.Right + PanelGap + width <= workingArea.Right)
                {
                    left = sourceBounds.Right + PanelGap;
                }
                break;
            case GestureDirection.Down:
                left = sourceBounds.Left;
                top = sourceBounds.Bottom + PanelGap;
                if (top + desiredHeight > workingArea.Bottom && sourceBounds.Top - PanelGap - desiredHeight >= workingArea.Top)
                {
                    top = sourceBounds.Top - PanelGap - desiredHeight;
                }
                break;
            case GestureDirection.Up:
                left = sourceBounds.Left;
                top = sourceBounds.Top - PanelGap - desiredHeight;
                if (top < workingArea.Top && sourceBounds.Bottom + PanelGap + desiredHeight <= workingArea.Bottom)
                {
                    top = sourceBounds.Bottom + PanelGap;
                }
                break;
        }

        var maxLeft = Math.Max(workingArea.Left, workingArea.Right - width);
        var maxTop = Math.Max(workingArea.Top, workingArea.Bottom - desiredHeight);

        left = Math.Clamp(left, workingArea.Left, maxLeft);
        top = Math.Clamp(top, workingArea.Top, maxTop);

        return new PanelPlacement(left, top, width, desiredHeight, maxHeight);
    }

    private static Rect ResolveWorkingArea(Forms.Screen? screen, double scale, double width)
    {
        if (screen is not null)
        {
            return new Rect(
                screen.WorkingArea.Left / scale,
                screen.WorkingArea.Top / scale,
                Math.Max(screen.WorkingArea.Width / scale, width),
                Math.Max(screen.WorkingArea.Height / scale, PanelStatusMinHeight));
        }

        var fallback = SystemParameters.WorkArea;
        return new Rect(
            fallback.Left,
            fallback.Top,
            Math.Max(fallback.Width, width),
            Math.Max(fallback.Height, PanelStatusMinHeight));
    }

    private static double GetScaleForPoint(System.Windows.Point point)
    {
        var monitor = MonitorFromPoint(new NativePoint((int)point.X, (int)point.Y), 2);
        if (monitor == IntPtr.Zero)
        {
            return 1.0;
        }

        return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96d : 1.0;
    }

    private static bool TryGetWindowRect(Window window, out NativeRect rect)
    {
        rect = default;

        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && GetWindowRect(handle, out rect);
    }

    private sealed record PanelContext(int Level, int? ParentLevel, FolderPanelItem? ParentItem, FolderPanelWindow Window);

    private sealed record PanelPlacement(double Left, double Top, double Width, double Height, double MaxHeight);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
