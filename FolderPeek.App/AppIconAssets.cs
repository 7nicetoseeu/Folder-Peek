using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

namespace FolderPeek.App;

public static class AppIconAssets
{
    private static readonly string IconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "folder-peek-icon.ico");
    private static readonly Dictionary<Window, NativeWindowIcons> NativeIconsByWindow = new();
    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int SmCxIcon = 11;
    private const int SmCyIcon = 12;
    private const int SmCxSmallIcon = 49;
    private const int SmCySmallIcon = 50;

    public static void ApplyWindowIcon(Window window)
    {
        try
        {
            if (!File.Exists(IconPath))
            {
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(IconPath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            window.Icon = bitmap;
            window.SourceInitialized += OnWindowSourceInitialized;
            window.Closed += OnWindowClosed;
        }
        catch
        {
            // Ignore icon loading failures to avoid affecting window startup.
        }
    }

    public static (Icon Icon, bool OwnsIcon) LoadTrayIcon()
    {
        try
        {
            if (File.Exists(IconPath))
            {
                return (new Icon(IconPath), true);
            }
        }
        catch
        {
            // Fall back to the default icon if the custom icon cannot be loaded.
        }

        return (SystemIcons.Application, false);
    }

    private static void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        try
        {
            ApplyNativeWindowIcons(window);
        }
        catch
        {
            // Ignore native icon failures and keep the managed icon path.
        }
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.SourceInitialized -= OnWindowSourceInitialized;
        window.Closed -= OnWindowClosed;
        ReleaseNativeWindowIcons(window);
    }

    private static void ApplyNativeWindowIcons(Window window)
    {
        if (!File.Exists(IconPath))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ReleaseNativeWindowIcons(window);

        var smallSize = new System.Drawing.Size(
            Math.Max(16, GetSystemMetrics(SmCxSmallIcon)),
            Math.Max(16, GetSystemMetrics(SmCySmallIcon)));
        var bigSize = new System.Drawing.Size(
            Math.Max(32, GetSystemMetrics(SmCxIcon)),
            Math.Max(32, GetSystemMetrics(SmCyIcon)));

        var smallIcon = new Icon(IconPath, smallSize);
        var bigIcon = new Icon(IconPath, bigSize);

        _ = SendMessage(handle, WmSetIcon, new IntPtr(IconSmall), smallIcon.Handle);
        _ = SendMessage(handle, WmSetIcon, new IntPtr(IconBig), bigIcon.Handle);

        NativeIconsByWindow[window] = new NativeWindowIcons(smallIcon, bigIcon);
    }

    private static void ReleaseNativeWindowIcons(Window window)
    {
        if (!NativeIconsByWindow.Remove(window, out var icons))
        {
            return;
        }

        icons.Dispose();
    }

    private sealed class NativeWindowIcons : IDisposable
    {
        private readonly Icon _smallIcon;
        private readonly Icon _bigIcon;

        public NativeWindowIcons(Icon smallIcon, Icon bigIcon)
        {
            _smallIcon = smallIcon;
            _bigIcon = bigIcon;
        }

        public void Dispose()
        {
            _smallIcon.Dispose();
            _bigIcon.Dispose();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
