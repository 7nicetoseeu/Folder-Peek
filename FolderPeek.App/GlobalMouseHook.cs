using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FolderPeek.App;

public sealed class GlobalMouseHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;

    private readonly HookProc _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _disposed;

    public GlobalMouseHook()
    {
        _hookProc = HookCallback;
    }

    public event EventHandler<GlobalMouseEventArgs>? MouseAction;

    public Func<GlobalMouseEventArgs, bool>? SuppressPredicate { get; set; }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module is null ? IntPtr.Zero : GetModuleHandle(module.ModuleName);
        _hookHandle = SetWindowsHookEx(WhMouseLl, _hookProc, moduleHandle, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法安装全局鼠标钩子。");
        }
    }

    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var action = TranslateAction((int)wParam);
            if (action is not null)
            {
                var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                var eventArgs = new GlobalMouseEventArgs(action.Value, data.Point.X, data.Point.Y);
                var shouldSuppress = SuppressPredicate?.Invoke(eventArgs) == true;
                MouseAction?.Invoke(this, eventArgs);
                if (shouldSuppress)
                {
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static MouseActionType? TranslateAction(int message)
    {
        return message switch
        {
            WmMouseMove => MouseActionType.Move,
            WmLButtonDown => MouseActionType.LeftButtonDown,
            WmLButtonUp => MouseActionType.LeftButtonUp,
            _ => null
        };
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public PointStruct Point;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
