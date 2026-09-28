using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Riji.Core;

namespace Riji.Windows;

public sealed record InputHookDelay(DateTimeOffset EventUtc, double DelayMs);

// Keep callbacks alive for the lifetime of the native hooks; never retain typed content.
public sealed class WindowsObserver : IDisposable
{
    private readonly Native.HookProc keyboardCallback;
    private readonly Native.HookProc mouseCallback;
    private readonly Native.WinEventProc foregroundCallback;
    private readonly Thread inputHookThread;
    private nint keyboardHook;
    private nint mouseHook;
    private readonly nint foregroundHook;
    private readonly System.Threading.Timer gamepadTimer;
    private readonly GamepadSample[] gamepads = [new(), new(), new(), new()];
    private int gamepadPollMode;
    private int disposed;
    private bool gamepadSupported = true;
    private double lastInput;
    private readonly InputActivity inputActivity;
    private nint lastWindow;
    private AppIdentity? lastApp;
    private double lastLookup = -10;
    private readonly List<InputHookDelay> inputDelays = [];
    private readonly object inputDelayGate = new();
    private int inputTiming;
    private int inputHooksAvailable;
    private int inputHookThreadId;
    private int droppedInputDelays;
    public bool Suspended { get; set; }
    public bool SessionLocked { get; set; }
    public bool HooksAvailable => Volatile.Read(ref inputHooksAvailable) != 0 && foregroundHook != 0;
    public event Action? ForegroundChanged;
    public static double Monotonic => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private void ObserveInput(double when)
    {
        double previous;
        do
        {
            previous = Volatile.Read(ref lastInput);
            if (when <= previous) return;
        } while (Interlocked.CompareExchange(ref lastInput, when, previous) != previous);
    }

    public void StartInputTiming() => Volatile.Write(ref inputTiming, 1);

    public (InputHookDelay[] Delays, int Dropped) DrainInputTiming()
    {
        lock (inputDelayGate)
        {
            var result = (inputDelays.ToArray(), droppedInputDelays);
            inputDelays.Clear();
            droppedInputDelays = 0;
            return result;
        }
    }

