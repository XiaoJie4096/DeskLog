using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Media.Control;

namespace MediaSessionProbe;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--snapshot")
        {
            var sample = Probe.Read().GetAwaiter().GetResult();
            File.WriteAllText(args[1], JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new ProbeWindow());
    }
}

internal sealed record Foreground(string Process, int Pid, string? AppId, string? Error = null);
internal sealed record MediaRow(string Source, string Status, string Type, string PlaybackInfoType, string Match, string Reason, bool VideoCandidate);
internal sealed record Sample(DateTimeOffset At, Foreground Foreground, MediaRow[] Sessions, string? Error = null);

internal static class Probe
{
    private static GlobalSystemMediaTransportControlsSessionManager? manager;

    public static async Task<Sample> Read()
    {
        var foreground = ReadForeground();
        var rows = new List<MediaRow>();
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            foreach (var session in manager.GetSessions())
            {
                var source = session.SourceAppUserModelId;
                var (match, reason) = Match(source, foreground);
                try
                {
                    var properties = await session.TryGetMediaPropertiesAsync();
                    var playback = session.GetPlaybackInfo();
                    var status = playback.PlaybackStatus;
                    var type = properties.PlaybackType;
                    var candidate = match == "匹配"
                        && status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                        && type == Windows.Media.MediaPlaybackType.Video;
                    rows.Add(new(source, status.ToString(), type?.ToString() ?? "未上报",
                        playback.PlaybackType?.ToString() ?? "未上报", match, reason, candidate));
                }
                catch (Exception error) { rows.Add(new(source, "读取失败", "未知", "未知", match, error.Message, false)); }
            }
            // Discard matching conclusions if focus changed while awaiting media properties.
            var after = ReadForeground();
            if (after.Pid != foreground.Pid || after.AppId != foreground.AppId)
                return new(DateTimeOffset.Now, after, rows.Select(row => row with
                    { Match = "待确认", Reason = "读取期间前台应用发生变化，等待下一次刷新", VideoCandidate = false }).ToArray());
            return new(DateTimeOffset.Now, foreground, rows.ToArray());
        }
        catch (Exception error) { return new(DateTimeOffset.Now, foreground, rows.ToArray(), error.Message); }
    }

    private static (string Match, string Reason) Match(string source, Foreground foreground)
    {
        if (foreground.Error is not null) return ("无法确认", foreground.Error);
        if (!string.IsNullOrEmpty(foreground.AppId) && source.Equals(foreground.AppId, StringComparison.OrdinalIgnoreCase))
            return ("匹配", "来源标识与前台应用 AppUserModelID 完全相同");
        // Only accept a simple executable identity; do not guess from titles or partial names.
        if (source.Contains('!') || source.Contains('\\') || source.Contains('/') || string.IsNullOrWhiteSpace(source))
            return ("无法确认", "此来源标识无法直接对应前台进程；需要额外映射");
        var name = source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? source[..^4] : source;
        return name.Equals(foreground.Process, StringComparison.OrdinalIgnoreCase)
            ? ("匹配", "来源标识与前台进程名称完全相同（只确认应用，不确认窗口或标签页）")
            : ("未匹配", "来源标识与前台应用标识、进程名称均不相同");
    }

    private static Foreground ReadForeground()
    {
        var window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var handle = OpenProcess(0x1000, false, pid);
            string? appId = null;
            if (handle != 0)
            {
                try
                {
                    uint length = 0;
                    if (GetApplicationUserModelId(handle, ref length, null) == 122 && length is > 0 and < 32768)
                    {
                        var value = new StringBuilder((int)length);
                        if (GetApplicationUserModelId(handle, ref length, value) == 0) appId = value.ToString();
                    }
                }
                finally { CloseHandle(handle); }
            }
            return new(process.ProcessName, (int)pid, appId);
        }
        catch (Exception error) { return new("未知", (int)pid, null, error.Message); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder? appId);
}

internal sealed class ProbeWindow : Form
{
    private readonly Label foreground = new() { AutoSize = true, MaximumSize = new Size(1000, 0) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(1000, 0) };
    private readonly DataGridView table = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        BackgroundColor = SystemColors.Window, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
    };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private readonly Queue<Sample> history = new();
    private bool refreshing;

    public ProbeWindow()
    {
        Text = "视频播放检测 v2 · 对照两处上报类型";
        ClientSize = new Size(1250, 440);
        MinimumSize = new Size(800, 360);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Text = "播放视频后切到浏览器或播放器，等待 5 秒，再回来查看或复制结果。" }, 0, 0);
        layout.Controls.Add(foreground, 0, 1);
        layout.Controls.Add(status, 0, 2);
        foreach (var (name, title, weight) in new[] { ("source", "Windows 来源标识", 160f), ("state", "播放状态", 70f),
            ("type", "元数据上报类型", 95f), ("playbackType", "播放信息上报类型", 110f),
            ("match", "前台匹配", 65f), ("video", "严格视频条件", 75f), ("reason", "判断依据", 230f) })
            table.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight });
        table.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        layout.Controls.Add(table, 0, 3);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var refresh = new Button { Text = "立即刷新", AutoSize = true };
        refresh.Click += async (_, _) => await RefreshSample();
        var copy = new Button { Text = "复制最近结果", AutoSize = true };
        copy.Click += (_, _) =>
        {
            if (history.Count == 0) return;
            Clipboard.SetText(JsonSerializer.Serialize(history.ToArray(), new JsonSerializerOptions { WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        };
        actions.Controls.Add(refresh); actions.Controls.Add(copy);
        actions.Controls.Add(new Label { AutoSize = true, Padding = new Padding(0, 7, 0, 0), Text = "仅检测；保留最近 12 次结果。不控制播放、不修改日迹。" });
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
        timer.Tick += async (_, _) => await RefreshSample();
        Shown += async (_, _) => { timer.Start(); await RefreshSample(); };
        FormClosed += (_, _) => timer.Dispose();
    }

    private async Task RefreshSample()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var sample = await Probe.Read();
            if (IsDisposed) return;
            history.Enqueue(sample);
            while (history.Count > 12) history.Dequeue();
            foreground.Text = $"前台：{sample.Foreground.Process}    PID：{sample.Foreground.Pid}    AppUserModelID：{sample.Foreground.AppId ?? "未提供"}";
            status.Text = $"刷新：{sample.At:HH:mm:ss}    媒体会话：{sample.Sessions.Length}    "
                + (sample.Error is not null ? "读取失败：" + sample.Error : sample.Sessions.Any(row => row.VideoCandidate)
                    ? "检测到符合条件的前台视频" : "未确认符合条件的前台视频");
            table.Rows.Clear();
            foreach (var row in sample.Sessions)
                table.Rows.Add(row.Source, row.Status, row.Type, row.PlaybackInfoType, row.Match, row.VideoCandidate ? "是" : "否", row.Reason);
        }
        catch (Exception error) { if (!IsDisposed) status.Text = "检测失败：" + error.Message; }
        finally { refreshing = false; }
    }
}
