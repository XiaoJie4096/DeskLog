using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Riji.Core;
using Riji.Infrastructure;
using Xunit;

namespace Riji.Tests;

public sealed class SummaryTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-11T00:00:00Z");
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Prompts = [];
        public Func<int, CancellationToken, Task<HttpResponseMessage>> Respond = (_, _) => Task.FromResult(Reply("观察到阅读活动，记录之外的时间未知。"));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Prompts.Add(body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!);
            return await Respond(Prompts.Count, token);
        }
    }
    private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content } } } })) };
    private sealed class Fixture : IDisposable
    {
        public readonly string Folder = Path.Combine(Path.GetTempPath(), "RijiTests", Guid.NewGuid().ToString("N"));
        public LocalStore Store;
        public readonly Handler Http = new();
        public readonly AiConfiguration Config = new("https://example.com/v1", "test", "encrypted-not-exported", 4000);
        public Fixture() => Store = new(Path.Combine(Folder, "test.db"));
        public SummaryService Service() => new(Store, new(new HttpClient(Http)), () => (Config, "secret-not-exported"));
        public string Add(int minute, string description = "正在阅读", int seconds = 60)
        {
            var id = Guid.NewGuid().ToString("N"); var job = new RecognitionJob(id, Epoch.AddMinutes(minute), "2026-09-11", seconds, id + ".png", Category.Defaults);
            Store.SaveJob(job); Store.Complete(job, new(description, "learning", 0.9)); return id;
        }
        public void Reopen() { Store.Dispose(); Store = new(Path.Combine(Folder, "test.db")); }
        public void Dispose() { Store.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(Folder, true); }
    }
    private static SummaryRange Range(int start = 0, int end = 60) => new(Epoch.AddMinutes(start), Epoch.AddMinutes(end), TimeZoneInfo.Utc.Id);

    [Fact] public async Task HourlyPromptKeepsIntermediateDetailsAndAppliesStyleOnlyAtFinalStage()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 12; i++) fixture.Add(i, "项目名称与问题 " + i + new string('文', 180));
        var id = await fixture.Service().Generate(Range(), HourlySummaryService.Prompt, hourly: true);
        var saved = fixture.Store.Summary(id);
        Assert.Equal(GenerationState.Succeeded, saved.State); Assert.Equal(1, saved.PromptVersion);
        Assert.True(fixture.Http.Prompts.Count > 1);
        Assert.Contains("此处是中间整理", fixture.Http.Prompts[0]);
        Assert.DoesNotContain("通常写 2—4 句", fixture.Http.Prompts[0]);
        Assert.Contains(HourlySummaryService.Prompt, fixture.Http.Prompts[^1]);
        Assert.DoesNotContain("保留覆盖范围及不确定性", fixture.Http.Prompts[^1]);
        Assert.DoesNotContain(SummaryPrompts.Grounding, fixture.Http.Prompts[^1]);
        Assert.All(fixture.Http.Prompts, prompt => Assert.True(Encoding.UTF8.GetByteCount(prompt) + 512 <= 4000));
    }

    [Fact] public async Task LegacyHourlyRetryKeepsItsOriginalPromptPlan()
    {
        using var fixture = new Fixture(); fixture.Add(1);
        fixture.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var service = fixture.Service();
        var id = await service.Generate(Range(), "旧版时段摘要");
        fixture.Store.SaveSummary(fixture.Store.Summary(id) with { Hourly = true });
        var prompt = fixture.Http.Prompts.Single();
        fixture.Http.Respond = (_, _) => Task.FromResult(Reply("重试成功"));
        await service.Retry(id);
        Assert.Equal(prompt, fixture.Http.Prompts[^1]);
        Assert.Equal(GenerationState.Succeeded, fixture.Store.Summary(id).State);
    }

    [Fact] public void HistoryIncludesRecognitionOnlyDaysWithoutAddingSamplingToAppTime()
    {
        using var fixture = new Fixture(); fixture.Add(1); fixture.Add(2, seconds: 120);
        var day = Assert.Single(fixture.Store.Days());
        Assert.Equal("2026-09-11", day.Day); Assert.Equal(0, day.Seconds);
        Assert.Equal(2, day.RecordCount); Assert.Equal(180, day.SampleSeconds);
        fixture.Store.Save([new(Guid.NewGuid().ToString("N"), "session", "app", "Application", day.Day, Epoch, Epoch.AddSeconds(45), 45)], new(), new());
        fixture.Reopen(); day = Assert.Single(fixture.Store.Days());
        Assert.Equal(45, day.Seconds); Assert.Equal(180, day.SampleSeconds); Assert.Equal(2, day.RecordCount);
    }

    [Fact] public void HistoryDoesNotHideDaysOlderThanOneYear()
    {
        using var fixture = new Fixture();
        var slices = Enumerable.Range(0, 400).Select(index => {
            var start = Epoch.AddDays(-index);
            return new ActivitySlice(Guid.NewGuid().ToString("N"), "session", "app", "Application",
                start.ToString("yyyy-MM-dd"), start, start.AddSeconds(30), 30);
        }).ToArray();
        fixture.Store.Save(slices, new(), new()); fixture.Reopen();
        var days = fixture.Store.Days();
        Assert.Equal(400, days.Count);
        Assert.Equal(Epoch.ToString("yyyy-MM-dd"), days[0].Day);
        Assert.Equal(Epoch.AddDays(-399).ToString("yyyy-MM-dd"), days[^1].Day);
        Assert.Equal(12000, days.Sum(day => day.Seconds));
        Assert.Equal(30, Assert.Single(fixture.Store.Apps(days[^1].Day)).Seconds);
    }

    [Fact] public void TaskHealthPagesIncludeCleanupWithoutExposingRecordContent()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Store.LatestRecognizedSample());
        for (var minute = 0; minute < 55; minute++) fixture.Add(minute, "不应出现在任务健康列表的内容");
        var first = fixture.Store.UnfinishedJobs(0); var second = fixture.Store.UnfinishedJobs(50);
        Assert.Equal(55, first.Total); Assert.Equal(50, first.Items.Length); Assert.Equal(5, second.Items.Length);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
        Assert.Equal(Epoch.AddMinutes(54), first.Items[0].Utc);
        Assert.Equal(Epoch.AddMinutes(54), fixture.Store.LatestRecognizedSample());
        Assert.DoesNotContain("不应出现在", JsonSerializer.Serialize(first));
        var job = fixture.Store.Jobs(JobStatus.Succeeded)[0];
        fixture.Store.SaveJob(job with { CleanupPending = false });
        Assert.Equal(54, fixture.Store.UnfinishedJobs(0).Total);
        fixture.Store.SaveJob(job with { Status = JobStatus.Manual, CleanupPending = false, Error = "识别失败" });
        Assert.Equal(55, fixture.Store.UnfinishedJobs(0).Total);
        Assert.Contains(fixture.Store.UnfinishedJobs(50).Items, item => item.Error == "识别失败");
    }

    [Fact] public async Task RangeAndSourceSnapshotSurviveLateRecordsAndRestartWithoutKeys()
    {
        using var fixture = new Fixture(); fixture.Add(-1); var expected = fixture.Add(1); fixture.Add(60);
        var service = fixture.Service(); var id = await service.Generate(Range(), "回顾阅读情况");
        fixture.Add(2); fixture.Reopen(); var document = fixture.Store.Summary(id);
        Assert.Equal(GenerationState.Succeeded, document.State); Assert.Equal(expected, Assert.Single(document.Sources).Id);
        Assert.Equal("回顾阅读情况", document.Prompt); Assert.Equal("test", document.Model.Model);
        var serialized = JsonSerializer.Serialize(document);
        Assert.DoesNotContain("secret-not-exported", serialized); Assert.DoesNotContain("encrypted-not-exported", serialized);
        Assert.NotEmpty(document.Calls!);
    }

    [Fact] public async Task InvalidOrEmptyRangePreservesDraftAndDoesNotCallModel()
    {
        using var fixture = new Fixture(); var service = fixture.Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.Generate(Range(), "保留这个草稿"));
        Assert.Contains("保留这个草稿", fixture.Store.Read<JsonElement>("summary-draft").GetProperty("prompt").GetString());
        fixture.Add(1);
        await Assert.ThrowsAsync<ArgumentException>(() => service.Generate(Range(10, 0), "倒置范围"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Generate(Range(), " "));
        Assert.Empty(fixture.Http.Prompts); Assert.Empty(fixture.Store.Summaries());
    }

    [Fact] public async Task ConversationsAreIndependentAndPersistWithPresets()
    {
        using var fixture = new Fixture(); fixture.Add(1); var service = fixture.Service();
        var first = await service.Generate(Range(), "甲总结"); var second = await service.Generate(Range(), "乙总结");
        await service.Chat(first, "仅甲的问题"); await service.Chat(second, "仅乙的问题");
        Assert.DoesNotContain("仅甲的问题", fixture.Http.Prompts.Last());
        await service.Chat(first, "继续甲的对话");
        Assert.Contains("仅甲的问题", fixture.Http.Prompts.Last()); Assert.DoesNotContain("仅乙的问题", fixture.Http.Prompts.Last());
        service.SavePresets([new("mine", "自定义", "原始预设")]); fixture.Reopen();
        Assert.Equal(2, fixture.Store.Summary(first).Conversation!.Length);
        Assert.Single(fixture.Store.Summary(second).Conversation!);
        Assert.Equal("原始预设", fixture.Store.Read<PromptPreset[]>("summary-presets")!.Single().Prompt);
        var reopened = fixture.Service();
        reopened.SavePresets([new("mine", "修改名称", "修改后的提示词")]);
        reopened.SavePresets([]); fixture.Reopen();
        Assert.Empty(fixture.Store.Read<PromptPreset[]>("summary-presets")!);
        Assert.Equal("甲总结", fixture.Store.Summary(first).Prompt);
        Assert.Equal(2, fixture.Store.Summary(first).Conversation!.Length);
    }

    [Fact] public async Task ChatDraftsSurviveRestartFailureAndDoNotEraseNewerInput()
    {
        using var fixture = new Fixture(); fixture.Add(1); var service = fixture.Service();
        var first = await service.Generate(Range(), "甲"); var second = await service.Generate(Range(), "乙");
        fixture.Store.SaveChatDraft(first, "甲的问题"); fixture.Store.SaveChatDraft(second, "乙的草稿");
        fixture.Reopen(); service = fixture.Service();
        Assert.Equal("甲的问题", fixture.Store.Summary(first).ChatDraft);
        fixture.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await service.Chat(first, "甲的问题");
        Assert.Equal("甲的问题", fixture.Store.Summary(first).ChatDraft);
        fixture.Http.Respond = (_, _) => { fixture.Store.SaveChatDraft(first, "新输入"); return Task.FromResult(Reply("回答")); };
        await service.Chat(first, "甲的问题");
        Assert.Equal("新输入", fixture.Store.Summary(first).ChatDraft);
        fixture.Http.Respond = (_, _) => Task.FromResult(Reply("回答"));
        await service.Chat(first, "新输入"); fixture.Reopen();
        Assert.Equal("", fixture.Store.Summary(first).ChatDraft);
        Assert.Equal("乙的草稿", fixture.Store.Summary(second).ChatDraft);
    }

    [Fact] public async Task PartialBatchFailureRetriesOnlyUnfinishedCallsWithoutTruncation()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 10; index++) fixture.Add(index, new string('文', 150) + "终点" + index);
        fixture.Http.Respond = (index, _) => Task.FromResult(index == 2 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Reply("本批记录了阅读活动。"));
        var service = fixture.Service(); var id = await service.Generate(Range(), "完整回顾");
        var failed = fixture.Store.Summary(id); Assert.Equal(GenerationState.Failed, failed.State);
        Assert.Null(failed.Text); Assert.Single(failed.Calls!, call => call.Result is not null);
        var firstPrompt = fixture.Http.Prompts[0]; await service.Retry(id);
        var completed = fixture.Store.Summary(id); Assert.Equal(GenerationState.Succeeded, completed.State);
        Assert.Equal(1, fixture.Http.Prompts.Count(prompt => prompt == firstPrompt));
        Assert.All(fixture.Http.Prompts, prompt => Assert.True(Encoding.UTF8.GetByteCount(prompt) + 512 <= 4000));
        Assert.Equal(10, completed.Sources.Length);
        Assert.Contains("终点9", string.Join("", completed.Calls!.Where(call => call.Id.StartsWith("0:")).Select(call => call.Prompt)));
    }

    [Fact] public async Task MergeFailureResumesAfterRestartFromFrozenBatchesAndRejectsModelChanges()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 10; index++) fixture.Add(index, new string('文', 150) + "记录" + index);
        fixture.Http.Respond = (_, _) => Task.FromResult(fixture.Http.Prompts.Last().Contains("合并各批摘要")
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Reply("本批观察摘要"));
        var service = fixture.Service(); var id = await service.Generate(Range(), "完整回顾");
        var failed = fixture.Store.Summary(id);
        Assert.Equal(GenerationState.Failed, failed.State); Assert.Null(failed.Text);
        var successful = failed.Calls!.Where(call => call.Result is not null).ToArray();
        Assert.True(successful.Length > 1); Assert.All(successful, call => Assert.StartsWith("0:", call.Id));
        Assert.Contains(failed.Calls!, call => call.Id == "1:0" && call.Result is null);
        fixture.Reopen(); fixture.Add(30, "失败后才到达的新记录");
        var changed = new SummaryService(fixture.Store, new(new HttpClient(fixture.Http)),
            () => (fixture.Config with { Model = "another-model" }, "test-key"));
        var before = fixture.Http.Prompts.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => changed.Retry(id));
        Assert.Equal(before, fixture.Http.Prompts.Count);
        service = fixture.Service(); fixture.Http.Respond = (_, _) => Task.FromResult(Reply("完整合并结果"));
        await service.Retry(id); fixture.Reopen();
        var completed = fixture.Store.Summary(id);
        Assert.Equal(GenerationState.Succeeded, completed.State); Assert.Equal("完整合并结果", completed.Text);
        Assert.Equal(before + 1, fixture.Http.Prompts.Count);
        Assert.Equal(failed.DataCutoff, completed.DataCutoff); Assert.Equal(failed.Sources, completed.Sources);
        Assert.DoesNotContain("失败后才到达的新记录", string.Join("\n", fixture.Http.Prompts));
        foreach (var call in successful)
        {
            Assert.Equal(call, completed.Calls!.Single(item => item.Id == call.Id));
            Assert.Equal(1, fixture.Http.Prompts.Count(prompt => prompt == call.Prompt));
        }
    }

    [Fact] public async Task DraftUpdatePreservesEvidenceConversationAndDoesNotRewriteHeaders()
    {
        using var fixture = new Fixture(); fixture.Add(1); var service = fixture.Service();
        var id = await service.Generate(Range(), "原始提示词"); await service.Chat(id, "已发送问题");
        var before = fixture.Store.Summary(id);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Store.Path }.ToString());
        connection.Open();
        using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = "CREATE TRIGGER reject_header_update BEFORE UPDATE ON summary_headers BEGIN SELECT RAISE(FAIL,'header must remain unchanged'); END";
            trigger.ExecuteNonQuery();
        }
        const string draft = "草稿\n\"引号\"与\\反斜线 <script>文本</script>";
        fixture.Store.SaveChatDraft(id, draft);
        var after = fixture.Store.Summary(id);
        Assert.Equal(draft, after.ChatDraft);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after with { ChatDraft = before.ChatDraft }));
        Assert.Throws<ArgumentException>(() => fixture.Store.SaveChatDraft("missing", "未写入"));
        Assert.Throws<ArgumentException>(() => fixture.Store.SaveChatDraft(id, new string('字', 10001)));
        Assert.Equal(draft, fixture.Store.Summary(id).ChatDraft);
    }

    [Fact] public async Task CancelledGenerationIsNotSuccessfulAndCanResume()
    {
        using var fixture = new Fixture(); fixture.Add(1); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Respond = async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return Reply("不应保存"); };
        var service = fixture.Service(); var pending = service.Generate(Range(), "可以取消");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); service.Cancel(); var id = await pending;
        Assert.Equal(GenerationState.Cancelled, fixture.Store.Summary(id).State); Assert.Null(fixture.Store.Summary(id).Text);
        fixture.Http.Respond = (_, _) => Task.FromResult(Reply("恢复完成")); await service.Retry(id);
        Assert.Equal("恢复完成", fixture.Store.Summary(id).Text);
    }

    [Fact] public async Task FailedChatKeepsQuestionAndRetryDoesNotDuplicateTurn()
    {
        using var fixture = new Fixture(); fixture.Add(1); var service = fixture.Service(); var id = await service.Generate(Range(), "回顾");
        fixture.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await service.Chat(id, "保留问题"); var turn = Assert.Single(fixture.Store.Summary(id).Conversation!);
        Assert.Equal(GenerationState.Failed, turn.State); Assert.Null(turn.Reply);
        fixture.Http.Respond = (_, _) => Task.FromResult(Reply("重试回复")); await service.Chat(id, "", turn.Id);
        var recovered = Assert.Single(fixture.Store.Summary(id).Conversation!);
        Assert.Equal("保留问题", recovered.Question); Assert.Equal("重试回复", recovered.Reply);
    }

    [Fact] public async Task InterruptedSummaryAndChatBecomeRecoverableAfterRestart()
    {
        using var fixture = new Fixture(); fixture.Add(1); var service = fixture.Service(); var id = await service.Generate(Range(), "重启验证");
        var saved = fixture.Store.Summary(id);
        fixture.Store.SaveSummary(saved with { State = GenerationState.Running, Text = null });
        fixture.Reopen(); service = fixture.Service();
        Assert.Equal(GenerationState.Failed, fixture.Store.Summary(id).State);
        var callsBefore = fixture.Http.Prompts.Count; await service.Retry(id);
        Assert.Equal(callsBefore, fixture.Http.Prompts.Count);
        saved = fixture.Store.Summary(id);
        fixture.Store.SaveSummary(saved with { Conversation = [new("interrupted", Epoch, "未完成问题", "当时提示词", saved.Model)] });
        fixture.Reopen(); _ = fixture.Service();
        var recovered = Assert.Single(fixture.Store.Summary(id).Conversation!);
        Assert.Equal(GenerationState.Failed, recovered.State); Assert.Equal("未完成问题", recovered.Question); Assert.Null(recovered.Reply);
    }
}
