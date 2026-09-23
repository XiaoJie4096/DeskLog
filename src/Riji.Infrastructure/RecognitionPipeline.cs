using Riji.Core;

namespace Riji.Infrastructure;

// Serialize capture and recognition work, and preserve recoverable jobs across process restarts.
public sealed class RecognitionPipeline : IDisposable
{
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
    private bool busy;
    private bool closing;
    private DateTimeOffset nextCapture = DateTimeOffset.MaxValue;
    public AiConfiguration? Configuration { get; set; }
    public CaptureSettings Settings { get; private set; }
    public Category[] Categories { get; private set; }
    public string? Error { get; private set; }
    public bool Paused { get; private set; }
    public bool Busy => busy;

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
        foreach (var job in store.Jobs(JobStatus.Capturing, JobStatus.Running))
        {
            var exists = File.Exists(ImagePath(job));
            store.SaveJob(job with { Status = exists ? JobStatus.Pending : JobStatus.Manual, Error = exists ? null : "截图未完成，请检查保留文件。" });
        }
        foreach (var job in store.Jobs(JobStatus.Succeeded).Where(job => job.CleanupPending)) Cleanup(job);
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
        if (busy || closing) throw new InvalidOperationException("识别任务正在执行或日迹正在退出，请稍后测试配置。");
        if (!allowed()) throw new InvalidOperationException("请先恢复允许识屏的记录状态，再测试截图识别。");
        AiConfiguration.ValidateEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model) || model.Length > 200 || budget is < 4000 or > 150000) throw new ArgumentException("请补全 Key、模型和有效上下文长度。");
        var candidate = new AiConfiguration(endpoint.Trim(), model.Trim(), protect(key), budget, string.IsNullOrWhiteSpace(summaryModel) ? null : summaryModel.Trim());
        store.SaveValue("ai-candidate", candidate);
        busy = true; var image = Path.Combine(images, "test-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
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
            busy = false;
            if (!Settings.KeepImages) try { File.Delete(image); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Error = "测试截图清理失败，文件仍保留在本机。"; }
        }
    }

    public async Task Pulse(DateTimeOffset now)
    {
        if (busy || closing || Configuration is null || Paused || !Settings.Enabled || !allowed()) return;
        busy = true;
        RecognitionJob? job = null;
        var stage = "准备识别任务";
        AiConfiguration? attemptedConfiguration = null;
        string? attemptedKey = null;
        try
        {
            job = store.NextJob(now);
            if (job is null && Settings.Enabled)
            {
                if (nextCapture == DateTimeOffset.MaxValue) nextCapture = now.AddSeconds(Settings.IntervalSeconds);
                if (now < nextCapture) return;
                nextCapture = now.AddSeconds(Settings.IntervalSeconds);
                var id = Guid.NewGuid().ToString("N");
                job = new(id, now.ToUniversalTime(), TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).ToString("yyyy-MM-dd"), Settings.IntervalSeconds, id + ".png", Categories.Where(x => x.Enabled).ToArray());
                var instruction = Settings.Prompt ?? RecognitionPrompts.Default;
                job = job with { Prompt = RecognitionPrompts.Build(instruction, job.Categories, null) };
                store.SaveJob(job);
                stage = "创建截图目录";
                Directory.CreateDirectory(images);
                var before = observe();
                stage = "采集屏幕截图";
                capture(ImagePath(job));
                var context = RecognitionContext.From(before, observe());
                job = job with { Status = JobStatus.Pending, Context = context, Prompt = RecognitionPrompts.Build(instruction, job.Categories, context) }; store.SaveJob(job);
            }
            if (job is null || !allowed()) return;
            stage = "读取截图文件";
            var bytes = await File.ReadAllBytesAsync(ImagePath(job), stop.Token);
            if (!allowed() || !Settings.Enabled || closing) return;
            stage = "保存识别任务状态";
            job = job with { Status = JobStatus.Running, Attempts = job.Attempts + 1, Error = null }; store.SaveJob(job);
            stage = "请求截图识别服务";
            attemptedConfiguration = Configuration;
            attemptedKey = unprotect(Configuration.ProtectedKey);
            var result = await client.Recognize(attemptedConfiguration, attemptedKey, bytes, job.Categories, stop.Token, job.Prompt);
            stage = "保存识别结果到本地数据库";
            store.Complete(job, result);
            Error = null;
            Cleanup(job with { Status = JobStatus.Succeeded, CleanupPending = true });
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception error)
        {
            diagnosticLog.RecognitionFailure(job?.Id, job?.Utc, stage, error,
                attemptedConfiguration?.Endpoint ?? Configuration?.Endpoint,
                attemptedConfiguration?.Model ?? Configuration?.Model,
                job?.Prompt,
                job is null ? null : ImagePath(job), attemptedKey);
            Error = diagnosticLog.WriteFailed
                ? "截图识别失败，诊断日志写入失败，请检查本地数据目录权限和磁盘空间。"
                : "截图识别失败，详细原因已写入本机识别故障日志。";
            if (error is AiFailure { Authorization: true })
            { Paused = true; try { store.SaveValue("ai-paused", true); } catch (Microsoft.Data.Sqlite.SqliteException) { } }
            if (job is not null)
            {
                var retry = error is AiFailure { Retryable: true } && job.Attempts < Settings.MaxAttempts;
                try { store.SaveJob(job with { Status = retry ? JobStatus.Retry : JobStatus.Manual, Error = Error, RetryAt = now.AddSeconds(Math.Min(300, 10 * Math.Pow(2, job.Attempts))) }); }
                catch (Exception) { /* The previously committed job remains the recovery source. */ }
            }
        }
        finally { busy = false; }
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
            if (!Settings.KeepImages) File.Delete(ImagePath(job));
            store.SaveJob(job with { Status = JobStatus.Succeeded, CleanupPending = false, Error = null });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { Error = "识别结果已保存，截图清理等待恢复；不会重新识别。"; }
    }

    public void RetryFailed()
    {
        if (busy || closing) throw new InvalidOperationException("请等待当前任务结束，或重启恢复识别服务。");
        foreach (var job in store.Jobs(JobStatus.Manual, JobStatus.Retry))
            store.SaveJob(job with { Status = JobStatus.Pending, Attempts = 0, RetryAt = null, Error = null });
        foreach (var job in store.Jobs(JobStatus.Succeeded).Where(job => job.CleanupPending)) Cleanup(job);
    }

    public string ActiveKey() => Configuration is null ? throw new InvalidOperationException("请先验证 AI 配置。") : unprotect(Configuration.ProtectedKey);
    // Let pending continuations finish before their database is disposed by the host.
    public async Task Shutdown()
    {
        closing = true; stop.Cancel();
        while (busy) await Task.Delay(25);
    }
    public void Dispose() { closing = true; stop.Cancel(); }
}
