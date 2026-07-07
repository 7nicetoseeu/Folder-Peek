using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

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
    private System.Windows.Point? _mouseDownPoint;
    private FolderPanelItem? _mouseDownItem;

    public FolderPanelWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => ItemList?.Focus();
    }

    public int Level { get; set; }

    public int VisibleItemLimit { get; set; } = AppSettingsService.DefaultPanelVisibleItemCount;

    public event EventHandler<PanelCloseRequestEventArgs>? CloseRequested;

    public event EventHandler<string>? FileRequested;

    public event EventHandler<PanelFolderHit>? FolderRequested;

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

            var point = PointFromScreen(new System.Windows.Point(screenX, screenY));
            if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight)
            {
                return false;
            }

            if (InputHitTest(point) is not DependencyObject target ||
                ItemsControl.ContainerFromElement(ItemList, target) is not System.Windows.Controls.ListViewItem container ||
                container.DataContext is not FolderPanelItem item ||
                !item.IsFolder)
            {
                return false;
            }

            var topLeft = container.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = container.PointToScreen(new System.Windows.Point(container.ActualWidth, container.ActualHeight));
            hit = new PanelFolderHit(item, new Rect(topLeft, bottomRight), Level);
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

    private void Window_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        CloseRequested?.Invoke(this, new PanelCloseRequestEventArgs(PanelCloseReason.EscapeKey));
    }

    private void ItemList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownPoint = e.GetPosition(ItemList);
        _mouseDownItem = ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is System.Windows.Controls.ListViewItem container
            ? container.DataContext as FolderPanelItem
            : null;
    }

    private void ItemList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is not System.Windows.Controls.ListViewItem container ||
            container.DataContext is not FolderPanelItem item)
        {
            return;
        }

        ItemList.SelectedItem = item;

        if (item.IsFolder)
        {
            if (!IsClickRelease(e, item))
            {
                return;
            }

            e.Handled = true;
            FolderRequested?.Invoke(this, CreatePanelFolderHit(container, item));
            return;
        }

        e.Handled = true;
        FileRequested?.Invoke(this, item.FullPath);
    }

    private void NoticeCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        HideNotice();
    }

    private PanelFolderHit CreatePanelFolderHit(System.Windows.Controls.ListViewItem container, FolderPanelItem item)
    {
        var topLeft = container.PointToScreen(new System.Windows.Point(0, 0));
        var bottomRight = container.PointToScreen(new System.Windows.Point(container.ActualWidth, container.ActualHeight));
        return new PanelFolderHit(item, new Rect(topLeft, bottomRight), Level);
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

    private static System.Windows.Point ResolveOffset(GestureDirection direction, bool isClosing)
    {
        var distance = isClosing ? AnimationOffset * 0.7 : AnimationOffset;

        return direction switch
        {
            GestureDirection.Right => new System.Windows.Point(isClosing ? distance : -distance, 0),
            GestureDirection.Left => new System.Windows.Point(isClosing ? -distance : distance, 0),
            GestureDirection.Down => new System.Windows.Point(0, isClosing ? distance : -distance),
            GestureDirection.Up => new System.Windows.Point(0, isClosing ? -distance : distance),
            _ => new System.Windows.Point(0, 0)
        };
    }

}
