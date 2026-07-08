using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WpfBrush = System.Windows.Media.Brush;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfListViewItem = System.Windows.Controls.ListViewItem;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace FolderPeek.App;

public partial class FolderPanelWindow : Window
{
    private const double AnimationOffset = 14;
    private const double PanelListRowHeight = 46;
    private const double StatusMinHeight = 220;
    private const double ContentMinHeight = 124;
    private static readonly Duration AnimationDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration CloseAnimationDuration = new(TimeSpan.FromMilliseconds(110));

    private IReadOnlyList<FolderPanelItem> _items = Array.Empty<FolderPanelItem>();
    private GestureDirection _lastAnimationDirection = GestureDirection.Right;
    private bool _closeAnimationStarted;
    private DispatcherTimer? _noticeTimer;
    private WpfPoint? _mouseDownPoint;
    private FolderPanelItem? _mouseDownItem;
    private bool _suppressMouseUpAction;
    private PanelPinMode _currentPinMode = PanelPinMode.None;
    private bool _canDragWindow;

    public FolderPanelWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ItemList?.Focus();
    }

    public Guid PanelId { get; set; }

    public int Level { get; set; }

    public int VisibleItemLimit { get; set; } = AppSettingsService.DefaultPanelVisibleItemCount;

    public event EventHandler<PanelCloseRequestEventArgs>? CloseRequested;

    public event EventHandler<string>? FileRequested;

    public event EventHandler<PanelFolderHit>? FolderRequested;

    public event EventHandler<PanelPinnedChangedEventArgs>? PinStateChanged;

    public void SetVisibleItemLimit(int visibleItemLimit)
    {
        VisibleItemLimit = visibleItemLimit;
        if (_items.Count > 0)
        {
            PrepareItemListLayout(_items.Count);
        }
    }

    public void ShowLoadingState(string displayName, string folderPath)
    {
        EnsureControlsInitialized();
        PrepareStatusLayout();

        FolderNameText.Text = displayName;
        FolderPathText.Text = folderPath;
        ItemCountText.Text = "正在读取...";
        ItemList.ItemsSource = null;
        _items = Array.Empty<FolderPanelItem>();
        HideNotice();
        ClearExpandedState();
        ShowStatus("正在读取文件夹内容...", "请稍候，Folder Peek 正在准备这一层内容。", isLoading: true);
    }

    public void Bind(string displayName, string folderPath, IReadOnlyList<FolderPanelItem> items)
    {
        EnsureControlsInitialized();

        FolderNameText.Text = displayName;
        FolderPathText.Text = folderPath;
        ItemCountText.Text = items.Count == 0 ? "空文件夹" : $"{items.Count} 个项目";
        ItemList.ItemsSource = items;
        _items = items;
        HideNotice();
        ClearExpandedState();

        if (items.Count == 0)
        {
            PrepareStatusLayout();
            ShowStatus("这个文件夹是空的", "当前没有可显示的文件或子文件夹。", isLoading: false);
            return;
        }

        StatusPanel.Visibility = Visibility.Collapsed;
        ItemList.Visibility = Visibility.Visible;
        PrepareItemListLayout(items.Count);
    }

    public void ShowErrorState(string message)
    {
        EnsureControlsInitialized();

        ItemCountText.Text = "读取失败";
        ItemList.ItemsSource = null;
        _items = Array.Empty<FolderPanelItem>();
        HideNotice();
        ClearExpandedState();
        ShowStatus("读取这一层内容时出了点问题", message, isLoading: false);
    }

    public void ShowTransientNotice(string title, string detail, TimeSpan? duration = null)
    {
        EnsureControlsInitialized();

        NoticeTitleText.Text = title;
        NoticeDetailText.Text = detail;
        NoticeBanner.Visibility = Visibility.Visible;

        _noticeTimer ??= new DispatcherTimer();
        _noticeTimer.Stop();
        _noticeTimer.Interval = duration ?? TimeSpan.FromSeconds(2.8);
        _noticeTimer.Tick -= NoticeTimer_OnTick;
        _noticeTimer.Tick += NoticeTimer_OnTick;
        _noticeTimer.Start();
    }

    public void SetPinState(bool isVisible, PanelPinMode pinMode, bool canDragWindow)
    {
        EnsureControlsInitialized();

        _currentPinMode = pinMode;
        _canDragWindow = canDragWindow;
        PinToggleButton.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        PinToggleButton.ToolTip = BuildPinTooltip(pinMode);
        PinToggleButton.Background = ResolvePinBackground(pinMode);
        PinToggleButton.BorderBrush = ResolvePinBorderBrush(pinMode);
        PinToggleButton.Foreground = ResolvePinForeground(pinMode);
        PinGlyphText.RenderTransform = new RotateTransform(ResolvePinRotation(pinMode));
        TitleBarBorder.Cursor = canDragWindow ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.Arrow;
    }

    public double GetPreferredWindowHeight()
    {
        EnsureControlsInitialized();

        UpdateLayout();
        FindListScrollViewer()?.ScrollToTop();
        UpdateLayout();
        return Math.Ceiling(ActualHeight);
    }

    public void ClearExpandedState()
    {
        foreach (var item in _items)
        {
            item.IsExpanded = false;
        }
    }

    public void SetExpandedItem(FolderPanelItem? expandedItem)
    {
        foreach (var item in _items)
        {
            item.ClearDragPreview();
            item.IsExpanded = ReferenceEquals(item, expandedItem);
        }
    }

    public void SetDragPreviewItem(FolderPanelItem previewItem, GestureDirection direction, double progress)
    {
        foreach (var item in _items)
        {
            if (ReferenceEquals(item, previewItem))
            {
                item.SetDragPreview(direction, progress);
                continue;
            }

            item.ClearDragPreview();
        }
    }

    public void ClearDragPreview()
    {
        foreach (var item in _items)
        {
            item.ClearDragPreview();
        }
    }

    public bool TryHitTestFolderItem(int screenX, int screenY, out PanelFolderHit? hit)
    {
        EnsureControlsInitialized();

        hit = null;

        try
        {
            if (!IsVisible || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
            {
                return false;
            }

            var point = PointFromScreen(new WpfPoint(screenX, screenY));
            if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight)
            {
                return false;
            }

            if (InputHitTest(point) is not DependencyObject target ||
                ItemsControl.ContainerFromElement(ItemList, target) is not WpfListViewItem container ||
                container.DataContext is not FolderPanelItem item ||
                !item.IsFolder)
            {
                return false;
            }

            var topLeft = container.PointToScreen(new WpfPoint(0, 0));
            var bottomRight = container.PointToScreen(new WpfPoint(container.ActualWidth, container.ActualHeight));
            hit = new PanelFolderHit(item, new Rect(topLeft, bottomRight), Level, PanelId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void BeginShowAnimation(GestureDirection direction)
    {
        EnsureControlsInitialized();

        _lastAnimationDirection = direction;
        _closeAnimationStarted = false;
        Opacity = 0;

        var offset = ResolveOffset(direction, isClosing: false);
        ChromeTransform.X = offset.X;
        ChromeTransform.Y = offset.Y;

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, AnimationDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        ChromeTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset.X, 0, AnimationDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        ChromeTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset.Y, 0, AnimationDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    public void CloseAnimated()
    {
        EnsureControlsInitialized();

        if (_closeAnimationStarted)
        {
            return;
        }

        _closeAnimationStarted = true;
        IsHitTestVisible = false;
        _noticeTimer?.Stop();

        var offset = ResolveOffset(_lastAnimationDirection, isClosing: true);

        var opacityAnimation = new DoubleAnimation(Opacity, 0, CloseAnimationDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        opacityAnimation.Completed += (_, _) =>
        {
            if (IsLoaded)
            {
                Close();
            }
        };

        BeginAnimation(OpacityProperty, opacityAnimation);
        ChromeTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(ChromeTransform.X, offset.X, CloseAnimationDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        ChromeTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(ChromeTransform.Y, offset.Y, CloseAnimationDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
    }

    private void Window_OnKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        CloseRequested?.Invoke(this, new PanelCloseRequestEventArgs(PanelCloseReason.EscapeKey));
    }

    private void TitleBarBorder_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_canDragWindow || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        try
        {
            DragMove();
            e.Handled = true;
        }
        catch
        {
        }
    }

    private void ItemList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _suppressMouseUpAction = false;
        _mouseDownPoint = e.GetPosition(ItemList);
        _mouseDownItem = ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is WpfListViewItem container
            ? container.DataContext as FolderPanelItem
            : null;
    }

    private void ItemList_OnPreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _mouseDownPoint is null ||
            _mouseDownItem is null ||
            _mouseDownItem.IsFolder ||
            IsSpacePressed())
        {
            return;
        }

        var currentPoint = e.GetPosition(ItemList);
        var deltaX = currentPoint.X - _mouseDownPoint.Value.X;
        var deltaY = currentPoint.Y - _mouseDownPoint.Value.Y;
        if (Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (ItemList.ItemContainerGenerator.ContainerFromItem(_mouseDownItem) is not WpfListViewItem container)
        {
            ResetPointerInteraction();
            return;
        }

        var dataObject = new System.Windows.DataObject();
        var files = new StringCollection
        {
            _mouseDownItem.FullPath
        };
        dataObject.SetFileDropList(files);

        _suppressMouseUpAction = true;
        DragDrop.DoDragDrop(container, dataObject, System.Windows.DragDropEffects.Copy);
        ResetPointerInteraction();
        e.Handled = true;
    }

    private void ItemList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressMouseUpAction)
        {
            ResetPointerInteraction();
            e.Handled = true;
            return;
        }

        if (ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is not WpfListViewItem container ||
            container.DataContext is not FolderPanelItem item)
        {
            ResetPointerInteraction();
            return;
        }

        ItemList.SelectedItem = item;

        if (item.IsFolder)
        {
            if (!IsClickRelease(e, item))
            {
                ResetPointerInteraction();
                return;
            }

            e.Handled = true;
            FolderRequested?.Invoke(this, CreatePanelFolderHit(container, item));
            ResetPointerInteraction();
            return;
        }

        e.Handled = true;
        FileRequested?.Invoke(this, item.FullPath);
        ResetPointerInteraction();
    }

    private void PinToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        PinStateChanged?.Invoke(this, new PanelPinnedChangedEventArgs(_currentPinMode.GetNextPinMode()));
    }

    private void NoticeCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        HideNotice();
    }

    private PanelFolderHit CreatePanelFolderHit(WpfListViewItem container, FolderPanelItem item)
    {
        var topLeft = container.PointToScreen(new WpfPoint(0, 0));
        var bottomRight = container.PointToScreen(new WpfPoint(container.ActualWidth, container.ActualHeight));
        return new PanelFolderHit(item, new Rect(topLeft, bottomRight), Level, PanelId);
    }

    private bool IsClickRelease(MouseButtonEventArgs e, FolderPanelItem item)
    {
        if (!ReferenceEquals(_mouseDownItem, item) || _mouseDownPoint is null)
        {
            return false;
        }

        var releasePoint = e.GetPosition(ItemList);
        var deltaX = releasePoint.X - _mouseDownPoint.Value.X;
        var deltaY = releasePoint.Y - _mouseDownPoint.Value.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY)) <= SystemParameters.MinimumHorizontalDragDistance;
    }

    private void ResetPointerInteraction()
    {
        _mouseDownPoint = null;
        _mouseDownItem = null;
        _suppressMouseUpAction = false;
    }

    private void EnsureControlsInitialized()
    {
        if (FolderNameText is null ||
            FolderPathText is null ||
            ItemCountText is null ||
            ItemList is null ||
            StatusPanel is null ||
            StatusTitleText is null ||
            StatusDetailText is null ||
            LoadingBar is null ||
            ChromeTransform is null ||
            PinToggleButton is null ||
            PinGlyphText is null ||
            TitleBarBorder is null ||
            NoticeBanner is null ||
            NoticeTitleText is null ||
            NoticeDetailText is null)
        {
            throw new InvalidOperationException("FolderPanelWindow 控件初始化不完整。");
        }
    }

    private void ShowStatus(string title, string detail, bool isLoading)
    {
        StatusTitleText.Text = title;
        StatusDetailText.Text = detail;
        LoadingBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
        ItemList.Visibility = Visibility.Collapsed;
    }

    private void PrepareStatusLayout()
    {
        MinHeight = StatusMinHeight;
        ItemList.ClearValue(FrameworkElement.HeightProperty);
        ItemList.ClearValue(FrameworkElement.MaxHeightProperty);
        ItemList.SetCurrentValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        ItemList.SetCurrentValue(ScrollViewer.CanContentScrollProperty, false);
    }

    private void PrepareItemListLayout(int itemCount)
    {
        MinHeight = ContentMinHeight;
        ItemList.ClearValue(FrameworkElement.HeightProperty);

        var visibleItemLimit = Math.Clamp(
            VisibleItemLimit,
            AppSettingsService.MinPanelVisibleItemCount,
            AppSettingsService.MaxPanelVisibleItemCount);
        ItemList.MaxHeight = visibleItemLimit * PanelListRowHeight;
        ItemList.SetCurrentValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        ItemList.SetCurrentValue(ScrollViewer.CanContentScrollProperty, true);

        UpdateLayout();
        FindListScrollViewer()?.ScrollToTop();
        UpdateLayout();
    }

    private void HideNotice()
    {
        if (NoticeBanner is null)
        {
            return;
        }

        _noticeTimer?.Stop();
        NoticeBanner.Visibility = Visibility.Collapsed;
    }

    private void NoticeTimer_OnTick(object? sender, EventArgs e)
    {
        HideNotice();
    }

    private ScrollViewer? FindListScrollViewer()
    {
        return FindDescendant<ScrollViewer>(ItemList);
    }

    private static T? FindDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        if (root is T target)
        {
            return target;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var match = FindDescendant<T>(VisualTreeHelper.GetChild(root, index));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? node)
        where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private string BuildPinTooltip(PanelPinMode pinMode)
    {
        return pinMode switch
        {
            PanelPinMode.None => "切换到“只钉在桌面”",
            PanelPinMode.PinnedToDesktop => "切换到“全局置顶”",
            PanelPinMode.PinnedTopmost => "恢复默认自动关闭",
            _ => "钉住当前展开栏"
        };
    }

    private WpfBrush ResolvePinBackground(PanelPinMode pinMode)
    {
        return pinMode switch
        {
            PanelPinMode.PinnedToDesktop => GetBrush("PanelSelectedBrush"),
            PanelPinMode.PinnedTopmost => GetBrush("PanelExpandedBrush"),
            _ => System.Windows.Media.Brushes.Transparent
        };
    }

    private WpfBrush ResolvePinBorderBrush(PanelPinMode pinMode)
    {
        return pinMode switch
        {
            PanelPinMode.PinnedToDesktop => GetBrush("AccentBorderHoverBrush"),
            PanelPinMode.PinnedTopmost => GetBrush("PanelExpandedBorderBrush"),
            _ => System.Windows.Media.Brushes.Transparent
        };
    }

    private WpfBrush ResolvePinForeground(PanelPinMode pinMode)
    {
        return pinMode switch
        {
            PanelPinMode.PinnedToDesktop => GetBrush("TextPrimaryBrush"),
            PanelPinMode.PinnedTopmost => GetBrush("SegmentSelectedTextBrush"),
            _ => GetBrush("PanelMutedBrush")
        };
    }

    private static double ResolvePinRotation(PanelPinMode pinMode)
    {
        return pinMode switch
        {
            PanelPinMode.PinnedToDesktop => -45,
            PanelPinMode.PinnedTopmost => -90,
            _ => 0
        };
    }

    private WpfBrush GetBrush(string resourceKey)
    {
        return (WpfBrush)FindResource(resourceKey);
    }

    private static bool IsSpacePressed()
    {
        return (GetAsyncKeyState(VkSpace) & 0x8000) != 0;
    }

    private static WpfPoint ResolveOffset(GestureDirection direction, bool isClosing)
    {
        var distance = isClosing ? AnimationOffset * 0.7 : AnimationOffset;

        return direction switch
        {
            GestureDirection.Right => new WpfPoint(isClosing ? distance : -distance, 0),
            GestureDirection.Left => new WpfPoint(isClosing ? -distance : distance, 0),
            GestureDirection.Down => new WpfPoint(0, isClosing ? distance : -distance),
            GestureDirection.Up => new WpfPoint(0, isClosing ? -distance : distance),
            _ => new WpfPoint(0, 0)
        };
    }

    private const int VkSpace = 0x20;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
