using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Riji.Core;

namespace Riji.Windows;

// Keep callbacks alive for the lifetime of the native hooks; never retain typed content.
public sealed class WindowsObserver : IDisposable
{
    private readonly Native.HookProc keyboardCallback;
    private readonly Native.HookProc mouseCallback;
    private readonly Native.WinEventProc foregroundCallback;
    private readonly nint keyboardHook;
    private readonly nint mouseHook;
    private readonly nint foregroundHook;
    private double lastInput;
    private readonly InputActivity inputActivity;
    private nint lastWindow;
    private AppIdentity? lastApp;
    private double lastLookup = -10;
    public bool Suspended { get; set; }
    public bool SessionLocked { get; set; }
    public bool HooksAvailable => keyboardHook != 0 && mouseHook != 0 && foregroundHook != 0;
    public event Action? ForegroundChanged;
    public static double Monotonic => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public WindowsObserver(InputThresholds? thresholds = null)
    {
        inputActivity = new(thresholds ?? new());
        var input = new Native.LastInput { Size = (uint)Marshal.SizeOf<Native.LastInput>() };
        lastInput = Monotonic;
        if (Native.GetLastInputInfo(ref input))
            lastInput -= InputActivity.IdleSeconds(unchecked((uint)Environment.TickCount64), input.Time);
        keyboardCallback = OnKeyboard;
        mouseCallback = OnMouse;
        foregroundCallback = (_, _, _, _, _, _, _) => ForegroundChanged?.Invoke();
        var module = Native.GetModuleHandle(null);
        keyboardHook = Native.SetWindowsHookEx(13, keyboardCallback, module, 0);
        mouseHook = Native.SetWindowsHookEx(14, mouseCallback, module, 0);
        foregroundHook = Native.SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 0);
    }

    public Observation Capture()
    {
        var now = Monotonic;
        var blocked = Suspended || SessionLocked || !HooksAvailable || !IsInteractiveDesktop();
        var window = blocked ? 0 : Native.GetForegroundWindow();
        if (window != lastWindow || now - lastLookup >= 1)
        {
            lastWindow = window;
            lastLookup = now;
            lastApp = Resolve(window);
        }
        Native.GetWindowThreadProcessId(window, out var pid);
        var desktop = !blocked && window == Native.GetShellWindow();
        return new(DateTimeOffset.UtcNow, now, lastApp, lastInput, blocked, (int)pid, $"{pid}:{window}", null, desktop);
    }

    // Secure desktops and inaccessible desktop state are conservatively paused.
    private static bool IsInteractiveDesktop()
    {
        var desktop = Native.OpenInputDesktop(0, false, 1);
        if (desktop == 0) return false;
        try
        {
            var name = new StringBuilder(128);
            return Native.GetUserObjectInformation(desktop, 2, name, 256, out _) && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { Native.CloseDesktop(desktop); }
    }

    // Resolve only process identity; window titles are not collected.
    private static AppIdentity? Resolve(nint window)
    {
        if (window == 0) return null;
        Native.GetWindowThreadProcessId(window, out var pid);
        var process = Native.OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        try
        {
            var path = new StringBuilder(32768);
            var size = path.Capacity;
            if (!Native.QueryFullProcessImageName(process, 0, path, ref size)) return null;
            var fullPath = path.ToString();
            var name = System.IO.Path.GetFileNameWithoutExtension(fullPath);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())));
            return new(hash, name.Length > 120 ? name[..120] : name);
        }
        finally { Native.CloseHandle(process); }
    }

    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code >= 0 && message is 0x100 or 0x104) lastInput = Monotonic;
        return Native.CallNextHookEx(0, code, message, data);
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var now = Monotonic;
            if (message is 0x201 or 0x204 or 0x207 or 0x20A or 0x20B or 0x20E) lastInput = now;
            else if (message == 0x200)
            {
                var x = Marshal.ReadInt32(data);
                var y = Marshal.ReadInt32(data, 4);
                if (inputActivity.MouseMoved(x, y, now)) lastInput = now;
            }
        }
        return Native.CallNextHookEx(0, code, message, data);
    }

    public void Dispose()
    {
        if (keyboardHook != 0) Native.UnhookWindowsHookEx(keyboardHook);
        if (mouseHook != 0) Native.UnhookWindowsHookEx(mouseHook);
        if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct LastInput { public uint Size; public uint Time; }
        internal delegate nint HookProc(int code, nint message, nint data);
        internal delegate void WinEventProc(nint hook, uint evt, nint window, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint GetShellWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
        [DllImport("kernel32.dll")] internal static extern nint OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll")] internal static extern bool GetLastInputInfo(ref LastInput input);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint SetWindowsHookEx(int type, HookProc callback, nint module, uint thread);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
        [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
        [DllImport("user32.dll")] internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int length, out int needed);
    }
}
