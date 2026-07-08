using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FolderPeek.App;

public sealed class GlobalKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly HookProc _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _disposed;

    public GlobalKeyboardHook()
    {
        _hookProc = HookCallback;
    }

    public event EventHandler<GlobalKeyEventArgs>? KeyAction;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module is null ? IntPtr.Zero : GetModuleHandle(module.ModuleName);
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, moduleHandle, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法安装全局键盘钩子。");
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
            var message = wParam.ToInt32();
            var actionType = TranslateActionType(message);
            if (actionType is not null)
            {
                var data = Marshal.PtrToStructure<KbdllHookStruct>(lParam);
                KeyAction?.Invoke(this, new GlobalKeyEventArgs(data.VirtualKeyCode, actionType.Value));
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static KeyActionType? TranslateActionType(int message)
    {
        return message switch
        {
            WmKeyDown or WmSysKeyDown => KeyActionType.Down,
            WmKeyUp or WmSysKeyUp => KeyActionType.Up,
            _ => null
        };
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdllHookStruct
    {
        public int VirtualKeyCode;
        public int ScanCode;
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
