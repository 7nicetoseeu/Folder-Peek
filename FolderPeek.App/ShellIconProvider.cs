using System.IO;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FolderPeek.App;

public sealed class ShellIconProvider
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiSmallIcon = 0x1;
    private const uint ShgfiUseFileAttributes = 0x10;

    private readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? GetFolderIcon()
    {
        return _iconCache.GetOrAdd("folder", _ => TryLoadIcon("folder", FileAttributeDirectory));
    }

    public ImageSource? GetFileIcon(string fullPath)
    {
        var extension = Path.GetExtension(fullPath);
        var cacheKey = string.IsNullOrWhiteSpace(extension) ? "file" : extension;
        var shellPath = string.IsNullOrWhiteSpace(extension) ? "file" : $"sample{extension}";
        return _iconCache.GetOrAdd(cacheKey, _ => TryLoadIcon(shellPath, FileAttributeNormal));
    }

    private static ImageSource? TryLoadIcon(string shellPath, uint attributes)
    {
        try
        {
            return LoadIcon(shellPath, attributes);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadIcon(string shellPath, uint attributes)
    {
        var fileInfo = new ShFileInfo();
        var result = SHGetFileInfo(
            shellPath,
            attributes,
            ref fileInfo,
            (uint)Marshal.SizeOf<ShFileInfo>(),
            ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes);

        if (result == IntPtr.Zero || fileInfo.IconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(
                fileInfo.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(16, 16));
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(fileInfo.IconHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
