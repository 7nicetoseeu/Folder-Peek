using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace FolderPeek.App;

public sealed class DesktopItemResolver
{
    private readonly string[] _desktopRoots;
    private IReadOnlyList<DesktopFolderHit>? _cachedSnapshot;

    public DesktopItemResolver()
    {
        _desktopRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    public IReadOnlyList<string> DesktopRoots => _desktopRoots;

    public void PrimeSnapshot()
    {
        _cachedSnapshot = CaptureDesktopFolderHits();
    }

    public void InvalidateSnapshot()
    {
        _cachedSnapshot = null;
    }

    public DesktopFolderHit? TryResolveFolderFromScreenPoint(int x, int y)
    {
        return ResolveHitFromSnapshot(GetOrCreateSnapshot(), x, y);
    }

    internal DesktopFolderHit? TryResolveFolderFromSnapshotPoint(int x, int y)
    {
        return ResolveHitFromSnapshot(GetOrCreateSnapshot(), x, y);
    }

    public string DescribeScreenPoint(int x, int y)
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
        {
            return $"坐标=({x},{y})；未找到桌面列表视图。";
        }

        var items = GetOrCreateSnapshot();
        if (items.Count == 0)
        {
            return $"坐标=({x},{y})；当前桌面下未命中可展开文件夹。";
        }

        var hit = ResolveHitFromSnapshot(items, x, y);
        if (hit is null)
        {
            return $"坐标=({x},{y})；桌面文件夹数={items.Count}，当前点位未命中。";
        }

        return $"坐标=({x},{y})；命中={hit.DisplayName}@({hit.Bounds.Left:F0},{hit.Bounds.Top:F0},{hit.Bounds.Right:F0},{hit.Bounds.Bottom:F0})";
    }

    internal static DesktopFolderHit? ResolveHitFromSnapshot(IReadOnlyList<DesktopFolderHit> items, int x, int y)
    {
        return items
            .FirstOrDefault(item => item.Bounds.Contains(x, y));
    }

    private IReadOnlyList<DesktopFolderHit> GetOrCreateSnapshot()
    {
        return _cachedSnapshot ??= CaptureDesktopFolderHits();
    }

    private IReadOnlyList<DesktopFolderHit> CaptureDesktopFolderHits()
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
        {
            return Array.Empty<DesktopFolderHit>();
        }

        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle(listView);
        }
        catch
        {
            return Array.Empty<DesktopFolderHit>();
        }

        if (root is null)
        {
            return Array.Empty<DesktopFolderHit>();
        }

        var items = new List<DesktopFolderHit>();

        try
        {
            var listItems = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

            foreach (AutomationElement listItem in listItems)
            {
                var name = listItem.Current.Name?.Trim();
                var bounds = listItem.Current.BoundingRectangle;
                if (string.IsNullOrWhiteSpace(name) || bounds.IsEmpty)
                {
                    continue;
                }

                foreach (var rootPath in _desktopRoots)
                {
                    var fullPath = Path.Combine(rootPath, name);
                    if (!Directory.Exists(fullPath))
                    {
                        continue;
                    }

                    items.Add(new DesktopFolderHit(name, fullPath, "desktop-point-hit", bounds));
                    break;
                }
            }
        }
        catch
        {
            return Array.Empty<DesktopFolderHit>();
        }

        return items
            .GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static IntPtr FindDesktopListView()
    {
        var progman = FindWindow("Progman", "Program Manager");
        var listView = FindDesktopListViewFromTopLevel(progman);
        if (listView != IntPtr.Zero)
        {
            return listView;
        }

        var result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            result = FindDesktopListViewFromTopLevel(window);
            return result == IntPtr.Zero;
        }, IntPtr.Zero);

        return result;
    }

    private static IntPtr FindDesktopListViewFromTopLevel(IntPtr topLevel)
    {
        if (topLevel == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var shellView = FindWindowEx(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null);
        return shellView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(shellView, IntPtr.Zero, "SysListView32", "FolderView");
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
}
