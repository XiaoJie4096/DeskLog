using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Riji.Core;
using Riji.Infrastructure;
using Riji.Windows;

namespace Riji.Desktop;

public sealed class MainWindow : Window
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    private const int DwmUseImmersiveDarkMode = 20, DwmCaptionColor = 35;
    private readonly WebView2 web = new();
    private readonly LocalStore store;
    private Tracker tracker;
    private readonly WindowsObserver observer;
    private readonly DispatcherTimer timer;
    private readonly System.Windows.Forms.NotifyIcon tray;
    private readonly string dataDir;
    private readonly string profile;
    private readonly string appVersion;
    private readonly bool systemTest;
    private readonly BrowserSessions browserSessions = new();
    private readonly BrowserHttpServer? browser;
    private readonly System.Net.Http.HttpClient aiHttp;
    private readonly bool offlineReview;
    private readonly OfflineHandler offlineHandler = new();
    private sealed class OfflineHandler : System.Net.Http.HttpMessageHandler
    {
        public int Attempts;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            Attempts++;
            throw new System.Net.Http.HttpRequestException("独立离线验证：网络不可用。");
        }
    }
    private RecognitionPipeline recognition = null!;
    private SummaryService summaries = null!;
    private HourlySummaryService hourlySummaries = null!;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private bool exiting;
    private bool maintenance;
    private string? dataStatus;
    private bool ready;
    private int ticks;
    private string? storageError;
    private readonly DiagnosticLog diagnosticLog;
    private readonly long startupStarted;
    private long startupPrevious;
    private readonly List<StartupTimingStage> startupStages = [];
    private readonly List<StartupUiDelay> startupUiDelays = [];
    private readonly object startupUiGate = new();
    private System.Threading.Timer? startupUiProbe;
    private int startupUiProbePending;
    private int startupUiProbeStopped;
    private double startupUiMonitoringStartMs;
    private bool startupTimingWritten;
    private readonly List<RuntimeStageTiming> runtimeStages = [];
    private DateTimeOffset runtimeBatchStartUtc;
    private long runtimeStarted;
    private long runtimeLastFlush;
    private int droppedRuntimeStages;
    private bool runtimeTiming;
    private HwndSource? source;
    private string selectedDay = DateTime.Now.ToString("yyyy-MM-dd");

    public MainWindow(string dataDir, string profile, bool systemTest, bool offlineReview = false, long startupStarted = 0)
    {
        this.startupStarted = startupStarted == 0 ? Stopwatch.GetTimestamp() : startupStarted;
        startupPrevious = this.startupStarted;
        if (offlineReview && profile != "Test") throw new ArgumentException("离线验证仅允许独立测试目录。");
        this.offlineReview = offlineReview;
        aiHttp = offlineReview ? new(offlineHandler) : new(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false });
        aiHttp.Timeout = TimeSpan.FromMinutes(6);
        this.dataDir = dataDir; this.profile = profile;
        appVersion = ReadAppVersion();
        diagnosticLog = new(dataDir);
        MarkStartup("preflight_and_window_allocation");
        this.systemTest = systemTest;
        Title = "日迹" + (profile != "Production" ? " · 开发版" : "");
        Width = 1240; Height = 840; MinWidth = 850; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 27, 27));
        store = new(Path.Combine(dataDir, "riji.db"));
        MarkStartup("database_open");
        tracker = new((store.Read<TrackingSettings>("settings") ?? new()) with { WebsiteSnippets = false }, store.Read<ModeState>("mode") ?? new(), TimeZoneInfo.Local);
        selectedDay = DayRange.Today(DateTimeOffset.Now, tracker.Settings, TimeZoneInfo.Local);
        SetStartup(tracker.Settings.StartWithWindows);
        MarkStartup("tracker_and_startup_registry");
        observer = new();
        MarkStartup("input_hook_install");
        StartStartupUiProbe();
        InitializePipelines(measureStartup: true);
        dataStatus = DataArchive.CleanupRetired(store, dataDir);
        MarkStartup("retired_screenshot_cleanup");
        if (profile is "Development" or "Production")
        {
            browser = new(profile == "Development" ? 4177 : 4178,
                (session, name) => Dispatcher.Invoke(() => browserSessions.Challenge(session, name, observer.Capture(), tracker.Settings.WebsiteTitles)),
                report => Dispatcher.Invoke(() =>
                {
                    if (maintenance || exiting) return false;
                    tracker.Observe(Capture());
                    var accepted = browserSessions.Report(report, observer.Capture());
                    tracker.Observe(Capture()); return accepted;
                }));
            _ = browser.Start();
        }
        MarkStartup("browser_server_start");
        observer.ForegroundChanged += OnForeground;
        tracker.Observe(Capture());
        MarkStartup("first_observation");
        timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Tick();
        tray = new() { Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "riji.ico")), Text = "日迹 · 正在记录", Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开日迹", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("默认模式", null, (_, _) => Dispatcher.Invoke(() => ChangeMode(RecordingMode.Default, null)));
        menu.Items.Add("离开模式", null, (_, _) => Dispatcher.Invoke(() => ChangeMode(RecordingMode.Away, null)));
        menu.Items.Add("退出日迹", null, (_, _) => Dispatcher.Invoke(Exit));
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(ShowMainWindow);
        };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);
        Content = web;
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(WindowMessage);
            ApplyTitleBarTheme(tracker.Settings.Theme);
            WTSRegisterSessionNotification(source.Handle, 0);
        };
        Loaded += async (_, _) => { MarkStartup("wpf_window_loaded"); await InitializeWeb(); };
        StateChanged += (_, _) => { if (ready && WindowState != WindowState.Minimized) Push(); };
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => Cleanup();
        MarkStartup("window_setup");
    }

    private void ShowMainWindow()
    {
        if (exiting) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != nint.Zero) SetForegroundWindow(handle);
        Push();
    }

    // Load only bundled content, with a dedicated WebView profile and restricted message origin.
    private async Task InitializeWeb()
    {
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "Web");
            if (!File.Exists(Path.Combine(folder, "index.html"))) throw new InvalidOperationException("缺少正式界面，请先运行前端构建。");
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDir, "WebView"));
            MarkStartup("webview_environment_wait", asyncWait: true);
            await web.EnsureCoreWebView2Async(environment);
            MarkStartup("webview_control_wait", asyncWait: true);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("riji.local", folder, CoreWebView2HostResourceAccessKind.DenyCors);
            web.CoreWebView2.Settings.AreDevToolsEnabled = profile == "Development";
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            if (offlineReview)
            {
                web.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                web.CoreWebView2.WebResourceRequested += (_, e) =>
                {
                    if (!e.Request.Uri.StartsWith("https://riji.local/", StringComparison.Ordinal))
                        e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(null, 503, "Offline test", "");
                };
            }
            web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            web.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://riji.local/", StringComparison.Ordinal)) e.Cancel = true; };
            web.CoreWebView2.WebMessageReceived += OnMessage;
            web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                MarkStartup("webview_navigation_wait", asyncWait: true);
                ready = e.IsSuccess;
                if (ready) { Push(); MarkStartup("first_snapshot"); }
                else MarkStartup("webview_navigation_failed");
                await CompleteStartupTiming();
                if (ready && offlineReview) await OfflineReviewTest();
            };
            web.Source = new Uri("https://riji.local/index.html");
            if (systemTest) ChangeMode(RecordingMode.Locked, 120);
            timer.Start();
            MarkStartup("webview_configuration_and_navigation");
        }
        catch (Exception e)
        {
            MarkStartup("webview_startup_failed");
            await CompleteStartupTiming(observeAfterSnapshot: false);
            diagnosticLog.Failure(DiagnosticEvent.WebViewStartup, e);
            System.Windows.MessageBox.Show(e.Message, "日迹启动失败");
            Exit();
        }
    }

    private Observation Capture()
    {
        var raw = observer.Capture();
        var evidence = browserSessions.Evidence(raw);
        return raw with { Website = evidence };
    }

    private void InitializePipelines(bool measureStartup = false)
    {
        recognition = new(store, new HttpAiClient(aiHttp), dataDir, ScreenCapture.SavePng,
            () => !maintenance && !exiting && tracker.Settings.AutoRecord && tracker.State.Mode is RecordingMode.Default or RecordingMode.Locked && !observer.Capture().SystemBlocked,
            SecretVault.Protect, SecretVault.Unprotect, Capture);
        if (measureStartup) MarkStartup("recognition_recovery");
        summaries = new(store, new HttpAiClient(aiHttp), () => (recognition.Configuration ?? throw new InvalidOperationException("请先在设置中验证 AI 配置。"), recognition.ActiveKey()));
        if (measureStartup) MarkStartup("summary_recovery");
        hourlySummaries = new(store, summaries, TimeZoneInfo.Local, DateTimeOffset.UtcNow, () => !maintenance && !exiting && !offlineReview && profile != "Test" && recognition.Configuration is not null);
        if (measureStartup) MarkStartup("hourly_summary_setup");
    }

    private void MarkStartup(string name, bool asyncWait = false)
    {
        if (startupTimingWritten) return;
        var now = Stopwatch.GetTimestamp();
        startupStages.Add(new(name, Math.Round(Stopwatch.GetElapsedTime(startupPrevious, now).TotalMilliseconds, 1),
            Math.Round(Stopwatch.GetElapsedTime(startupStarted, now).TotalMilliseconds, 1), asyncWait));
        startupPrevious = now;
    }

    private void StartStartupUiProbe()
    {
        startupUiMonitoringStartMs = Stopwatch.GetElapsedTime(startupStarted).TotalMilliseconds;
        startupUiProbe = new System.Threading.Timer(_ => ProbeStartupUi(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
    }

    private void ProbeStartupUi()
    {
        if (Volatile.Read(ref startupUiProbeStopped) != 0 || Interlocked.CompareExchange(ref startupUiProbePending, 1, 0) != 0) return;
        var posted = Stopwatch.GetTimestamp();
        var postedUtc = DateTimeOffset.UtcNow;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() =>
            {
                var handled = Stopwatch.GetTimestamp();
                var delay = Stopwatch.GetElapsedTime(posted, handled).TotalMilliseconds;
                if (delay >= 100 && Volatile.Read(ref startupUiProbeStopped) == 0)
                {
                    lock (startupUiGate)
                        startupUiDelays.Add(new(postedUtc, Math.Round(Stopwatch.GetElapsedTime(startupStarted, posted).TotalMilliseconds, 1),
                            Math.Round(Stopwatch.GetElapsedTime(startupStarted, handled).TotalMilliseconds, 1), Math.Round(delay, 1)));
                }
                Volatile.Write(ref startupUiProbePending, 0);
            }));
        }
        catch (InvalidOperationException) { Volatile.Write(ref startupUiProbePending, 0); }
    }

    private async Task CompleteStartupTiming(bool observeAfterSnapshot = true)
    {
        if (startupTimingWritten) return;
        startupTimingWritten = true;
        var stages = startupStages.ToArray();
        await Task.Run(() => diagnosticLog.StartupTiming(stages));
        if (observeAfterSnapshot)
        {
            await Task.Delay(1000);
            if (!Dispatcher.HasShutdownStarted)
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        Volatile.Write(ref startupUiProbeStopped, 1);
        startupUiProbe?.Dispose();
        StartupUiDelay[] delays;
        lock (startupUiGate) delays = startupUiDelays.ToArray();
        var end = Math.Round(Stopwatch.GetElapsedTime(startupStarted).TotalMilliseconds, 1);
        await Task.Run(() => diagnosticLog.StartupUiDelays(startupUiMonitoringStartMs, end, delays));
        if (!exiting && !systemTest && !offlineReview) StartRuntimeTiming();
    }

    private void StartRuntimeTiming()
    {
        runtimeStarted = runtimeLastFlush = Stopwatch.GetTimestamp();
        runtimeBatchStartUtc = DateTimeOffset.UtcNow;
        runtimeTiming = true;
        observer.StartInputTiming();
    }

    private void TimeRuntime(string stage, Action action)
    {
        if (!runtimeTiming) { action(); return; }
        var started = Stopwatch.GetTimestamp();
        try { action(); }
        finally { RecordRuntimeStage(stage, started); }
    }

    private void RecordRuntimeStage(string stage, long started)
    {
        if (!runtimeTiming) return;
        var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (duration < 20) return;
        if (runtimeStages.Count >= 128) { droppedRuntimeStages++; return; }
        runtimeStages.Add(new(DateTimeOffset.UtcNow, stage, Math.Round(duration, 1)));
    }

    private void FlushRuntimeTiming(bool force = false)
    {
        if (!runtimeTiming) return;
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(runtimeLastFlush, now);
        var complete = Stopwatch.GetElapsedTime(runtimeStarted, now) >= TimeSpan.FromHours(1);
        if (!force && !complete && elapsed < TimeSpan.FromSeconds(30)) return;
        if (force || complete) { runtimeTiming = false; observer.StopInputTiming(); }
        var endUtc = DateTimeOffset.UtcNow;
        var (inputDelays, droppedInputDelays) = observer.DrainInputTiming();
        var mouseDelays = inputDelays.Select(delay => new RuntimeInputTiming(delay.EventUtc, delay.DelayMs)).ToArray();
        var stages = runtimeStages.ToArray();
        var droppedStages = droppedRuntimeStages;
        var startUtc = runtimeBatchStartUtc;
        runtimeStages.Clear();
        droppedRuntimeStages = 0;
        runtimeBatchStartUtc = endUtc;
        runtimeLastFlush = now;
        if (force) diagnosticLog.RuntimeTiming(startUtc, endUtc, mouseDelays, droppedInputDelays, stages, droppedStages);
        else _ = Task.Run(() => diagnosticLog.RuntimeTiming(startUtc, endUtc, mouseDelays, droppedInputDelays, stages, droppedStages));
    }

    private void OnForeground() { if (!exiting && !maintenance) Dispatcher.BeginInvoke(() => { if (!exiting && !maintenance) tracker.Observe(Capture()); }); }

    private void Tick()
    {
        if (maintenance || exiting) return;
        var started = Stopwatch.GetTimestamp();
        try
        {
            TimeRuntime("observe", () => tracker.Observe(Capture()));
            if (++ticks % 4 == 0)
            {
                TimeRuntime("commit", () => Commit());
                if (ticks % 12 == 0 && IsVisible && WindowState != WindowState.Minimized)
                    TimeRuntime("push", Push);
                UpdateTrayStatus();
                TimeRuntime("recognition_pulse_dispatch", () => { _ = recognition.Pulse(DateTimeOffset.UtcNow); });
                TimeRuntime("hourly_pulse_dispatch", () => { _ = hourlySummaries.Pulse(DateTimeOffset.UtcNow); });
                TimeRuntime("summary_pulse_dispatch", () => { _ = summaries.Pulse(DateTimeOffset.UtcNow); });
                if (systemTest)
                {
                    WriteSystemTrace("tick");
                    if (File.Exists(Path.Combine(dataDir, "stop-validation"))) Exit();
                }
            }
        }
        finally { RecordRuntimeStage("tick", started); FlushRuntimeTiming(); }
    }

    private bool Commit()
    {
        try { store.Save(tracker.Pending, tracker.Settings, tracker.State, tracker.PendingWebsites); tracker.Acknowledge(); storageError = null; return true; }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        { diagnosticLog.Failure(DiagnosticEvent.StorageCommit, e); storageError = "保存失败，尚未提交的数据不会显示为已保存：" + e.GetType().Name; return false; }
    }

    private void ChangeMode(RecordingMode mode, int? minutes)
    { tracker.SetMode(mode, minutes, Capture()); Commit(); Push(); }

    // Accept a small typed command surface; never execute renderer-provided code or paths.
    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (exiting || !e.Source.StartsWith("https://riji.local/", StringComparison.Ordinal)) return;
        string? id = null;
        try
        {
            if (e.WebMessageAsJson.Length > 16384) throw new ArgumentException("请求过大。");
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetString();
            var type = root.GetProperty("type").GetString();
            if (maintenance) throw new InvalidOperationException("数据维护中，请等待完成。");
            object? value = null;
            switch (type)
            {
                case "snapshot": selectedDay = root.GetProperty("day").GetString()!; store.Apps(selectedDay); Push(); break;
                case "mode":
                    var mode = Enum.Parse<RecordingMode>(root.GetProperty("mode").GetString()!);
                    ChangeMode(mode, root.TryGetProperty("minutes", out var minutes) && minutes.ValueKind == JsonValueKind.Number ? minutes.GetInt32() : null); break;
                case "settings":
                    var settings = root.GetProperty("settings").Deserialize<TrackingSettings>(json) ?? throw new ArgumentException("设置无效。");
                    settings = settings with { WebsiteSnippets = false, WebsiteRules = null };
                    var dayRulesChanged = settings.NightMode != tracker.Settings.NightMode || settings.DayStartHour != tracker.Settings.DayStartHour;
                    tracker.Configure(settings, Capture());
                    if (dayRulesChanged) selectedDay = DayRange.Today(DateTimeOffset.Now, tracker.Settings, TimeZoneInfo.Local);
                    SetStartup(settings.StartWithWindows);
                    ApplyTitleBarTheme(settings.Theme);
                    tracker.Observe(Capture());
                    if (!settings.WebsiteTitles) { browserSessions.DisableTitles(); tracker.Observe(Capture()); }
                    Commit(); Push(); break;
                case "captureSettings":
                    recognition.Configure(root.GetProperty("settings").Deserialize<CaptureSettings>(json) ?? throw new ArgumentException("截图设置无效。"), DateTimeOffset.UtcNow);
                    Push(); break;
                case "categories":
                    recognition.SetCategories(root.GetProperty("categories").Deserialize<Category[]>(json) ?? throw new ArgumentException("分类无效。"));
                    Push(); break;
                case "retryRecognition": recognition.RetryFailed(); Push(); break;
                case "recognitionJobs": value = store.UnfinishedJobs(root.GetProperty("offset").GetInt32()); break;
                case "openRecognitionLog":
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(diagnosticLog.RecognitionFailureIndex()) { UseShellExecute = true });
                    break;
                case "testAi":
                    var submittedKey = root.TryGetProperty("key", out var keyElement) ? keyElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(submittedKey) && recognition.Configuration is not null) submittedKey = recognition.ActiveKey();
                    if (string.IsNullOrWhiteSpace(submittedKey)) throw new ArgumentException("首次配置时请填写 API Key。");
                    await recognition.TestConfiguration(root.GetProperty("endpoint").GetString()!, root.GetProperty("model").GetString()!, submittedKey, root.TryGetProperty("contextK", out var ck) ? ck.GetInt32() * 1000 : 100000, root.TryGetProperty("summaryModel", out var sm) ? sm.GetString() : null);
                    Push(); break;
                case "models":
                    var modelKey = root.TryGetProperty("key", out var modelKeyElement) ? modelKeyElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(modelKey) && recognition.Configuration is not null) modelKey = recognition.ActiveKey();
                    if (string.IsNullOrWhiteSpace(modelKey)) throw new ArgumentException("请先填写 API Key。");
                    value = await new HttpAiClient(aiHttp).Models(root.GetProperty("endpoint").GetString()!, modelKey);
                    break;
                case "saveAi":
                    var saveKey = root.TryGetProperty("key", out var saveKeyElement) ? saveKeyElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(saveKey) && recognition.Configuration is not null) saveKey = recognition.ActiveKey();
                    if (string.IsNullOrWhiteSpace(saveKey)) throw new ArgumentException("首次配置时请填写 API Key。");
                    var saveConfig = new AiConfiguration(root.GetProperty("endpoint").GetString()!, root.GetProperty("model").GetString()!, SecretVault.Protect(saveKey), root.TryGetProperty("contextK", out var saveCk) ? saveCk.GetInt32() * 1000 : 100000, root.TryGetProperty("summaryModel", out var saveSm) ? saveSm.GetString() : null);
                    if (string.IsNullOrWhiteSpace(saveConfig.Model) || saveConfig.Model.Length > 200 || saveConfig.InputBudget is < 4000 or > 150000) throw new ArgumentException("请填写有效模型和上下文长度。\n");
                    AiConfiguration.ValidateEndpoint(saveConfig.Endpoint); store.SaveValue("ai-active", saveConfig); recognition.Configuration = saveConfig; Push(); break;
                case "summaryForm":
                    var form = root.GetProperty("form").Deserialize<SummaryForm>(json) ?? throw new ArgumentException("总结草稿无效。");
                    if (form.Start.Length > 40 || form.End.Length > 40 || form.Prompt.Length > 10000) throw new ArgumentException("草稿内容过长。");
                    store.SaveValue("summary-form", form); break;
                case "hourSummaryGenerate":
                    value = hourlySummaries.QueueGenerate(root.GetProperty("start").GetDateTimeOffset()); Push(); break;
                case "summaryGenerate":
                    value = summaries.QueueGenerate(new(root.GetProperty("start").GetDateTimeOffset(), root.GetProperty("end").GetDateTimeOffset(), TimeZoneInfo.Local.Id), root.GetProperty("prompt").GetString()!);
                    Push(); break;
                case "summaryDetail": value = store.Summary(root.GetProperty("summaryId").GetString()!); break;
                case "summaryChatDraft": store.SaveChatDraft(root.GetProperty("summaryId").GetString()!, root.GetProperty("text").GetString()!); break;
                case "summaryRetry": summaries.QueueRetry(root.GetProperty("summaryId").GetString()!); Push(); break;
                case "summaryChat":
                    await summaries.Chat(root.GetProperty("summaryId").GetString()!, root.GetProperty("question").GetString()!, root.TryGetProperty("turnId", out var turnId) ? turnId.GetString() : null);
                    Push(); break;
                case "summaryCancel": summaries.Cancel(root.TryGetProperty("summaryId", out var cancelSummary) ? cancelSummary.GetString() : null); break;
                case "summaryPresets":
                    summaries.SavePresets(root.GetProperty("presets").Deserialize<PromptPreset[]>(json) ?? throw new ArgumentException("提示词预设无效。")); Push(); break;
                case "exportData": await ExportBackup(); break;
                case "importData": await ImportBackup(); break;
                case "clearData": await ClearData(); break;
                case "openDataFolder": OpenDataFolder(); break;
                case "exit": Exit(); return;
                default: throw new ArgumentException("未知命令。");
            }
            Send(new { type = "result", id, ok = storageError is null, error = storageError, value });
        }
        catch (Exception ex) { diagnosticLog.Failure(DiagnosticEvent.Command, ex); Send(new { type = "result", id, ok = false, error = ex is ArgumentException or JsonException or InvalidOperationException or AiFailure ? ex.Message : "操作失败，请检查输入或本地存储。" }); }
    }

    private void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key?.SetValue("Riji", '"' + Environment.ProcessPath + '"'); else key?.DeleteValue("Riji", false);
    }

    private void OpenDataFolder()
    {
        var folder = Path.GetDirectoryName(store.Path);
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("无法确定数据目录。");
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void ApplyTitleBarTheme(string theme)
    {
        if (source is null) return;
        var dark = theme == "dark" ? 1 : 0;
        DwmSetWindowAttribute(source.Handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        var color = theme == "dark" ? 0x00171B1B : 0x00F4F1E8;
        DwmSetWindowAttribute(source.Handle, DwmCaptionColor, ref color, sizeof(int));
    }

    private void Push()
    {
        if (!ready) return;
        try
        {
            var today = DayRange.Today(DateTimeOffset.Now, tracker.Settings, TimeZoneInfo.Local);
            var view = store.ViewDay(selectedDay, tracker.Settings, TimeZoneInfo.Local);
            Send(new { type = "snapshot", day = selectedDay, today,
                settings = tracker.Settings, mode = tracker.State, currentApp = tracker.CurrentApp?.Name,
                dataStatus, maintenance, diagnosticLogFailed = diagnosticLog.WriteFailed,
                health = storageError ?? (observer.HooksAvailable ? tracker.Health : "输入或前台事件钩子不可用，请重启检查权限"),
                apps = view.Apps, websites = view.Websites, browserConnections = browserSessions.Connections(observer.Capture().MonotonicSeconds), browserError = browser?.Error,
                recognition = new { settings = recognition.Settings, defaultPrompt = RecognitionPrompts.Default, categories = recognition.Categories, busy = recognition.Busy, paused = recognition.Paused,
                    error = recognition.Error, configured = recognition.Configuration is not null, endpoint = recognition.Configuration?.Endpoint, model = recognition.Configuration?.Model, summaryModel = recognition.Configuration?.SummaryModel ?? recognition.Configuration?.Model,
                    jobs = store.JobCounts(), latestSample = store.LatestRecognizedSample(), records = view.Records },
                hourlyDefaultPrompt = HourlySummaryService.Prompt,
                hourlySummaryError = hourlySummaries.Error, summaryBusy = summaries.Busy, summaryForm = store.Read<SummaryForm>("summary-form"),
                summaryPresets = store.Read<PromptPreset[]>("summary-presets") ?? PromptPreset.Defaults,
                summaries = store.SummaryHeaders(),
                days = store.Days(tracker.Settings, TimeZoneInfo.Local), profile, dataPath = store.Path, appVersion, savedAt = DateTimeOffset.UtcNow,
                recording = tracker.IsTimingActive, timingStatus = tracker.TimingStatus });
            UpdateTrayStatus();
        }
        catch (Exception error) { diagnosticLog.Failure(DiagnosticEvent.Snapshot, error); Send(new { type = "error", error = "无法读取统计，请检查数据库及磁盘状态。" }); }
    }

    private void UpdateTrayStatus()
    {
        var text = "日迹 · " + (tracker.State.Mode == RecordingMode.Away ? "离开" : "运行中");
        if (tray.Text != text) tray.Text = text;
    }

    private void Send(object message) { if (ready) web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, json)); }

    private static string ReadAppVersion()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            var productVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(processPath).ProductVersion;
            if (!string.IsNullOrWhiteSpace(productVersion)) return productVersion;
        }

        return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "未知";
    }

    // Quiesce producers before replacing data; restart from committed state without counting the maintenance gap.
    private async Task Maintain(Action operation)
    {
        tracker.Observe(Capture()); if (!Commit()) throw new InvalidOperationException("末段记录保存失败，未开始数据维护。");
        maintenance = true; timer.Stop(); Push();
        try
        {
            await recognition.Shutdown(); await hourlySummaries.Shutdown(); await summaries.Shutdown();
            operation();
        }
        finally
        {
            try
            {
                recognition.Dispose(); browserSessions.Clear();
                tracker = new((store.Read<TrackingSettings>("settings") ?? new()) with { WebsiteSnippets = false }, store.Read<ModeState>("mode") ?? new(), TimeZoneInfo.Local);
                InitializePipelines(); tracker.Observe(Capture());
            }
            catch (Exception error) { diagnosticLog.Failure(DiagnosticEvent.MaintenanceRecovery, error); dataStatus = "数据维护后服务恢复失败，采样已暂停，请重启日迹检查存储和权限。"; }
            finally { maintenance = false; timer.Start(); Push(); }
        }
    }

    private async Task ExportBackup()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "日迹备份 (*.riji)|*.riji", DefaultExt = ".riji", FileName = "日迹备份-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
        if (dialog.ShowDialog(this) != true) return;
        if (!dialog.FileName.EndsWith(".riji", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("备份文件应使用 .riji 扩展名。");
        var images = Path.Combine(dataDir, store.ImageRootName()) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(dialog.FileName).StartsWith(images, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请将备份保存到截图目录以外。");
        await Maintain(() => { DataArchive.Export(store, dataDir, dialog.FileName); dataStatus = "备份已保存：" + dialog.FileName; });
    }

    private async Task ImportBackup()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "日迹备份 (*.riji)|*.riji", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var prepared = DataArchive.Prepare(dialog.FileName, dataDir); var committed = false;
        try
        {
            if (System.Windows.MessageBox.Show($"备份含 {prepared.Data.Activities.Length} 个应用区间、{prepared.Data.Records.Length} 条识别记录、{prepared.Data.Summaries.Length} 篇总结。\n\n导入将完整替换当前记录、总结与对话，并导入分类及预设；不会叠加。原数据先自动备份。当前 API 配置保留，自动记录和截图将关闭。继续吗？", "导入日迹备份", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await Maintain(() =>
            {
                var backupFolder = Path.Combine(dataDir, "Backups"); Directory.CreateDirectory(backupFolder);
                var recovery = Path.Combine(backupFolder, "before-import-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".riji");
                DataArchive.Export(store, dataDir, recovery);
                store.ReplaceData(prepared.Data, prepared.ImageRoot); committed = true;
                dataStatus = DataArchive.CleanupRetired(store, dataDir) ?? "导入完成，记录已暂停。原数据备份：" + recovery;
            });
        }
        finally { if (!committed) DataArchive.DeleteGeneration(dataDir, prepared.ImageRoot, store.ImageRootName()); }
    }

    private async Task ClearData()
    {
        if (System.Windows.MessageBox.Show("将清空当前环境的应用/网站区间、识别记录与截图、所有总结与对话。保留分类、预设和 API 配置，关闭自动记录及截图。\n\n此操作不会新建备份，已有导出备份不会删除。确定清空吗？", "清空日迹记录", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Maintain(() =>
        {
            var empty = store.ExportData() with { Activities = [], Websites = [], Jobs = [], Records = [], Summaries = [] };
            var imageRoot = "Screenshots-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(Path.Combine(dataDir, imageRoot));
            store.ReplaceData(empty, imageRoot);
            dataStatus = DataArchive.CleanupRetired(store, dataDir) ?? "记录、总结与对话已清空。自动记录及截图已关闭。";
        });
    }

    private nint WindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x2B1)
        {
            if (wParam == 7) observer.SessionLocked = true;
            if (wParam == 8) observer.SessionLocked = false;
            if (maintenance) return 0;
            tracker.Observe(Capture()); Commit(); Push();
            if (systemTest) WriteSystemTrace("session:" + wParam);
        }
        if (msg == 0x218)
        {
            if (wParam == 4) observer.Suspended = true;
            if (wParam is 7 or 18) observer.Suspended = false;
            if (maintenance) return 0;
            tracker.Observe(Capture()); Commit(); Push();
            if (systemTest) WriteSystemTrace("power:" + wParam);
        }
        if (msg is 0x1A or 0x1E)
        { TimeZoneInfo.ClearCachedData(); if (!maintenance) { tracker.ChangeZone(TimeZoneInfo.Local, Capture()); Commit(); } }
        if (msg == 0x11 && !maintenance) { tracker.Observe(Capture()); Commit(); }
        return 0;
    }

    public void RequestExit() => Exit();

    private async void Exit()
    {
        if (exiting) return;
        if (maintenance) { System.Windows.MessageBox.Show("数据维护尚未完成，请稍后退出。", "日迹"); return; }
        tracker.Observe(Capture());
        if (!Commit() && System.Windows.MessageBox.Show("仍有数据保存失败。退出会丢失未保存的末段，仍要退出吗？", "日迹", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        exiting = true; timer.Stop();
        await recognition.Shutdown();
        await hourlySummaries.Shutdown();
        await summaries.Shutdown();
        if (browser is not null) await browser.DisposeAsync();
        Close();
    }

    // Exercise the actual native host, renderer bridge and disk persistence in a disposable profile.
    private async Task OfflineReviewTest()
    {
        try
        {
            await Task.Delay(500);
            var saved = store.Summaries().First(item => item.State == GenerationState.Succeeded);
            await web.ExecuteScriptAsync("[...document.querySelectorAll('nav button')].find(b=>b.textContent.includes('AI 总结')).click()");
            var visible = false;
            for (var attempt = 0; attempt < 30 && !visible; attempt++)
            {
                await Task.Delay(100);
                visible = await web.ExecuteScriptAsync("document.querySelector('.summary-result')?.innerText.includes(" + JsonSerializer.Serialize(saved.Text) + ") ?? false") == "true";
            }
            await web.ExecuteScriptAsync("document.querySelector('.summary-result details').open=true");
            var sources = await web.ExecuteScriptAsync("document.querySelectorAll('.summary-result ol > li').length");
            await web.ExecuteScriptAsync("document.querySelector('.summary-result').scrollIntoView({block:'start'})");
            await Task.Delay(200);
            using (var stream = File.Create(Path.Combine(dataDir, "offline-summary.png")))
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            File.WriteAllText(Path.Combine(dataDir, "offline-result.json"), JsonSerializer.Serialize(new {
                passed = visible && sources == saved.Sources.Length.ToString() && offlineHandler.Attempts == 0,
                summaryVisible = visible, sourceCount = sources, aiRequests = offlineHandler.Attempts,
                aiTransportDisabled = true, externalWebResourcesBlocked = true, systemNetworkUnchanged = true }, json));
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(dataDir, "offline-result.json"), JsonSerializer.Serialize(new { passed = false, error = error.GetType().Name }, json));
        }
        Exit();
    }

    private void Cleanup()
    {
        Volatile.Write(ref startupUiProbeStopped, 1); startupUiProbe?.Dispose();
        FlushRuntimeTiming(force: true);
        timer.Stop(); recognition.Dispose(); aiHttp.Dispose(); observer.ForegroundChanged -= OnForeground; observer.Dispose();
        if (source is not null) { WTSUnRegisterSessionNotification(source.Handle); source.RemoveHook(WindowMessage); }
        tray.Visible = false; tray.Dispose(); web.Dispose(); store.Dispose();
    }

    // Record bounded diagnostics only in explicitly selected system-test profiles.
    private void WriteSystemTrace(string evt)
    {
        var sample = observer.Capture();
        File.AppendAllText(Path.Combine(dataDir, "system-trace.jsonl"), JsonSerializer.Serialize(new {
            evt, utc = sample.Utc, mono = sample.MonotonicSeconds, blocked = sample.SystemBlocked,
            mode = tracker.State.Mode, total = store.Days().Sum(x => x.Seconds), days = store.Days(), health = tracker.Health,
            pending = tracker.Pending.Count }, json) + Environment.NewLine);
    }

    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint hwnd);
}
