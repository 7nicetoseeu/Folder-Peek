using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpShowWindow = 0x0040;

    private readonly MainWindow _debugWindow;
    private readonly AppSettingsService _settingsService;
    private readonly FolderContentProvider _contentProvider;
    private readonly ShellLauncher _shellLauncher;
    private readonly List<PanelContext> _contexts = new();

    private CancellationTokenSource? _loadCts;
    private long _loadVersion;
    private Guid? _activeChainId;

    public bool HasActivePanel => _contexts.Count > 0;

    public PanelManager(MainWindow debugWindow, AppSettingsService settingsService)
    {
        _debugWindow = debugWindow;
        _settingsService = settingsService;
        _settingsService.PanelVisibleItemCountChanged += SettingsService_OnPanelVisibleItemCountChanged;
        _settingsService.ExpandModeChanged += SettingsService_OnExpandModeChanged;
        var iconProvider = new ShellIconProvider();
        _contentProvider = new FolderContentProvider(iconProvider, _debugWindow.AddLog);
        _shellLauncher = new ShellLauncher();
    }

    public Task ShowPanelAsync(DesktopFolderHit hit, GestureDirection direction)
    {
        CloseTemporaryPanels(PanelCloseReason.NewPanelTriggered);
        _activeChainId = Guid.NewGuid();
        return ShowPanelCoreAsync(
            hit.DisplayName,
            hit.FullPath,
            hit.Bounds,
            direction,
            level: 0,
            chainId: _activeChainId,
            parentContextId: null,
            parentItem: null,
            initialPinMode: PanelPinMode.None);
    }

    public Task ShowChildPanelAsync(PanelFolderHit hit, GestureDirection direction)
    {
        if (!TryGetContext(hit.PanelId, out var parentContext) || parentContext is null || !hit.Item.IsFolder)
        {
            return Task.CompletedTask;
        }

        Guid chainId;
        if (parentContext.ChainId.HasValue && parentContext.ChainId == _activeChainId)
        {
            CloseChainDescendants(parentContext.Id);
            chainId = parentContext.ChainId.Value;
        }
        else
        {
            CloseTemporaryPanels(PanelCloseReason.NewPanelTriggered);
            chainId = Guid.NewGuid();
            _activeChainId = chainId;
        }

        return ShowPanelCoreAsync(
            hit.Item.DisplayName,
            hit.Item.FullPath,
            hit.Bounds,
            direction,
            level: parentContext.Level + 1,
            chainId: chainId,
            parentContextId: parentContext.Id,
            parentItem: hit.Item,
            initialPinMode: PanelPinMode.None);
    }

    public void CloseAll(PanelCloseReason reason = PanelCloseReason.Unknown, bool logWhenNoPanel = false)
    {
        if (_contexts.Count == 0)
        {
            if (logWhenNoPanel)
            {
                _debugWindow.AddLog($"收到关闭面板请求，但当前没有展开面板。原因：{DescribeCloseReason(reason)}");
            }
            return;
        }

        if (ShouldPreservePinnedPanels(reason))
        {
            _debugWindow.AddLog($"关闭临时展开链，保留已钉住窗口。原因：{DescribeCloseReason(reason)}");
            CloseTemporaryPanels(reason);
            return;
        }

        _debugWindow.AddLog($"关闭全部展开面板。原因：{DescribeCloseReason(reason)}");
        CloseAllPanels(reason);
    }

    public void Dispose()
    {
        _settingsService.PanelVisibleItemCountChanged -= SettingsService_OnPanelVisibleItemCountChanged;
        _settingsService.ExpandModeChanged -= SettingsService_OnExpandModeChanged;
        CloseAllPanels(PanelCloseReason.Dispose);
    }

    public void PrewarmFolder(string fullPath)
    {
        _contentProvider.Prewarm(fullPath);
    }

    public bool TryActivatePanel()
    {
        var context = GetLeafContexts()
            .OrderByDescending(item => item.ChainId == _activeChainId)
            .ThenByDescending(item => item.Level)
            .FirstOrDefault();
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
        foreach (var context in _contexts)
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
        if (TryGetTopmostContextAtPoint(x, y, out var topmostContext) &&
            topmostContext!.Window.TryHitTestFolderItem(x, y, out hit))
        {
            return true;
        }

        foreach (var context in EnumerateFallbackHitTestContexts(topmostContext))
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
        if (!TryGetContext(hit.PanelId, out var context) || context is null)
        {
            return;
        }

        context.Window.SetDragPreviewItem(hit.Item, direction, progress);
        _contentProvider.Prewarm(hit.Item.FullPath);
    }

    public void ClearDragPreview()
    {
        foreach (var context in _contexts)
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
        Guid? chainId,
        Guid? parentContextId,
        FolderPanelItem? parentItem,
        PanelPinMode initialPinMode)
    {
        CancelPendingLoad();

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var loadVersion = ++_loadVersion;
        FolderPanelWindow? panel = null;

        try
        {
            var contextId = Guid.NewGuid();
            panel = new FolderPanelWindow
            {
                PanelId = contextId,
                Level = level,
                VisibleItemLimit = _settingsService.PanelVisibleItemCount,
                RequireShortFolderClick = RequiresShortFolderClick(),
                Topmost = ShouldUseTopMost(initialPinMode)
            };
            panel.ShowLoadingState(displayName, fullPath);

            var context = new PanelContext(contextId, level, chainId, parentContextId, parentItem, initialPinMode, panel);
            _contexts.Add(context);

            var loadingPlacement = CalculatePlacement(sourceBoundsPx, direction, PanelStatusMinHeight, PanelDefaultWidth);
            ApplyPlacement(panel, loadingPlacement);

            panel.CloseRequested += OnPanelCloseRequested;
            panel.FileRequested += OnPanelFileRequested;
            panel.FolderRequested += OnPanelFolderRequested;
            panel.PinStateChanged += OnPanelPinStateChanged;
            panel.Closed += OnPanelClosed;

            if (parentContextId.HasValue && parentItem is not null && TryGetContext(parentContextId.Value, out var parentContext))
            {
                parentContext!.Window.SetExpandedItem(parentItem);
            }

            panel.Show();
            ApplyWindowPinMode(context);
            SyncPanelPresentation();

            panel.BeginShowAnimation(direction);
            panel.Activate();

            _debugWindow.AddLog($"正在展开第 {level + 1} 层面板：{fullPath}");

            var items = await _contentProvider.GetItemsAsync(fullPath, cts.Token);
            if (cts.IsCancellationRequested || loadVersion != _loadVersion || !TryGetContext(contextId, out context) || context is null)
            {
                return;
            }

            panel.Bind(displayName, fullPath, items);
            var desiredWidth = CalculatePanelWidth(displayName, items);
            var desiredHeight = panel.GetPreferredWindowHeight();
            ApplyPlacement(panel, CalculatePlacement(sourceBoundsPx, direction, desiredHeight, desiredWidth));
            ApplyWindowPinMode(context);
            SyncPanelPresentation();

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
        if (sender is not FolderPanelWindow panel ||
            !TryGetContext(panel.PanelId, out var context) ||
            context is null)
        {
            return;
        }

        if (e.PinMode == PanelPinMode.None)
        {
            UnpinContext(context);
        }
        else
        {
            PinContext(context, e.PinMode);
        }

        SyncPanelPresentation();
        panel.ShowTransientNotice(GetPinNoticeTitle(e.PinMode), GetPinNoticeDetail(e.PinMode));
        panel.Activate();
    }

    private void SettingsService_OnPanelVisibleItemCountChanged(object? sender, EventArgs e)
    {
        foreach (var context in _contexts)
        {
            context.Window.SetVisibleItemLimit(_settingsService.PanelVisibleItemCount);
        }
    }

    private void SettingsService_OnExpandModeChanged(object? sender, EventArgs e)
    {
        var requireShortClick = RequiresShortFolderClick();
        foreach (var context in _contexts)
        {
            context.Window.RequireShortFolderClick = requireShortClick;
        }
    }

    private bool RequiresShortFolderClick()
    {
        return _settingsService.ExpandMode is FolderExpandMode.LongPressLeft or FolderExpandMode.LongPressRight;
    }

    private void OnPanelFileRequested(object? sender, string fullPath)
    {
        if (_shellLauncher.TryOpen(fullPath, out var errorMessage))
        {
            _debugWindow.AddLog($"已打开文件：{fullPath}");
            _debugWindow.AddActivity($"已打开文件：{Path.GetFileName(fullPath)}");
            CloseTemporaryPanels(PanelCloseReason.FileOpened);
            return;
        }

        _debugWindow.AddLog($"打开文件失败：{fullPath}，{errorMessage}");
        _debugWindow.AddActivity($"打开文件失败：{Path.GetFileName(fullPath)}");
        if (sender is FolderPanelWindow panel)
        {
            panel.ShowTransientNotice("打开失败", string.IsNullOrWhiteSpace(errorMessage) ? "系统没有返回更多信息。" : errorMessage);
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

        if (!TryGetContext(panel.PanelId, out var context) || context is null)
        {
            DetachPanel(panel);
            return;
        }

        CloseDescendants(context.Id);
        _contexts.Remove(context);
        DetachPanel(panel);

        if (context.ParentContextId.HasValue && TryGetContext(context.ParentContextId.Value, out var parentContext))
        {
            parentContext!.Window.ClearExpandedState();
        }

        if (_activeChainId.HasValue && !_contexts.Any(item => item.ChainId == _activeChainId))
        {
            _activeChainId = null;
        }

        SyncPanelPresentation();
    }

    private void PinContext(PanelContext context, PanelPinMode pinMode)
    {
        if (context.ChainId.HasValue && context.ChainId == _activeChainId)
        {
            CloseChainDescendants(context.Id);
        }

        context.ParentContextId = null;
        context.ParentItem = null;
        CloseAncestorChain(context.Id);
        context.ChainId = null;
        context.PinMode = pinMode;

        if (_activeChainId.HasValue && !_contexts.Any(item => item.Id != context.Id && item.ChainId == _activeChainId))
        {
            _activeChainId = null;
        }

        ApplyWindowPinMode(context);
    }

    private void UnpinContext(PanelContext context)
    {
        CloseTemporaryPanels(PanelCloseReason.Unknown);
        _activeChainId = Guid.NewGuid();
        context.ChainId = _activeChainId;
        context.PinMode = PanelPinMode.None;
        context.ParentContextId = null;
        context.ParentItem = null;
        ApplyWindowPinMode(context);
    }

    private void CloseTemporaryPanels(PanelCloseReason reason)
    {
        CancelPendingLoad();

        if (!_activeChainId.HasValue)
        {
            return;
        }

        var activeChainId = _activeChainId.Value;
        foreach (var context in _contexts
                     .Where(item => item.ChainId == activeChainId)
                     .OrderByDescending(item => item.Level)
                     .ToArray())
        {
            _contexts.Remove(context);
            ClosePanelContext(context);
        }

        _activeChainId = null;
    }

    private void CloseAllPanels(PanelCloseReason reason)
    {
        CancelPendingLoad();

        foreach (var context in _contexts.OrderByDescending(item => item.Level).ToArray())
        {
            _contexts.Remove(context);
            ClosePanelContext(context);
        }

        _activeChainId = null;
    }

    private void CloseChainDescendants(Guid parentContextId)
    {
        foreach (var context in GetDescendants(parentContextId)
                     .OrderByDescending(item => item.Level)
                     .ToArray())
        {
            _contexts.Remove(context);
            ClosePanelContext(context);
        }

        if (TryGetContext(parentContextId, out var parentContext))
        {
            parentContext!.Window.ClearExpandedState();
        }

        if (_activeChainId.HasValue && !_contexts.Any(item => item.ChainId == _activeChainId))
        {
            _activeChainId = null;
        }
    }

    private void CloseAncestorChain(Guid contextId)
    {
        if (!TryGetContext(contextId, out var context) || context is null)
        {
            return;
        }

        var ancestorId = context.ParentContextId;
        while (ancestorId.HasValue && TryGetContext(ancestorId.Value, out var ancestor) && ancestor is not null)
        {
            var nextAncestorId = ancestor.ParentContextId;
            _contexts.Remove(ancestor);
            ClosePanelContext(ancestor);
            ancestorId = nextAncestorId;
        }

        if (_activeChainId.HasValue && !_contexts.Any(item => item.ChainId == _activeChainId))
        {
            _activeChainId = null;
        }
    }

    private void CloseDescendants(Guid contextId)
    {
        foreach (var context in GetDescendants(contextId)
                     .OrderByDescending(item => item.Level)
                     .ToArray())
        {
            _contexts.Remove(context);
            ClosePanelContext(context);
        }
    }

    private IEnumerable<PanelContext> GetDescendants(Guid parentContextId)
    {
        var descendants = new List<PanelContext>();
        var queue = new Queue<Guid>();
        queue.Enqueue(parentContextId);

        while (queue.Count > 0)
        {
            var currentParentId = queue.Dequeue();
            foreach (var child in _contexts.Where(item => item.ParentContextId == currentParentId).ToArray())
            {
                descendants.Add(child);
                queue.Enqueue(child.Id);
            }
        }

        return descendants;
    }

    private IEnumerable<PanelContext> GetLeafContexts()
    {
        return _contexts.Where(context => !_contexts.Any(child => child.ParentContextId == context.Id));
    }

    private void ClosePanelContext(PanelContext context)
    {
        if (context.ParentContextId.HasValue && TryGetContext(context.ParentContextId.Value, out var parentContext))
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

    private bool TryGetContext(Guid panelId, out PanelContext? context)
    {
        context = _contexts.FirstOrDefault(item => item.Id == panelId);
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
        return _contexts.Any(item => ReferenceEquals(item.Window, panel));
    }

    private void SyncPanelPresentation()
    {
        foreach (var context in _contexts)
        {
            var isLeaf = !_contexts.Any(child => child.ParentContextId == context.Id);
            var canDrag = context.PinMode.IsPinned() && !_contexts.Any(parent => parent.Id == context.ParentContextId);
            context.Window.SetPinState(isLeaf, context.PinMode, canDrag);
            ApplyWindowPinMode(context);
        }
    }

    private void ApplyWindowPinMode(PanelContext context)
    {
        var handle = new WindowInteropHelper(context.Window).Handle;
        var shouldUseTopMost = ShouldUseTopMost(context.PinMode);
        context.Window.Topmost = shouldUseTopMost;

        if (handle == IntPtr.Zero)
        {
            return;
        }

        var target = shouldUseTopMost ? HwndTopMost : HwndNoTopMost;
        _ = SetWindowPos(handle, target, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | SwpNoOwnerZOrder | SwpShowWindow);
    }

    private static bool ShouldUseTopMost(PanelPinMode pinMode)
    {
        return pinMode != PanelPinMode.PinnedToDesktop;
    }

    private static bool ShouldPreservePinnedPanels(PanelCloseReason reason)
    {
        return reason is PanelCloseReason.LostFocus or
            PanelCloseReason.EscapeKey or
            PanelCloseReason.FileOpened or
            PanelCloseReason.NewPanelTriggered;
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

    private static string GetPinNoticeTitle(PanelPinMode pinMode)
    {
        return pinMode switch
        {
            PanelPinMode.None => "已恢复默认",
            PanelPinMode.PinnedToDesktop => "已钉在桌面",
            PanelPinMode.PinnedTopmost => "已全局置顶",
            _ => "已更新图钉状态"
        };
    }

    private static string GetPinNoticeDetail(PanelPinMode pinMode)
    {
        return pinMode switch
        {
            PanelPinMode.None => "当前窗口恢复默认自动关闭行为；再次点图钉可重新保留。",
            PanelPinMode.PinnedToDesktop => "当前窗口会保留在桌面工作区里，但不会继续压在后续打开的程序窗口之上。",
            PanelPinMode.PinnedTopmost => "当前窗口会保留并继续显示在后续打开的程序窗口之上。",
            _ => "图钉状态已更新。"
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

    private bool TryGetTopmostContextAtPoint(int x, int y, out PanelContext? context)
    {
        var handle = WindowFromPoint(new NativePoint(x, y));
        if (handle != IntPtr.Zero)
        {
            context = _contexts.FirstOrDefault(item => new WindowInteropHelper(item.Window).Handle == handle);
            if (context is not null)
            {
                return true;
            }
        }

        context = null;
        return false;
    }

    private IEnumerable<PanelContext> EnumerateFallbackHitTestContexts(PanelContext? excludedContext)
    {
        for (var index = _contexts.Count - 1; index >= 0; index--)
        {
            var context = _contexts[index];
            if (ReferenceEquals(context, excludedContext))
            {
                continue;
            }

            yield return context;
        }
    }

    private sealed class PanelContext
    {
        public PanelContext(
            Guid id,
            int level,
            Guid? chainId,
            Guid? parentContextId,
            FolderPanelItem? parentItem,
            PanelPinMode pinMode,
            FolderPanelWindow window)
        {
            Id = id;
            Level = level;
            ChainId = chainId;
            ParentContextId = parentContextId;
            ParentItem = parentItem;
            PinMode = pinMode;
            Window = window;
        }

        public Guid Id { get; }

        public int Level { get; }

        public Guid? ChainId { get; set; }

        public Guid? ParentContextId { get; set; }

        public FolderPanelItem? ParentItem { get; set; }

        public PanelPinMode PinMode { get; set; }

        public FolderPanelWindow Window { get; }
    }

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

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
