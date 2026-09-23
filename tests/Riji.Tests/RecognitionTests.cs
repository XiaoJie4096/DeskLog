using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Riji.Core;
using Riji.Infrastructure;
using Xunit;

namespace Riji.Tests;

public sealed class RecognitionTests
{
    private sealed class Handler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        public List<string> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return response(++Calls);
        }
    }
    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = text } } } }), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage IncompleteReply(string finishReason, string text) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finishReason, message = new { content = text } } } }), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Success() => Reply("{\"description\":\"正在阅读课程资料\",\"categoryName\":\"学习\",\"confidence\":0.9}");
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-10T00:00:00Z");

    private static Observation Foreground(string title = "视频主题", double seconds = 10) =>
        new(Epoch, seconds, new("chrome-id", "chrome"), seconds, false, 42, "window-42",
            new("example.com", title, seconds + 1.5));

    [Fact] public async Task PromptContextAndCategoryNamesAreFrozenAcrossSettingsChangesAndRecovery()
    {
        using var f = new Fixture();
        var handler = new Handler(n => n == 1 ? new(HttpStatusCode.TooManyRequests) : Success());
        var foreground = Foreground();
        using (var pipeline = f.Pipeline(handler, observe: () => foreground))
        {
            pipeline.Configure(new(true, Prompt: "自定义规则甲"), Epoch);
            pipeline.SetCategories([Category.Defaults[1], Category.Defaults[0] with { Enabled = false }]);
            await pipeline.Pulse(Epoch.AddSeconds(60));
            var job = Assert.Single(f.Store.Jobs(JobStatus.Retry));
            Assert.Equal("chrome", job.Context?.AppName); Assert.Equal("视频主题", job.Context?.BrowserTitle);
            Assert.Contains("自定义规则甲", job.Prompt);
            Assert.DoesNotContain("development", job.Prompt); Assert.DoesNotContain("learning", job.Prompt);
            Assert.DoesNotContain("Enabled", job.Prompt); Assert.DoesNotContain("Color", job.Prompt);
            Assert.DoesNotContain("#91a58e", job.Prompt);
            pipeline.Configure(new(true, Prompt: "自定义规则乙"), Epoch.AddSeconds(61));
            pipeline.SetCategories([Category.Defaults[1] with { Name = "改名后" }]);
        }
        foreground = Foreground("另一个网页");
        using var recovered = f.Pipeline(handler, observe: () => foreground);
        await recovered.Pulse(Epoch.AddSeconds(81));
        Assert.Equal(handler.Requests[0], handler.Requests[1]);
        Assert.Equal("自定义规则乙", recovered.Settings.Prompt);
        var record = Assert.Single(f.Store.Records("2026-09-10"));
        Assert.Equal("learning", record.Category.Id); Assert.Equal("学习", record.Category.Name);
    }

    [Fact] public async Task ForegroundChangeDuringScreenshotDropsMismatchedContext()
    {
        using var f = new Fixture(); var foreground = Foreground();
        using var pipeline = f.Pipeline(new Handler(_ => Success()), observe: () => foreground, capture: file => {
            File.WriteAllBytes(file, [1]); foreground = foreground with { WindowKey = "different" };
        });
        pipeline.Configure(new(true), Epoch); await pipeline.Pulse(Epoch.AddSeconds(60));
        var job = Assert.Single(f.Store.Jobs(JobStatus.Succeeded));
        Assert.Null(job.Context); Assert.DoesNotContain("视频主题", job.Prompt);
    }

    [Fact] public void BrowserTitleNeedsFreshConsentedEvidenceAndIsNotTakenFromAnotherTab()
    {
        var before = Foreground();
        Assert.Equal("视频主题", RecognitionContext.From(before, before)?.BrowserTitle);
        Assert.Null(RecognitionContext.From(before, Foreground("不同标签"))?.BrowserTitle);
        Assert.Null(RecognitionContext.From(before with { Website = null }, before)?.BrowserTitle);
        Assert.Null(RecognitionContext.From(before, before with { MonotonicSeconds = 12 })?.BrowserTitle);
        Assert.Null(RecognitionContext.From(before, before with { SystemBlocked = true }));
        var sessions = new BrowserSessions(); var session = Guid.NewGuid().ToString();
        var challenge = sessions.Challenge(session, "chrome", before, false);
        sessions.Report(new(session, challenge.Nonce, 1, "activity", "example.com", "不可发送的标题"), before);
        var withoutConsent = before with { Website = sessions.Evidence(before) };
        Assert.Null(RecognitionContext.From(withoutConsent, withoutConsent)?.BrowserTitle);
    }

    [Fact] public void CustomPromptValidationRejectsEmptyAndOversizedValues()
    {
        Assert.Throws<ArgumentException>(() => new CaptureSettings(Prompt: " ").Validate());
        Assert.Throws<ArgumentException>(() => new CaptureSettings(Prompt: new string('x', 10001)).Validate());
        new CaptureSettings(Prompt: null).Validate();
    }

    private sealed class Fixture : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "RijiTests", Guid.NewGuid().ToString("N"));
        public LocalStore Store { get; }
        public Fixture()
        {
            Store = new(Path.Combine(Folder, "test.db"));
            Store.SaveValue("ai-active", new AiConfiguration("https://example.com/v1", "test-model", "protected-test-key"));
        }
        public RecognitionPipeline Pipeline(HttpMessageHandler handler, Func<bool>? allowed = null, Func<Observation?>? observe = null, Action<string>? capture = null) => new(Store, new(new HttpClient(handler)), Folder,
            capture ?? (file => File.WriteAllBytes(file, [137, 80, 78, 71])), allowed ?? (() => true), _ => "protected-test-key", _ => "test-key", observe);
        public void Dispose() { Store.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(Folder, true); }
    }

    [Fact] public async Task UnavailableScreenshotDirectoryDoesNotPreventHistoryAndReportsCaptureFailure()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString("N");
        var savedJob = new RecognitionJob(id, Epoch, "2026-09-10", 60, id + ".png", Category.Defaults);
        fixture.Store.SaveJob(savedJob); fixture.Store.Complete(savedJob, new("历史合成记录", "learning", 0.9));
        // A file at the directory path produces a real filesystem failure without changing ACLs.
        File.WriteAllText(Path.Combine(fixture.Folder, "Screenshots"), "blocking test file");
        var handler = new Handler(_ => Success());
        using var pipeline = fixture.Pipeline(handler);
        Assert.Equal("历史合成记录", Assert.Single(fixture.Store.Records("2026-09-10")).Description);
        pipeline.Configure(new(true), Epoch); await pipeline.Pulse(Epoch.AddSeconds(60));
        Assert.Contains("创建截图目录失败", pipeline.Error);
        var report = File.ReadAllText(Directory.GetFiles(Path.Combine(fixture.Folder, "Logs"), "recognition-failure-*.html").Single());
        Assert.Contains("创建截图目录", report);
        Assert.Contains("IOException", report);
        Assert.False(pipeline.Busy); Assert.Equal(0, handler.Calls);
        Assert.Single(fixture.Store.Jobs(JobStatus.Invalid));
        Assert.Single(fixture.Store.Records("2026-09-10"));
        File.Delete(Path.Combine(fixture.Folder, "Screenshots"));
        await pipeline.Pulse(Epoch.AddSeconds(120));
        Assert.Null(pipeline.Error); Assert.Equal(1, handler.Calls);
        Assert.Equal(2, fixture.Store.Records("2026-09-10").Count);
    }

    [Fact] public async Task RecognitionFailureReportIncludesScreenshotPromptAndRawResponseButRedactsApiKey()
    {
        using var fixture = new Fixture();
        var handler = new Handler(_ => IncompleteReply("length", "provider said test-key was rejected"));
        using var pipeline = fixture.Pipeline(handler); pipeline.Configure(new(true, Prompt: "识别活动"), Epoch);
        await pipeline.Pulse(Epoch.AddSeconds(60));
        Assert.Contains("finish_reason=length", pipeline.Error);
        var report = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(fixture.Folder, "Logs"), "recognition-failure-*.html")));
        Assert.Contains("识别时的截图", report); Assert.Contains("识别活动", report);
        Assert.Contains("finish_reason", report); Assert.Contains("length", report);
        Assert.Contains("[API Key 已隐藏]", report); Assert.DoesNotContain("test-key", report);
        Assert.DoesNotContain("provider said test-key", pipeline.Error);
        Assert.Single(fixture.Store.Jobs(JobStatus.Retry));
    }

    [Fact] public async Task RetryUsesOriginalIntervalAndCommitsExactlyOnce()
    {
        using var fixture = new Fixture(); var handler = new Handler(call => call == 1 ? new(HttpStatusCode.TooManyRequests) : Success());
        using var pipeline = fixture.Pipeline(handler); pipeline.Configure(new(true), Epoch);
        await pipeline.Pulse(Epoch.AddSeconds(60));
        Assert.Single(fixture.Store.Jobs(JobStatus.Retry)); Assert.Empty(fixture.Store.Records("2026-09-10"));
        pipeline.Configure(new(true, 120), Epoch.AddSeconds(61));
        await pipeline.Pulse(Epoch.AddSeconds(81));
        Assert.Equal(60, fixture.Store.Records("2026-09-10").Single().Seconds);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots"), "*.png"));
        await pipeline.Pulse(Epoch.AddSeconds(82)); Assert.Equal(2, handler.Calls);
    }

    [Fact] public async Task TenMinuteSamplesProduceExactlySixHundredSeconds()
    {
        using var fixture = new Fixture(); var handler = new Handler(_ => Success());
        using var pipeline = fixture.Pipeline(handler); pipeline.Configure(new(true), Epoch);
        for (var minute = 1; minute <= 10; minute++)
        {
            await pipeline.Pulse(Epoch.AddMinutes(minute));
            await pipeline.Pulse(Epoch.AddMinutes(minute));
        }
        var records = fixture.Store.Records("2026-09-10");
        Assert.Equal(10, handler.Calls); Assert.Equal(10, records.Count);
        Assert.Equal(10, records.Select(record => record.Id).Distinct().Count());
        Assert.Equal(600, records.Sum(record => record.Seconds));
        Assert.Equal(10, fixture.Store.Jobs(JobStatus.Succeeded).Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots")));
    }

    [Fact] public async Task ThreeMinuteSamplesAndTwoTwoMinuteSamplesProduceFourHundredTwentySeconds()
    {
        using var fixture = new Fixture(); var handler = new Handler(_ => Success());
        using var pipeline = fixture.Pipeline(handler); pipeline.Configure(new(true), Epoch);
        for (var minute = 1; minute <= 3; minute++) await pipeline.Pulse(Epoch.AddMinutes(minute));
        pipeline.Configure(new(true, 120), Epoch.AddMinutes(3));
        await pipeline.Pulse(Epoch.AddMinutes(4)); Assert.Equal(3, handler.Calls);
        await pipeline.Pulse(Epoch.AddMinutes(5)); await pipeline.Pulse(Epoch.AddMinutes(7));
        var records = fixture.Store.Records("2026-09-10");
        Assert.Equal(5, handler.Calls); Assert.Equal(420, records.Sum(record => record.Seconds));
        Assert.Equal(3, records.Count(record => record.Seconds == 60));
        Assert.Equal(2, records.Count(record => record.Seconds == 120));
    }

    [Fact] public async Task ExpiredFailureRemovesImageEvenWhenSuccessfulImagesAreKept()
    {
        using var fixture = new Fixture(); var handler = new Handler(call => call == 2 ? new(HttpStatusCode.ServiceUnavailable) : Success());
        using var pipeline = fixture.Pipeline(handler); pipeline.Configure(new(true, KeepImages: true), Epoch);
        await pipeline.Pulse(Epoch.AddMinutes(1)); await pipeline.Pulse(Epoch.AddMinutes(2));
        Assert.Single(fixture.Store.Records("2026-09-10"));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots")).Length);
        Assert.Single(fixture.Store.Jobs(JobStatus.Retry));
        pipeline.Configure(new(false, KeepImages: true), Epoch.AddMinutes(3));
        pipeline.RetryFailed(); await pipeline.Pulse(Epoch.AddDays(40));
        Assert.Equal(2, handler.Calls);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots")));
        Assert.Single(fixture.Store.Jobs(JobStatus.Invalid));
        Assert.Empty(fixture.Store.Jobs(JobStatus.Retry));
        Assert.Equal(0, fixture.Store.UnfinishedJobs(0).Total);
        Assert.DoesNotContain(fixture.Store.JobCounts(), item => item.Status == "Invalid");
    }

    private sealed class NetworkHandler : HttpMessageHandler
    {
        public bool Connected;
        public int Requests;
        public int Probes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                Probes++;
                return Connected ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed))
                    : Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
            }
            Requests++;
            return Connected ? Task.FromResult(Success()) : Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
        }
    }

    [Fact] public async Task NetworkFailureWaitsForConnectionAndRetriesOnceWhenReachable()
    {
        using var fixture = new Fixture(); using var handler = new NetworkHandler();
        using (var first = fixture.Pipeline(handler))
        {
            first.Configure(new(true, MaxAttempts: 1), Epoch);
            await first.Pulse(Epoch.AddMinutes(1));
            Assert.Contains("无法连接 AI 服务", first.Error);
        }
        var waiting = Assert.Single(fixture.Store.Jobs(JobStatus.Retry));
        Assert.True(waiting.WaitForConnection);
        using var pipeline = fixture.Pipeline(handler);
        await pipeline.Pulse(Epoch.AddSeconds(90));
        await pipeline.Pulse(Epoch.AddSeconds(100));
        Assert.Equal(1, handler.Requests); Assert.Equal(1, handler.Probes);
        handler.Connected = true;
        await pipeline.Pulse(Epoch.AddSeconds(120));
        Assert.Equal(2, handler.Requests); Assert.Equal(2, handler.Probes);
        Assert.Single(fixture.Store.Records("2026-09-10"));
        Assert.Empty(fixture.Store.Jobs(JobStatus.Retry));
    }

    [Fact] public async Task ExhaustedRetryBecomesHiddenInvalidJobAndRemovesScreenshot()
    {
        using var fixture = new Fixture(); using var pipeline = fixture.Pipeline(new Handler(_ => new(HttpStatusCode.TooManyRequests)));
        pipeline.Configure(new(true, MaxAttempts: 1), Epoch);
        await pipeline.Pulse(Epoch.AddMinutes(1));
        var invalid = Assert.Single(fixture.Store.Jobs(JobStatus.Invalid));
        Assert.Equal(1, invalid.Attempts);
        Assert.False(invalid.CleanupPending);
        Assert.Empty(fixture.Store.Records("2026-09-10"));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots"), "*.png"));
        Assert.Equal(0, fixture.Store.UnfinishedJobs(0).Total);
        Assert.DoesNotContain(fixture.Store.JobCounts(), item => item.Status == "Invalid");
    }

    [Fact] public void InvalidCategoryChangesKeepPreviousConfiguration()
    {
        using var fixture = new Fixture(); using var pipeline = fixture.Pipeline(new Handler(_ => Success()));
        var initial = pipeline.Categories;
        var invalidSets = new[] {
            Enumerable.Range(0, 13).Select(index => new Category("id" + index, "分类" + index, "定义", "#abcdef")).ToArray(),
            new[] { initial[0], initial[1] with { Name = " " + initial[0].Name + " " } },
            new[] { initial[0] with { Name = " " } },
            new[] { initial[0] with { Meaning = new string('长', 81) } }
        };
        foreach (var invalid in invalidSets)
        {
            Assert.Throws<ArgumentException>(() => pipeline.SetCategories(invalid));
            Assert.Equal(initial, pipeline.Categories);
            Assert.Null(fixture.Store.Read<Category[]>("categories"));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("<html>bad gateway</html>")]
    [InlineData("{\"description\":\"\",\"categoryName\":\"学习\",\"confidence\":0.9}")]
    [InlineData("{\"description\":\"reading\",\"categoryName\":\"unknown\",\"confidence\":0.9}")]
    [InlineData("{\"description\":\"reading\",\"categoryName\":\"学习\"}")]
    public async Task InvalidRecognitionNeverCountsOrDeletesImage(string response)
    {
        using var fixture = new Fixture(); using var pipeline = fixture.Pipeline(new Handler(_ => Reply(response)));
        pipeline.Configure(new(true), Epoch); await pipeline.Pulse(Epoch.AddSeconds(60));
        Assert.Empty(fixture.Store.Records("2026-09-10"));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots"), "*.png"));
        Assert.Single(fixture.Store.Jobs(JobStatus.Retry));
    }

    [Fact] public async Task AuthorizationFailurePausesFurtherRequests()
    {
        using var fixture = new Fixture(); var handler = new Handler(_ => new(HttpStatusCode.Unauthorized));
        using var pipeline = fixture.Pipeline(handler); pipeline.Configure(new(true), Epoch);
        await pipeline.Pulse(Epoch.AddSeconds(60)); await pipeline.Pulse(Epoch.AddSeconds(600));
        Assert.True(pipeline.Paused); Assert.True(fixture.Store.Read<bool>("ai-paused")); Assert.Equal(1, handler.Calls);
    }

    [Fact] public async Task DisablingCapturePausesRetriesAndNewJobs()
    {
        using var fixture = new Fixture(); var handler = new Handler(_ => new(HttpStatusCode.TooManyRequests));
        using var pipeline = fixture.Pipeline(handler); pipeline.Configure(new(true), Epoch);
        await pipeline.Pulse(Epoch.AddSeconds(60)); pipeline.Configure(new(false), Epoch.AddSeconds(61));
        await pipeline.Pulse(Epoch.AddSeconds(1000)); Assert.Equal(1, handler.Calls);
    }

    [Fact] public async Task DatabaseFailureRetainsRecoverableImage()
    {
        using var fixture = new Fixture(); var handler = new Handler(_ => Success());
        using var connection = new SqliteConnection("Data Source=" + fixture.Store.Path); connection.Open();
        using (var command = connection.CreateCommand()) { command.CommandText = "CREATE TRIGGER fail_record BEFORE INSERT ON records BEGIN SELECT RAISE(FAIL,'injected'); END"; command.ExecuteNonQuery(); }
        using var pipeline = fixture.Pipeline(handler); pipeline.Configure(new(true), Epoch); await pipeline.Pulse(Epoch.AddSeconds(60));
        Assert.Empty(fixture.Store.Records("2026-09-10")); Assert.Single(fixture.Store.Jobs(JobStatus.Manual));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots"), "*.png"));
        using (var command = connection.CreateCommand()) { command.CommandText = "DROP TRIGGER fail_record"; command.ExecuteNonQuery(); }
        pipeline.RetryFailed(); await pipeline.Pulse(Epoch.AddSeconds(61));
        Assert.Single(fixture.Store.Records("2026-09-10"));
    }

    [Fact] public async Task FailedCandidateDoesNotReplaceWorkingConfiguration()
    {
        using var fixture = new Fixture(); using var pipeline = fixture.Pipeline(new Handler(_ => new(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<AiFailure>(() => pipeline.TestConfiguration("https://example.net/v1", "new-model", "new-test-key"));
        Assert.Equal("test-model", fixture.Store.Read<AiConfiguration>("ai-active")!.Model);
        Assert.Equal("new-model", fixture.Store.Read<AiConfiguration>("ai-candidate")!.Model);
        Assert.Empty(fixture.Store.Records("2026-09-10"));
    }

    [Fact] public async Task RunningJobRecoversWithOriginalCategorySnapshot()
    {
        using var fixture = new Fixture(); var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(fixture.Folder, "Screenshots")); File.WriteAllBytes(Path.Combine(fixture.Folder, "Screenshots", id + ".png"), [1]);
        fixture.Store.SaveJob(new(id, Epoch, "2026-09-10", 60, id + ".png", Category.Defaults, JobStatus.Running));
        using var pipeline = fixture.Pipeline(new Handler(_ => Reply("{\"description\":\"旧任务恢复\",\"categoryId\":\"learning\",\"confidence\":0.9}")));
        pipeline.SetCategories(Category.Defaults.Select(category => category.Id == "learning" ? category with { Name = "新的分类" } : category).ToArray());
        pipeline.Configure(new(true), Epoch); await pipeline.Pulse(Epoch.AddSeconds(1));
        Assert.Equal("学习", fixture.Store.Records("2026-09-10").Single().Category.Name);
        pipeline.SetCategories(Category.Defaults.Where(category => category.Id != "learning").ToArray());
        Assert.DoesNotContain(fixture.Store.Read<Category[]>("categories")!, category => category.Id == "learning");
        Assert.Equal("学习", fixture.Store.Records("2026-09-10").Single().Category.Name);
    }

    [Fact] public async Task StateBlockingPreventsCaptureAndRetryUntilResumed()
    {
        using var fixture = new Fixture(); var allowed = false; var handler = new Handler(_ => new(HttpStatusCode.TooManyRequests));
        using var pipeline = fixture.Pipeline(handler, () => allowed); pipeline.Configure(new(true), Epoch);
        await pipeline.Pulse(Epoch.AddSeconds(60));
        Assert.False(Directory.Exists(Path.Combine(fixture.Folder, "Screenshots"))); Assert.Equal(0, handler.Calls);
        allowed = true; await pipeline.Pulse(Epoch.AddSeconds(61));
        allowed = false; await pipeline.Pulse(Epoch.AddSeconds(100)); Assert.Equal(1, handler.Calls);
        allowed = true; await pipeline.Pulse(Epoch.AddSeconds(101)); Assert.Equal(2, handler.Calls);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.SetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return Success();
        }
    }

    private sealed class CompletingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Started.TrySetResult(); await Release.Task.WaitAsync(token); return Success();
        }
    }

    [Theory]
    [InlineData(RecordingMode.Away, 0)]
    [InlineData(RecordingMode.NoScreen, 10)]
    public async Task InflightSuccessFinishesOriginalSampleWhileNewCaptureStops(RecordingMode mode, int expectedAppSeconds)
    {
        using var fixture = new Fixture(); using var handler = new CompletingHandler();
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        Observation At(int second) => new(Epoch.AddSeconds(second), second, new("app", "app"), second);
        tracker.Observe(At(60));
        var captures = 0;
        using var pipeline = new RecognitionPipeline(fixture.Store, new(new HttpClient(handler)), fixture.Folder,
            path => { captures++; File.WriteAllBytes(path, [137, 80, 78, 71]); },
            () => tracker.State.Mode is RecordingMode.Default or RecordingMode.Locked, x => x, x => x);
        pipeline.Configure(new(true), Epoch);
        var request = pipeline.Pulse(Epoch.AddSeconds(60));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tracker.SetMode(mode, mode == RecordingMode.NoScreen ? 15 : null, At(60));
        // Reentrant scheduler calls while the request is pending must not create another worker.
        await pipeline.Pulse(Epoch.AddSeconds(61));
        handler.Release.SetResult(); await request;
        for (var second = 61; second <= 70; second++) tracker.Observe(At(second));
        await pipeline.Pulse(Epoch.AddSeconds(120));
        Assert.Equal(1, captures); Assert.Equal(1, handler.Calls);
        Assert.Equal(60, Assert.Single(fixture.Store.Records("2026-09-10")).Seconds);
        Assert.Equal(expectedAppSeconds, tracker.Pending.Sum(slice => slice.Seconds));
        Assert.Single(fixture.Store.Jobs(JobStatus.Succeeded));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots"), "*.png"));
    }

    [Fact] public async Task ResultCommitFailureRollsBackRecordAndRetainsScreenshotUntilRecovery()
    {
        using var fixture = new Fixture(); var handler = new Handler(_ => Success());
        using var database = new SqliteConnection("Data Source=" + fixture.Store.Path); database.Open();
        void Sql(string sql) { using var command = database.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        Sql("CREATE TRIGGER fail_result BEFORE UPDATE ON recognition_jobs WHEN NEW.status='Succeeded' BEGIN SELECT RAISE(FAIL,'injected sensitive storage detail'); END");
        using (var pipeline = fixture.Pipeline(handler))
        {
            pipeline.Configure(new(true), Epoch); await pipeline.Pulse(Epoch.AddSeconds(60));
            Assert.Empty(fixture.Store.Records("2026-09-10"));
            Assert.Single(fixture.Store.Jobs(JobStatus.Manual));
            Assert.Single(Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots"), "*.png"));
        Assert.Contains("保存识别任务或结果失败", pipeline.Error);
            var report = File.ReadAllText(Directory.GetFiles(Path.Combine(fixture.Folder, "Logs"), "recognition-failure-*.html").Single());
            Assert.Contains("保存识别结果到本地数据库", report);
            Assert.Contains("SQLite 数据库操作失败", report);
            Assert.DoesNotContain("sensitive", pipeline.Error);
        }
        Sql("DROP TRIGGER fail_result");
        using var reopened = new LocalStore(fixture.Store.Path);
        using var recovered = new RecognitionPipeline(reopened, new(new HttpClient(handler)), fixture.Folder,
            _ => throw new InvalidOperationException("Recovery must use the original screenshot"), () => true, x => x, x => x);
        recovered.RetryFailed(); await recovered.Pulse(Epoch.AddSeconds(61)); await recovered.Pulse(Epoch.AddSeconds(62));
        Assert.Equal(2, handler.Calls);
        Assert.Equal(60, Assert.Single(reopened.Records("2026-09-10")).Seconds);
        Assert.False(Assert.Single(reopened.Jobs(JobStatus.Succeeded)).CleanupPending);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots"), "*.png"));
    }

    [Fact] public async Task ScreenshotDeletionFailureDoesNotRepeatRecognition()
    {
        using var fixture = new Fixture(); FileStream? held = null;
        var handler = new Handler(_ =>
        {
            held = File.Open(Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots"), "*.png").Single(), FileMode.Open, FileAccess.Read, FileShare.None);
            return Success();
        });
        using var pipeline = fixture.Pipeline(handler); pipeline.Configure(new(true), Epoch);
        try
        {
            await pipeline.Pulse(Epoch.AddSeconds(60));
            Assert.Single(fixture.Store.Records("2026-09-10"));
            Assert.True(fixture.Store.Jobs(JobStatus.Succeeded).Single().CleanupPending);
        }
        finally { held?.Dispose(); }
        pipeline.Configure(new(false), Epoch.AddSeconds(61));
        await pipeline.Pulse(Epoch.AddSeconds(120));
        Assert.False(fixture.Store.Jobs(JobStatus.Succeeded).Single().CleanupPending);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots"), "*.png"));
        Assert.Equal(1, handler.Calls); Assert.Single(fixture.Store.Records("2026-09-10"));
    }

    [Fact] public async Task ShutdownCancelsInflightRequestAndLeavesRecoverableJob()
    {
        using var fixture = new Fixture(); using var handler = new BlockingHandler();
        using var pipeline = fixture.Pipeline(handler); pipeline.Configure(new(true), Epoch);
        var pending = pipeline.Pulse(Epoch.AddSeconds(60));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pipeline.Shutdown().WaitAsync(TimeSpan.FromSeconds(5)); await pending;
        Assert.False(pipeline.Busy); Assert.Empty(fixture.Store.Records("2026-09-10"));
        Assert.Single(fixture.Store.Jobs(JobStatus.Running));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Folder, "Screenshots"), "*.png"));
        using var recovered = fixture.Pipeline(new Handler(_ => Success()));
        await recovered.Pulse(Epoch.AddSeconds(61));
        Assert.Single(fixture.Store.Records("2026-09-10"));
    }
}
