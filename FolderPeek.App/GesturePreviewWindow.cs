using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace FolderPeek.App;

public sealed class GesturePreviewWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const double PreviewMarginPx = 10;
    private const double ProgressThickness = 6;
    private const double FadeDurationMs = 90;

    private readonly Border _progressBorder;
    private readonly Grid _root;

    public GesturePreviewWindow()
    {
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        IsHitTestVisible = false;
        Focusable = false;

        _root = new Grid
        {
            Background = System.Windows.Media.Brushes.Transparent
        };

        _progressBorder = new Border
        {
            CornerRadius = new CornerRadius(999),
            SnapsToDevicePixels = true
        };
        _root.Children.Add(_progressBorder);
        Content = _root;

        Loaded += (_, _) => ApplyWindowStyles();
        SourceInitialized += (_, _) => ApplyWindowStyles();
    }

    public void UpdatePreview(DesktopFolderHit hit, GestureDirection direction, double progress)
    {
        return;
    }

    public void Dismiss(bool immediate)
    {
        if (!IsVisible)
        {
            return;
        }

        BeginAnimation(OpacityProperty, null);
        if (immediate)
        {
            Hide();
            Opacity = 1;
            return;
        }

        var animation = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(FadeDurationMs));
        animation.Completed += (_, _) =>
        {
            Hide();
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, animation);
    }

    private void UpdateProgressIndicator(
        GestureDirection direction,
        double progress,
        double widthDip,
        double heightDip,
        double marginDip)
    {
        var normalizedProgress = Math.Clamp(progress, 0, 1);
        var displayedProgress = 0.18 + (normalizedProgress * 0.82);
        var innerWidth = Math.Max(20, widthDip - 12);
        var innerHeight = Math.Max(20, heightDip - 12);

        _progressBorder.Margin = new Thickness(marginDip + 6);
        switch (direction)
        {
            case GestureDirection.Left:
                _progressBorder.Width = Math.Max(16, innerWidth * displayedProgress);
                _progressBorder.Height = ProgressThickness;
                _progressBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                _progressBorder.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                break;
            case GestureDirection.Up:
                _progressBorder.Width = ProgressThickness;
                _progressBorder.Height = Math.Max(16, innerHeight * displayedProgress);
                _progressBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                _progressBorder.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                break;
            case GestureDirection.Down:
                _progressBorder.Width = ProgressThickness;
                _progressBorder.Height = Math.Max(16, innerHeight * displayedProgress);
                _progressBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                _progressBorder.VerticalAlignment = System.Windows.VerticalAlignment.Top;
                break;
            default:
                _progressBorder.Width = Math.Max(16, innerWidth * displayedProgress);
                _progressBorder.Height = ProgressThickness;
                _progressBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                _progressBorder.VerticalAlignment = System.Windows.VerticalAlignment.Top;
                break;
        }
    }

    private void ApplyThemeBrushes()
    {
        _progressBorder.Background = GetBrush("AccentBrush", MediaColor.FromRgb(0x25, 0x63, 0xEB));
    }

    private MediaBrush GetBrush(string key, MediaColor fallbackColor)
    {
        return TryFindResource(key) as MediaBrush ?? new SolidColorBrush(fallbackColor);
    }

    private void ApplyWindowStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var styles = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        styles |= WsExToolWindow | WsExNoActivate;
        _ = SetWindowLongPtr(handle, GwlExStyle, new IntPtr(styles));
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static class ScreenScaleHelper
    {
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

        [DllImport("Shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        public static double GetScaleForScreenPoint(double x, double y)
        {
            var monitor = MonitorFromPoint(new NativePoint((int)x, (int)y), 2);
            if (monitor == IntPtr.Zero)
            {
                return 1.0;
            }

            return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96d : 1.0;
        }

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
    }
}
