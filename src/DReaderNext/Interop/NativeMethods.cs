using System.Runtime.InteropServices;

namespace DReaderNext.Interop;

/// <summary>全局热键所需的 Win32 调用。</summary>
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
}
