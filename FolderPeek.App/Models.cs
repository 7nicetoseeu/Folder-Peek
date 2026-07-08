using System.Windows;
using System.Windows.Media;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FolderPeek.App;

public enum MouseActionType
{
    Move,
    LeftButtonDown,
    LeftButtonUp
}

public enum KeyActionType
{
    Down,
    Up
}

public enum GestureDirection
{
    Right,
    Left,
    Down,
    Up
}

public enum AppThemeMode
{
    FollowSystem,
    Light,
    Dark
}

public enum AppThemeStyle
{
    Original
}

public enum PanelCloseReason
{
    Unknown,
    LostFocus,
    EscapeKey,
    FileOpened,
    TrayMenu,
    ListeningPaused,
    NewPanelTriggered,
    AppExit,
    Dispose
}

public enum PanelPinMode
{
    None,
    PinnedToDesktop,
    PinnedTopmost
}

public sealed record GlobalMouseEventArgs(MouseActionType ActionType, int X, int Y);

public sealed record GlobalKeyEventArgs(int VirtualKey, KeyActionType ActionType);

public sealed class PanelCloseRequestEventArgs : EventArgs
{
    public PanelCloseRequestEventArgs(PanelCloseReason reason)
    {
        Reason = reason;
    }

    public PanelCloseReason Reason { get; }
}

public sealed class PanelPinnedChangedEventArgs : EventArgs
{
    public PanelPinnedChangedEventArgs(PanelPinMode pinMode)
    {
        PinMode = pinMode;
    }

    public PanelPinMode PinMode { get; }
}

public sealed record DesktopFolderHit(string DisplayName, string FullPath, string Source, Rect Bounds);

public sealed record PanelFolderHit(FolderPanelItem Item, Rect Bounds, int Level, Guid PanelId);

internal static class PanelPinModeExtensions
{
    public static PanelPinMode GetNextPinMode(this PanelPinMode pinMode)
    {
        return pinMode switch
        {
            PanelPinMode.None => PanelPinMode.PinnedToDesktop,
            PanelPinMode.PinnedToDesktop => PanelPinMode.PinnedTopmost,
            _ => PanelPinMode.None
        };
    }

    public static bool IsPinned(this PanelPinMode pinMode)
    {
        return pinMode != PanelPinMode.None;
    }
}

public sealed class FolderPanelItem : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isDragPreviewActive;
    private double _dragProgress;
    private string _previewIndicator = string.Empty;

    public FolderPanelItem(
        string displayName,
        string fullPath,
        bool isFolder,
        ImageSource? icon,
        string secondaryText)
    {
        DisplayName = displayName;
        FullPath = fullPath;
        IsFolder = isFolder;
        Icon = icon;
        SecondaryText = secondaryText;
    }

    public string DisplayName { get; }

    public string FullPath { get; }

    public bool IsFolder { get; }

    public ImageSource? Icon { get; }

    public string SecondaryText { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public bool IsDragPreviewActive
    {
        get => _isDragPreviewActive;
        set
        {
            if (_isDragPreviewActive == value)
            {
                return;
            }

            _isDragPreviewActive = value;
            OnPropertyChanged();
        }
    }

    public double DragProgress
    {
        get => _dragProgress;
        set
        {
            var normalized = Math.Clamp(value, 0, 1);
            if (Math.Abs(_dragProgress - normalized) < 0.001)
            {
                return;
            }

            _dragProgress = normalized;
            OnPropertyChanged();
        }
    }

    public string PreviewIndicator
    {
        get => _previewIndicator;
        set
        {
            if (string.Equals(_previewIndicator, value, StringComparison.Ordinal))
            {
                return;
            }

            _previewIndicator = value;
            OnPropertyChanged();
        }
    }

    public void SetDragPreview(GestureDirection direction, double progress)
    {
        if (!IsFolder)
        {
            return;
        }

        IsDragPreviewActive = progress > 0;
        DragProgress = progress;
        PreviewIndicator = progress > 0 ? DirectionToIndicator(direction) : string.Empty;
    }

    public void ClearDragPreview()
    {
        IsDragPreviewActive = false;
        DragProgress = 0;
        PreviewIndicator = string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string DirectionToIndicator(GestureDirection direction)
    {
        return direction switch
        {
            GestureDirection.Right => ">",
            GestureDirection.Left => "<",
            GestureDirection.Down => "v",
            GestureDirection.Up => "^",
            _ => string.Empty
        };
    }
}
