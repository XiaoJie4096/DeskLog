using System.Runtime.InteropServices;
using System.Text;
using Riji.Core;
using Windows.Media.Control;

namespace Riji.Windows;

// Sample media in the background; stale or ambiguous results never manufacture activity.
public sealed class WindowsMediaMonitor : IDisposable
{
    private readonly object gate = new();
    private Task<GlobalSystemMediaTransportControlsSessionManager>? managerTask;
    private MediaSample? sample;
    private double nextPoll;
    private bool polling;
    private bool enabled;
    private bool disposed;
    private int generation;
    private sealed record MediaSample(string AppId, int ProcessId, double At);

    // Start at most one asynchronous sample every five seconds without blocking tracking.
    public double? CaptureActivity(Observation foreground, bool eligible)
    {
        lock (gate)
        {
            if (disposed || !eligible || foreground.SystemBlocked || foreground.DesktopShown || foreground.App is null)
            {
                if (enabled) generation++;
                enabled = false;
                sample = null;
                nextPoll = 0;
                return null;
            }
            enabled = true;
            if (!polling && foreground.MonotonicSeconds >= nextPoll)
            {
                polling = true;
                nextPoll = foreground.MonotonicSeconds + 5;
                var version = generation;
                _ = Task.Run(() => PollAsync(foreground, version));
            }
            return sample is { } current && current.AppId == foreground.App.Id && current.ProcessId == foreground.ProcessId
                && foreground.MonotonicSeconds - current.At is >= 0 and <= 5 ? current.At : null;
        }
    }

    // Accept Playing from any media type, only while the sampled foreground remains stable.
    private async Task PollAsync(Observation expected, int version)
    {
        MediaSample? result = null;
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
            managerTask ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
            var manager = await managerTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var window = GetForegroundWindow();
            GetWindowThreadProcessId(window, out var pid);
            if ($"{pid}:{window}" != expected.WindowKey) return;
            var appId = ReadAppUserModelId(pid);
            var at = WindowsObserver.Monotonic;
            foreach (var session in manager.GetSessions())
            {
                try
                {
                    if (MediaSourceMatcher.Matches(session.SourceAppUserModelId, expected.App!.Name, appId)
                        && session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        result = new(expected.App.Id, (int)pid, at);
                        break;
                    }
                }
                catch (Exception) { /* A disappearing session must not hide another valid session. */ }
            }
            if (GetForegroundWindow() != window) result = null;
        }
        catch (Exception)
        {
            // Unsupported systems, unavailable sessions and API failures fall back to normal idle timing.
            managerTask = managerTask is { IsCompleted: false } ? managerTask : null;
        }
        finally
        {
            lock (gate)
            {
                if (!disposed && enabled && generation == version) sample = result;
                polling = false;
            }
        }
    }

    // Read the explicit packaged application identity before falling back to process-name matching.
    private static string? ReadAppUserModelId(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        try
        {
            uint length = 0;
            if (GetApplicationUserModelId(process, ref length, null) != 122 || length is 0 or > 32768) return null;
            var value = new StringBuilder((int)length);
            return GetApplicationUserModelId(process, ref length, value) == 0 ? value.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    // Reject pending samples at shutdown without waiting on Windows media services.
    public void Dispose()
    {
        lock (gate) { disposed = true; enabled = false; sample = null; generation++; }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder? appId);
}
