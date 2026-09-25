using Riji.Core;

namespace Riji.Infrastructure;

// Capture on schedule while bounded recognition requests and durable retries run independently.
public sealed class RecognitionPipeline : IDisposable
{
    private const int MaxConcurrentRecognitions = 10;
    private const int RetryConcurrencyTarget = 6;
    private const int RetryQueueLimit = 240;
    private readonly LocalStore store;
    private readonly HttpAiClient client;
    private readonly Action<string> capture;
    private readonly Func<bool> allowed;
    private readonly Func<string, string> protect;
    private readonly Func<string, string> unprotect;
    private readonly Func<Observation?> observe;
    private readonly string images;
    private readonly DiagnosticLog diagnosticLog;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly HashSet<string> activeJobs = [];
    private readonly Dictionary<string, (RecognitionJob Job, RecognitionResult? Result)> unsaved = [];
    private bool testing;
    private bool probing;
    private bool closing;
    private DateTimeOffset nextCapture = DateTimeOffset.MaxValue;
    private DateTimeOffset nextMaintenance = DateTimeOffset.MinValue;
    private DateTimeOffset nextConnectionCheck = DateTimeOffset.MinValue;
    public AiConfiguration? Configuration { get; set; }
    public CaptureSettings Settings { get; private set; }
    public Category[] Categories { get; private set; }
    public string? Error { get; private set; }
    public bool Paused { get; private set; }
    public bool Busy { get { lock (gate) return testing || activeJobs.Count > 0; } }

    public RecognitionPipeline(LocalStore store, HttpAiClient client, string dataDirectory, Action<string> capture,
        Func<bool> allowed, Func<string, string> protect, Func<string, string> unprotect, Func<Observation?>? observe = null)
    {
        this.store = store; this.client = client; this.capture = capture; this.allowed = allowed; this.protect = protect; this.unprotect = unprotect;
        this.observe = observe ?? (() => null);
        diagnosticLog = new(dataDirectory);
        images = Path.Combine(dataDirectory, store.ImageRootName());
        Configuration = store.Read<AiConfiguration>("ai-active"); Settings = store.Read<CaptureSettings>("capture") ?? new();
        Settings.Validate(); Categories = store.Read<Category[]>("categories") ?? Category.Defaults; Category.Validate(Categories);
        Paused = store.Read<bool>("ai-paused");
        RecoverCapturingJobs(includeRunning: true);
        foreach (var job in store.CleanupJobs()) Cleanup(job);
        TrimRetries();
    }

    private string ImagePath(RecognitionJob job)
    {
        if (!Guid.TryParseExact(job.Id, "N", out _) || job.Image != job.Id + ".png") throw new InvalidDataException("截图引用无效。");
        return Path.Combine(images, job.Image);
    }

    public void Configure(CaptureSettings settings, DateTimeOffset now)
    { settings.Validate(); store.SaveValue("capture", settings); Settings = settings; nextCapture = now.AddSeconds(settings.IntervalSeconds); }
    public void SetCategories(Category[] categories)
    {
        Category.Validate(categories);
        var clean = categories.Select(category => category with { Name = category.Name.Trim(), Meaning = category.Meaning.Trim() }).ToArray();
        store.SaveValue("categories", clean); Categories = clean;
    }

