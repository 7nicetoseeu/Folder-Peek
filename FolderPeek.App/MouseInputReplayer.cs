using System.Runtime.InteropServices;

namespace FolderPeek.App;

internal static class MouseInputReplayer
{
    public static readonly IntPtr Marker = new(0x465045454B);
    private const uint InputMouse = 0;
    private const uint MouseEventFRightDown = 0x0008;
    private const uint MouseEventFRightUp = 0x0010;

    public static void ReplayRightClick()
    {
        var inputs = new[]
        {
            CreateMouseInput(MouseEventFRightDown),
            CreateMouseInput(MouseEventFRightUp)
        };
        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input CreateMouseInput(uint flags)
    {
        return new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput { DwFlags = flags, DwExtraInfo = Marker }
            }
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, Input[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }
}
