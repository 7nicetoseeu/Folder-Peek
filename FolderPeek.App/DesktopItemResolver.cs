using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace FolderPeek.App;

public sealed class DesktopItemResolver
{
    private readonly string[] _desktopRoots;

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

    public DesktopFolderHit? TryResolveFolderFromScreenPoint(int x, int y)
    {
        foreach (var selectedItem in GetSelectedDesktopItems())
        {
            if (!selectedItem.Bounds.Contains(x, y))
            {
                continue;
            }

            foreach (var root in _desktopRoots)
            {
                var fullPath = Path.Combine(root, selectedItem.Name);
                if (Directory.Exists(fullPath))
                {
                    return new DesktopFolderHit(selectedItem.Name, fullPath, "desktop-selection-hit", selectedItem.Bounds);
                }
            }
        }

        return null;
    }

    public string DescribeScreenPoint(int x, int y)
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
        {
            return $"坐标=({x},{y})；未找到桌面列表视图。";
        }

        var selectedItems = GetSelectedDesktopItems();
        if (selectedItems.Count == 0)
        {
            return $"坐标=({x},{y})；当前选中=<none>";
        }

        var descriptions = selectedItems
            .Select(item => $"{item.Name}@({item.Bounds.Left:F0},{item.Bounds.Top:F0},{item.Bounds.Right:F0},{item.Bounds.Bottom:F0})");

        return $"坐标=({x},{y})；当前选中={string.Join(", ", descriptions)}";
    }

    private IReadOnlyList<SelectedDesktopItem> GetSelectedDesktopItems()
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
        {
            return Array.Empty<SelectedDesktopItem>();
        }

        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle(listView);
        }
        catch
        {
            return Array.Empty<SelectedDesktopItem>();
        }

        if (root is null)
        {
            return Array.Empty<SelectedDesktopItem>();
        }

        var items = new List<SelectedDesktopItem>();

        try
        {
            var listItems = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

            foreach (AutomationElement listItem in listItems)
            {
                if (!IsSelected(listItem))
                {
                    continue;
                }

                var name = listItem.Current.Name?.Trim();
                var bounds = listItem.Current.BoundingRectangle;
                if (!string.IsNullOrWhiteSpace(name) && !bounds.IsEmpty)
                {
                    items.Add(new SelectedDesktopItem(name, bounds));
                }
            }
        }
        catch
        {
            return Array.Empty<SelectedDesktopItem>();
        }

        return items
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private sealed record SelectedDesktopItem(string Name, Rect Bounds);

    private static bool IsSelected(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var patternObject) &&
                patternObject is SelectionItemPattern pattern)
            {
                return pattern.Current.IsSelected;
            }

            var value = element.GetCurrentPropertyValue(SelectionItemPattern.IsSelectedProperty, true);
            return value is bool isSelected && isSelected;
        }
        catch
        {
            return false;
        }
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