    // A real image and validated response are required before replacing the active configuration.
    public async Task<RecognitionResult> TestConfiguration(string endpoint, string model, string key, int budget = 100000, string? summaryModel = null)
    {
        if (!allowed()) throw new InvalidOperationException("请先恢复允许识屏的记录状态，再测试截图识别。");
        AiConfiguration.ValidateEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model) || model.Length > 200 || budget is < 4000 or > 150000) throw new ArgumentException("请补全 Key、模型和有效上下文长度。");
        var candidate = new AiConfiguration(endpoint.Trim(), model.Trim(), protect(key), budget, string.IsNullOrWhiteSpace(summaryModel) ? null : summaryModel.Trim());
        lock (gate)
        {
            if (testing || activeJobs.Count > 0 || closing) throw new InvalidOperationException("识别任务正在执行或日迹正在退出，请稍后测试配置。");
            testing = true;
        }
        var image = Path.Combine(images, "test-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            store.SaveValue("ai-candidate", candidate);
            Directory.CreateDirectory(images);
            var categories = Categories.Where(x => x.Enabled).ToArray();
            var instruction = Settings.Prompt ?? RecognitionPrompts.Default;
            var before = observe();
            capture(image);
            var context = RecognitionContext.From(before, observe());
            var prompt = RecognitionPrompts.Build(instruction, categories, context);
            var bytes = await File.ReadAllBytesAsync(image, stop.Token);
            if (!allowed() || closing) throw new InvalidOperationException("记录状态已改变，本次验证未发送。请恢复允许识屏后重试。");
            var result = await client.Recognize(candidate, key, bytes, categories, stop.Token, prompt);
            var summaryConfiguration = candidate with { Model = candidate.SummaryModel ?? candidate.Model };
            await client.Text(summaryConfiguration, key, "请只回复“验证成功”。", stop.Token);
            store.SaveValue("ai-active", candidate); Configuration = candidate;
            store.SaveValue("ai-paused", false); Paused = false; Error = null;
            return result;
        }
        finally
        {
            lock (gate) testing = false;
            if (!Settings.KeepImages) try { File.Delete(image); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Error = "测试截图清理失败，文件仍保留在本机。"; }
        }
    }

    public async Task Pulse(DateTimeOffset now)
    {
        List<Task> started = [];
        Task? connectionCheck = null;
        lock (gate)
        {
            if (closing) return;
            try
            {
                if (now >= nextMaintenance)
                {
                    nextMaintenance = now.AddMinutes(1);
                    RecoverCapturingJobs(includeRunning: false);
                    foreach (var expired in store.ExpiredJobs(now.AddHours(-24)))
                        if (!activeJobs.Contains(expired.Id)) Invalidate(expired, "超过 24 小时未完成识别，任务已失效。");
                    foreach (var pendingCleanup in store.CleanupJobs()) Cleanup(pendingCleanup);
                    TrimRetries();
                }
                FlushUnsaved(now);
                if (Configuration is null || Paused || !Settings.Enabled || !allowed() || testing) return;

                FillRetries(now, started);
                if (nextCapture == DateTimeOffset.MaxValue) nextCapture = now.AddSeconds(Settings.IntervalSeconds);
                if (now >= nextCapture)
                {
                    nextCapture = now.AddSeconds(Settings.IntervalSeconds);
                    if (activeJobs.Count < MaxConcurrentRecognitions)
                    {
                        var job = Capture(now);
                        if (job is not null) started.Add(Start(job, now));
                    }
                }
                FillPending(now, started);
                if (!probing && now >= nextConnectionCheck && store.WaitingConnectionJob() is not null)
                {
                    probing = true;
                    nextConnectionCheck = now.AddSeconds(30);
                    connectionCheck = CheckConnection(now);
                }
            }
            catch (Exception error)
            {
                Error = FailureSummary("调度识别任务", error);
                diagnosticLog.Failure(DiagnosticEvent.StorageCommit, error);
            }
        }
        if (connectionCheck is not null) started.Add(connectionCheck);
        await Task.WhenAll(started);
    }

    private RecognitionJob? Capture(DateTimeOffset now)
    {
        var id = Guid.NewGuid().ToString("N");
        var job = new RecognitionJob(id, now.ToUniversalTime(),
            TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).ToString("yyyy-MM-dd"),
            Settings.IntervalSeconds, id + ".png", Categories.Where(x => x.Enabled).ToArray());
        var instruction = Settings.Prompt ?? RecognitionPrompts.Default;
        job = job with { Prompt = RecognitionPrompts.Build(instruction, job.Categories, null) };
        try
        {
            store.SaveJob(job);
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            ReportCaptureFailure(job, "保存截图任务", error);
            return null;
        }
        try
        {
            Directory.CreateDirectory(images);
            var before = observe();
            capture(ImagePath(job));
            var context = RecognitionContext.From(before, observe());
            job = job with { Status = JobStatus.Pending, Context = context,
                Prompt = RecognitionPrompts.Build(instruction, job.Categories, context) };
        }
        catch (Exception error)
        {
            var stage = error is IOException ? "创建截图目录" : "采集屏幕截图";
            ReportCaptureFailure(job, stage, error);
            try { Invalidate(job, Error!); } catch (Exception) { }
            return null;
        }
        try
        {
            store.SaveJob(job);
            return job;
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            ReportCaptureFailure(job, "保存截图任务", error);
            try
            {
                var pending = job with { Status = JobStatus.Pending, Error = null };
                store.SaveJob(pending);
                return pending;
            }
            catch (Exception recoveryError) when (IsStorageFailure(recoveryError)) { return null; }
        }
    }

    private void RecoverCapturingJobs(bool includeRunning)
    {
        foreach (var job in includeRunning ? store.Jobs(JobStatus.Capturing, JobStatus.Running) : store.Jobs(JobStatus.Capturing))
        {
            var exists = File.Exists(ImagePath(job));
            store.SaveJob(job with { Status = exists ? JobStatus.Pending : JobStatus.Invalid, Error = exists ? null : "截图未完成，任务已失效。" });
        }
    }

    private void ReportCaptureFailure(RecognitionJob job, string stage, Exception error)
    {
        Error = FailureSummary(stage, error);
        diagnosticLog.RecognitionFailure(job.Id, job.Utc, stage, error,
            Configuration?.Endpoint, Configuration?.Model, job.Prompt, ImagePath(job), null);
    }

    private static bool IsStorageFailure(Exception error) => error is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException;

    private Task Start(RecognitionJob job, DateTimeOffset now)
    {
        activeJobs.Add(job.Id);
        return Process(job, now);
    }

    private void FillRetries(DateTimeOffset now, List<Task> started)
    {
        while (activeJobs.Count < RetryConcurrencyTarget)
        {
            var job = store.NextJob(now, retryOnly: true);
            if (job is null || activeJobs.Contains(job.Id) || unsaved.ContainsKey(job.Id)) break;
            started.Add(Start(job, now));
        }
    }

    private void FillPending(DateTimeOffset now, List<Task> started)
    {
        while (activeJobs.Count < MaxConcurrentRecognitions)
        {
            var job = store.NextJob(now);
            if (job is null || activeJobs.Contains(job.Id) || unsaved.ContainsKey(job.Id)) break;
            started.Add(Start(job, now));
        }
    }

    private async Task CheckConnection(DateTimeOffset now)
    {
        bool reachable;
        try { reachable = await client.CanReach(Configuration!, stop.Token); }
        catch (OperationCanceledException) when (closing) { reachable = false; }
        List<Task> started = [];
        lock (gate)
        {
            probing = false;
            if (!reachable || closing) return;
            try
            {
                foreach (var job in store.WaitingConnectionJobs(RetryQueueLimit))
                    store.SaveJob(job with { WaitForConnection = false, RetryAt = null });
                if (!Paused && Settings.Enabled && allowed() && !testing) FillRetries(now, started);
            }
            catch (Exception error) { Error = FailureSummary("恢复网络重试", error); }
        }
        await Task.WhenAll(started);
    }

    private async Task Process(RecognitionJob original, DateTimeOffset now)
    {
        var job = original;
        RecognitionResult? result = job.SavedResult;
        var stage = "保存识别任务状态";
        AiConfiguration? attemptedConfiguration = null;
        string? attemptedKey = null;
        try
        {
            job = job with { Status = JobStatus.Running, Attempts = job.Attempts + (result is null ? 1 : 0), Error = null };
            lock (gate) store.SaveJob(job);
            if (result is null)
            {
                stage = "读取截图文件";
                var bytes = await File.ReadAllBytesAsync(ImagePath(job), stop.Token);
                if (!allowed() || !Settings.Enabled || closing)
                {
                    lock (gate) store.SaveJob(job with { Status = JobStatus.Pending });
                    return;
                }
                stage = "请求截图识别服务";
                attemptedConfiguration = Configuration;
                attemptedKey = unprotect(attemptedConfiguration!.ProtectedKey);
                result = await client.Recognize(attemptedConfiguration, attemptedKey, bytes, job.Categories, stop.Token, job.Prompt);
            }
            stage = "保存识别结果到本地数据库";
            lock (gate)
            {
                store.Complete(job, result);
                Error = null;
                Cleanup(job with { Status = JobStatus.Succeeded, CleanupPending = true });
            }
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception error)
        {
            lock (gate)
            {
                diagnosticLog.RecognitionFailure(job.Id, job.Utc, stage, error,
                    attemptedConfiguration?.Endpoint ?? Configuration?.Endpoint,
                    attemptedConfiguration?.Model ?? Configuration?.Model, job.Prompt, ImagePath(job), attemptedKey);
                Error = FailureSummary(stage, error);
                if (error is AiFailure { NetworkFailure: true })
                { nextConnectionCheck = now.AddSeconds(30); }
                if (error is AiFailure { Authorization: true })
                { Paused = true; try { store.SaveValue("ai-paused", true); } catch (Microsoft.Data.Sqlite.SqliteException) { } }
                var storageFailure = error is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException;
                var retryable = result is not null || storageFailure || error is AiFailure { NetworkFailure: true }
                    || error is AiFailure { Retryable: true } && job.Attempts < Settings.MaxAttempts;
                var invalid = error is FileNotFoundException or DirectoryNotFoundException
                    || result is null && error is AiFailure { Retryable: true, NetworkFailure: false } && !retryable;
                var next = invalid
                    ? job with { Status = JobStatus.Invalid, Error = Error, SavedResult = null, RetryAt = null,
                        WaitForConnection = false, CleanupPending = File.Exists(ImagePath(job)) }
                    : job with { Status = retryable ? JobStatus.Retry : JobStatus.Manual,
                        Error = Error, SavedResult = result,
                        RetryAt = retryable && error is not AiFailure { NetworkFailure: true }
                            ? now.AddSeconds(Math.Min(300, 10 * Math.Pow(2, Math.Min(job.Attempts, 5)))) : null,
                        WaitForConnection = retryable && error is AiFailure { NetworkFailure: true } };
                try
                {
                    store.SaveJob(next);
                    if (invalid && next.CleanupPending) Cleanup(next);
                    else if (retryable) TrimRetries();
                }
                catch (Exception) { unsaved[job.Id] = (next, result); }
            }
        }
        finally { lock (gate) activeJobs.Remove(job.Id); }
    }

    private void FlushUnsaved(DateTimeOffset now)
    {
        foreach (var (id, pending) in unsaved.ToArray())
        {
            if (activeJobs.Contains(id)) continue;
            try
            {
                if (pending.Result is not null)
                {
                    store.Complete(pending.Job, pending.Result);
                    Cleanup(pending.Job with { Status = JobStatus.Succeeded, CleanupPending = true });
                }
                else
                {
                    store.SaveJob(pending.Job);
                    if (pending.Job.Status == JobStatus.Invalid && pending.Job.CleanupPending) Cleanup(pending.Job);
                }
                unsaved.Remove(id);
            }
            catch (Exception) { break; }
        }
        TrimRetries();
    }

    private void TrimRetries()
    {
        while (store.RetryCount() > RetryQueueLimit && store.OldestRetry() is { } oldest)
            Invalidate(oldest, $"等待重试任务超过 {RetryQueueLimit} 个，较早的任务已失效。");
    }

    private static string FailureSummary(string stage, Exception error) => error switch
    {
        AiFailure failure => failure.Message,
        FileNotFoundException or DirectoryNotFoundException => "找不到本次截图，无法继续识别。",
        UnauthorizedAccessException => $"{stage}失败：Windows 拒绝访问文件或目录。",
        IOException => $"{stage}失败：文件或目录不可用，请检查磁盘空间及占用情况。",
        Microsoft.Data.Sqlite.SqliteException => "保存识别任务或结果失败，请检查本地数据库和磁盘空间。",
        _ => $"{stage}失败（{error.GetType().Name}），请查看故障详情。"
    };

    private void Invalidate(RecognitionJob job, string reason)
    {
        var invalid = job with { Status = JobStatus.Invalid, Error = reason, SavedResult = null, RetryAt = null,
            WaitForConnection = false, CleanupPending = File.Exists(ImagePath(job)) };
        store.SaveJob(invalid);
        if (invalid.CleanupPending) Cleanup(invalid);
    }

    internal static string DescribeFailure(string stage, Exception error)
    {
        if (error is AiFailure aiFailure)
            return $"失败阶段：{stage}。原因：{aiFailure.Message}";

        var reason = error switch
        {
            UnauthorizedAccessException => "Windows 拒绝访问所需文件或目录。",
            FileNotFoundException => "找不到本次截图文件。",
            DirectoryNotFoundException => "截图目录不存在或无法访问。",
            IOException => "文件读写失败，可能是磁盘空间不足、文件被占用或路径不可用。",
            Microsoft.Data.Sqlite.SqliteException sqlite => $"SQLite 数据库操作失败（错误码 {sqlite.SqliteErrorCode}，扩展码 {sqlite.SqliteExtendedErrorCode}）。",
            InvalidDataException => "截图数据无效或不完整。",
            _ => $"发生 {error.GetType().Name}（HRESULT 0x{error.HResult:X8}）。"
        };
        return $"失败阶段：{stage}。原因：{reason}";
    }

    private void Cleanup(RecognitionJob job)
    {
        try
        {
            if (job.Status == JobStatus.Invalid || !Settings.KeepImages) File.Delete(ImagePath(job));
            store.SaveJob(job with { CleanupPending = false, Error = job.Status == JobStatus.Invalid ? job.Error : null });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { Error = "截图清理失败，稍后会自动重试。"; }
    }

    public void RetryFailed()
    {
        lock (gate)
        {
            if (Busy || closing) throw new InvalidOperationException("请等待当前任务结束，或重启恢复识别服务。");
            foreach (var job in store.Jobs(JobStatus.Manual, JobStatus.Retry))
                store.SaveJob(job with { Status = JobStatus.Retry, Attempts = 0, RetryAt = null, Error = null, WaitForConnection = false });
            TrimRetries();
            foreach (var job in store.CleanupJobs()) Cleanup(job);
        }
    }

    public string ActiveKey() => Configuration is null ? throw new InvalidOperationException("请先验证 AI 配置。") : unprotect(Configuration.ProtectedKey);
    // Let pending continuations finish before their database is disposed by the host.
    public async Task Shutdown()
    {
        closing = true; stop.Cancel();
        while (Busy || probing) await Task.Delay(25);
    }
    public void Dispose() { closing = true; stop.Cancel(); }
}
