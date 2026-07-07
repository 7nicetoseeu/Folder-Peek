using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MediaColor = System.Windows.Media.Color;

namespace FolderPeek.App;

public static class DwmWindowStyler
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private const int DwmwcpRound = 2;

    public static void ApplyMainWindowStyle(Window window, AppThemeService themeService)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetAttribute(handle, DwmwaUseImmersiveDarkMode, themeService.IsEffectiveDarkMode ? 1 : 0);
        SetAttribute(handle, DwmwaWindowCornerPreference, DwmwcpRound);

        var captionColor = themeService.GetResourceColor("PageBackgroundBrush");
        var textColor = themeService.GetResourceColor("TextPrimaryBrush");
        var borderColor = themeService.GetResourceColor("BorderBrushSoft");

        SetColorAttribute(handle, DwmwaCaptionColor, captionColor);
        SetColorAttribute(handle, DwmwaTextColor, textColor);
        SetColorAttribute(handle, DwmwaBorderColor, borderColor);
    }

    private static void SetAttribute(IntPtr handle, int attribute, int value)
    {
        _ = DwmSetWindowAttribute(handle, attribute, ref value, Marshal.SizeOf<int>());
    }

    private static void SetColorAttribute(IntPtr handle, int attribute, MediaColor color)
    {
        var value = ToColorRef(color);
        _ = DwmSetWindowAttribute(handle, attribute, ref value, Marshal.SizeOf<int>());
    }

    private static int ToColorRef(MediaColor color)
    {
        return color.R | (color.G << 8) | (color.B << 16);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
