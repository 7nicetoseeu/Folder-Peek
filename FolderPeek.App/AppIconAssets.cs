using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FolderPeek.App;

public static class AppIconAssets
{
    private static readonly string IconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "folder-peek-icon.ico");

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
}
