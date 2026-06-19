using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Engram;

/// <summary>
/// Registers a system-wide hotkey (default Ctrl+Alt+Space) to summon the
/// capture bar. Uses Win32 RegisterHotKey routed through the window's message
/// loop via an HwndSource hook.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint VK_SPACE = 0x20;

    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4567;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Action _callback;

    public GlobalHotkey(Window window, uint modifiers, uint vk, Action callback)
    {
        _callback = callback;
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(Hook);
        RegisterHotKey(_hwnd, HotkeyId, modifiers, vk);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _callback();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_hwnd, HotkeyId);
        _source.RemoveHook(Hook);
    }
}
