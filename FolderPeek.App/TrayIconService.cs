using System.Drawing;
using Forms = System.Windows.Forms;

namespace FolderPeek.App;

public sealed class TrayIconService : IDisposable
{
    private readonly AppSettingsService _settingsService;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private readonly Icon _trayIcon;
    private readonly bool _ownsTrayIcon;
    private bool _disposed;

    public TrayIconService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        (_trayIcon, _ownsTrayIcon) = AppIconAssets.LoadTrayIcon();

        _toggleItem = new Forms.ToolStripMenuItem("暂停监听");
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty);

        var showItem = new Forms.ToolStripMenuItem("打开状态窗口");
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        var closePanelsItem = new Forms.ToolStripMenuItem("关闭全部面板");
        closePanelsItem.Click += (_, _) => ClosePanelsRequested?.Invoke(this, EventArgs.Empty);

        var aboutItem = new Forms.ToolStripMenuItem("关于");
        aboutItem.Click += (_, _) => AboutRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(showItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(closePanelsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(aboutItem);
        menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Folder Peek",
            Visible = true,
            Icon = _trayIcon,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? ToggleRequested;

    public event EventHandler? ClosePanelsRequested;

    public event EventHandler? AboutRequested;

    public event EventHandler? ExitRequested;

    public void SetEnabledState(bool isEnabled)
    {
        _toggleItem.Text = isEnabled ? "暂停监听" : "恢复监听";
        if (!_settingsService.ShowTrayTips)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = "Folder Peek";
        _notifyIcon.BalloonTipText = isEnabled ? "鼠标监听已恢复。" : "鼠标监听已暂停。";
        _notifyIcon.ShowBalloonTip(1200);
    }

    public void ShowStartupTip()
    {
        if (!_settingsService.ShowTrayTips)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = "Folder Peek 已启动";
        _notifyIcon.BalloonTipText = "程序已驻留托盘。可从状态窗口选择文件夹展开方式。";
        _notifyIcon.ShowBalloonTip(1800);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        if (_ownsTrayIcon)
        {
            _trayIcon.Dispose();
        }

        _disposed = true;
    }
}
