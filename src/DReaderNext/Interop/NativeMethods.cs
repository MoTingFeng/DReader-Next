using System.Runtime.InteropServices;

namespace DReaderNext.Interop;

/// <summary>全局热键与低层鼠标钩子所需的 Win32 调用。</summary>
internal static class NativeMethods
{
    public const int WM_HOTKEY = 0x0312;

    /// <summary>RegisterHotKey 的修饰键位（对应 Win32 的 MOD_* 常量）。</summary>
    [Flags]
    public enum HotkeyModifiers : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000,
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------------------------------------------------------------- 低层鼠标钩子
    //
    // 「鼠标穿透」关闭时用它接管窗口矩形内的鼠标：透明窗口的抠色像素在 Windows 眼里
    // 是「没有窗口」的，鼠标会直接落到后面的窗口上（见 MainForm.UpdateMouseHook 的说明）。

    public const int WH_MOUSE_LL = 14;

    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MOUSEWHEEL = 0x020A;

    [StructLayout(LayoutKind.Sequential)]
    public struct WinPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseHookData
    {
        public WinPoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>取最近一次 Win32 调用的错误码（SetWindowsHookEx 失败时用来写日志）。</summary>
    public static int LastError() => Marshal.GetLastWin32Error();

    /// <summary>把 lParam 解成鼠标钩子结构体（把 InteropServices 的细节关在这个文件里）。</summary>
    public static MouseHookData ReadMouseHookData(IntPtr lParam)
        => Marshal.PtrToStructure<MouseHookData>(lParam);

    /// <summary>滚轮增量：mouseData 的高 16 位（有符号）。</summary>
    public static int WheelDelta(MouseHookData data) => (short)((data.MouseData >> 16) & 0xFFFF);
}