    public void StopInputTiming() => Volatile.Write(ref inputTiming, 0);

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
        foregroundHook = Native.SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 0);
        using var ready = new ManualResetEventSlim();
        inputHookThread = new Thread(() => RunInputHooks(ready)) { IsBackground = true, Name = "Riji input hooks" };
        inputHookThread.Start();
        ready.Wait();
        gamepadTimer = new(PollGamepads, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private void RunInputHooks(ManualResetEventSlim ready)
    {
        var signaled = false;
        try
        {
            Volatile.Write(ref inputHookThreadId, unchecked((int)Native.GetCurrentThreadId()));
            Native.PeekMessage(out _, 0, 0, 0, 0);
            var module = Native.GetModuleHandle(null);
            keyboardHook = Native.SetWindowsHookEx(13, keyboardCallback, module, 0);
            mouseHook = Native.SetWindowsHookEx(14, mouseCallback, module, 0);
            if (keyboardHook == 0 || mouseHook == 0) return;
            Volatile.Write(ref inputHooksAvailable, 1);
            ready.Set();
            signaled = true;
            while (Native.GetMessage(out _, 0, 0, 0) > 0) { }
        }
        catch (Exception) { }
        finally
        {
            Volatile.Write(ref inputHooksAvailable, 0);
            if (keyboardHook != 0) Native.UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != 0) Native.UnhookWindowsHookEx(mouseHook);
            if (!signaled) ready.Set();
        }
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
        return new(DateTimeOffset.UtcNow, now, lastApp, Volatile.Read(ref lastInput), blocked, (int)pid, $"{pid}:{window}", null, desktop);
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
        if (code >= 0 && message is 0x100 or 0x104) ObserveInput(Monotonic);
        return Native.CallNextHookEx(0, code, message, data);
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            if (Volatile.Read(ref inputTiming) != 0) RecordInputDelay(data);
            var now = Monotonic;
            if (message is 0x201 or 0x204 or 0x207 or 0x20A or 0x20B or 0x20E) ObserveInput(now);
            else if (message == 0x200)
            {
                var x = Marshal.ReadInt32(data);
                var y = Marshal.ReadInt32(data, 4);
                if (inputActivity.MouseMoved(x, y, now)) ObserveInput(now);
            }
        }
        return Native.CallNextHookEx(0, code, message, data);
    }

    private void RecordInputDelay(nint data)
    {
        // MSLLHOOKSTRUCT.time is the system tick when Windows generated this event.
        var eventTick = unchecked((uint)Marshal.ReadInt32(data, 16));
        var delayMs = unchecked((uint)Environment.TickCount64 - eventTick);
        if (delayMs < 40 || delayMs > 10000) return;
        lock (inputDelayGate)
        {
            if (inputDelays.Count >= 128) { droppedInputDelays++; return; }
            inputDelays.Add(new(DateTimeOffset.UtcNow.AddMilliseconds(-delayMs), delayMs));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        gamepadTimer.Dispose();
        StopInputTiming();
        if (inputHookThread.IsAlive)
        {
            Native.PostThreadMessage(unchecked((uint)Volatile.Read(ref inputHookThreadId)), 0x12, 0, 0);
            if (!inputHookThread.Join(TimeSpan.FromSeconds(2)))
            {
                if (keyboardHook != 0) Native.UnhookWindowsHookEx(keyboardHook);
                if (mouseHook != 0) Native.UnhookWindowsHookEx(mouseHook);
                Volatile.Write(ref inputHooksAvailable, 0);
            }
        }
        if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
    }

    private void PollGamepads(object? _)
    {
        if (Volatile.Read(ref disposed) != 0 || !gamepadSupported) return;
        var connected = false;
        try
        {
            for (uint index = 0; index < gamepads.Length; index++)
            {
                var result = Native.XInputGetState(index, out var state);
                if (result != 0) { gamepads[index].Connected = false; continue; }
                connected = true;
                var sample = gamepads[index];
                var active = IsMeaningful(state.Gamepad);
                var changed = sample.Connected && active && state.PacketNumber != sample.Packet &&
                    (!sample.Active || sample.Buttons != state.Gamepad.Buttons ||
                     Math.Abs(sample.LeftX - state.Gamepad.LeftThumbX) >= 8000 || Math.Abs(sample.LeftY - state.Gamepad.LeftThumbY) >= 8000 ||
                     Math.Abs(sample.RightX - state.Gamepad.RightThumbX) >= 8000 || Math.Abs(sample.RightY - state.Gamepad.RightThumbY) >= 8000 ||
                     Math.Abs(sample.LeftTrigger - state.Gamepad.LeftTrigger) >= 30 || Math.Abs(sample.RightTrigger - state.Gamepad.RightTrigger) >= 30);
                if (changed) ObserveInput(Monotonic);
                sample.Connected = true; sample.Active = active;
                sample.Packet = state.PacketNumber;
                sample.Buttons = state.Gamepad.Buttons;
                sample.LeftX = state.Gamepad.LeftThumbX; sample.LeftY = state.Gamepad.LeftThumbY;
                sample.RightX = state.Gamepad.RightThumbX; sample.RightY = state.Gamepad.RightThumbY;
                sample.LeftTrigger = state.Gamepad.LeftTrigger; sample.RightTrigger = state.Gamepad.RightTrigger;
            }
        }
        catch (DllNotFoundException)
        {
            gamepadSupported = false;
            return;
        }
        catch (Exception)
        {
            // A controller driver must never be able to terminate the recorder.
            // Keep the existing timer alive and retry on the next poll.
            return;
        }
        var desired = connected ? 1 : 0;
        if (Interlocked.Exchange(ref gamepadPollMode, desired) != desired)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            try { gamepadTimer.Change(connected ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(2), connected ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(2)); }
            catch (ObjectDisposedException) { }
        }
    }

    private static bool IsMeaningful(Native.XInputGamepad gamepad)
        => gamepad.Buttons != 0 || gamepad.LeftTrigger >= 30 || gamepad.RightTrigger >= 30
            || Math.Abs((int)gamepad.LeftThumbX) >= 8000 || Math.Abs((int)gamepad.LeftThumbY) >= 8000
            || Math.Abs((int)gamepad.RightThumbX) >= 8000 || Math.Abs((int)gamepad.RightThumbY) >= 8000;

    private sealed class GamepadSample
    {
        public bool Connected;
        public bool Active;
        public uint Packet;
        public ushort Buttons;
        public short LeftX, LeftY, RightX, RightY;
        public byte LeftTrigger, RightTrigger;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct LastInput { public uint Size; public uint Time; }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Message { public nint Hwnd; public uint Id; public nint WParam, LParam; public uint Time; public Point Position; public uint Private; }
        internal delegate nint HookProc(int code, nint message, nint data);
        internal delegate void WinEventProc(nint hook, uint evt, nint window, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint GetShellWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
        [DllImport("kernel32.dll")] internal static extern nint OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] internal static extern bool GetLastInputInfo(ref LastInput input);
        [DllImport("user32.dll")] internal static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
        [DllImport("user32.dll")] internal static extern int GetMessage(out Message message, nint window, uint min, uint max);
        [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint SetWindowsHookEx(int type, HookProc callback, nint module, uint thread);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
        [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
        [DllImport("user32.dll")] internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int length, out int needed);
        [StructLayout(LayoutKind.Sequential)] internal struct XInputState { internal uint PacketNumber; internal XInputGamepad Gamepad; }
        [StructLayout(LayoutKind.Sequential)] internal struct XInputGamepad { internal ushort Buttons; internal byte LeftTrigger, RightTrigger; internal short LeftThumbX, LeftThumbY, RightThumbX, RightThumbY; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", CallingConvention = CallingConvention.StdCall)] internal static extern uint XInputGetState(uint userIndex, out XInputState state);
    }
}
