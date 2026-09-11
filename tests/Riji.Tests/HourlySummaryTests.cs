using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Riji.Core;
using Riji.Infrastructure;
using Xunit;

namespace Riji.Tests;

public sealed class HourlySummaryTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-11T00:00:00Z");
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public Func<int, CancellationToken, Task<HttpResponseMessage>>? Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var count = ++Calls;
            return Respond?.Invoke(count, token) ?? Task.FromResult(Reply("摘要 " + count));
        }
    }
    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = text } } } }))
    };
    private sealed class Fixture : IDisposable
    {
        public readonly string Folder = Path.Combine(Path.GetTempPath(), "RijiTests", Guid.NewGuid().ToString("N"));
        public LocalStore Store;
        public readonly Handler Handler = new();
        public SummaryService Summary = null!;
        public HourlySummaryService Hourly = null!;
        public bool Allowed = true;
        public Fixture() { Store = new(Path.Combine(Folder, "test.db")); Services(Epoch); }
        public void Services(DateTimeOffset now)
        {
            Summary = new(Store, new(new HttpClient(Handler)), () => (new("https://example.com/v1", "test", "encrypted", 4000), "test-secret"));
            Hourly = new(Store, Summary, TimeZoneInfo.Utc, now, () => Allowed);
        }
        public void Add(int minute)
        {
            var utc = Epoch.AddMinutes(minute); var id = Guid.NewGuid().ToString("N");
            var job = new RecognitionJob(id, utc, utc.ToString("yyyy-MM-dd"), 60, id + ".png", Category.Defaults);
            Store.SaveJob(job); Store.Complete(job, new("阅读资料 " + minute, "learning", 0.9));
        }
        public void Reopen(DateTimeOffset now) { Store.Dispose(); Store = new(Path.Combine(Folder, "test.db")); Services(now); }
        public void Dispose() { Store.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(Folder, true); }
    }

    [Fact] public async Task ExactHourRegeneratesAfterManualUsingFreshEvidence()
    {
        using var f = new Fixture(); f.Add(1);
        var manual = await f.Hourly.Generate(Epoch.AddMinutes(30)); f.Add(55);
        await f.Hourly.Pulse(Epoch.AddMinutes(59).AddSeconds(59));
        Assert.Equal(1, f.Handler.Calls);
        await f.Hourly.Pulse(Epoch.AddHours(1));
        Assert.Equal(2, f.Handler.Calls);
        var latest = f.Store.SummaryHeaders()[0];
        Assert.True(latest.Hourly); Assert.True(latest.Automatic); Assert.Equal("摘要 2", latest.Text);
        Assert.Equal(2, latest.SourceCount); Assert.Single(f.Store.Summary(manual).Sources);
        Assert.Null(f.Store.Read<SummaryForm>("summary-form"));
        Assert.Null(f.Store.Read<object>("summary-draft"));
    }

    [Fact] public async Task AutomaticWorkWaitsForManualRequestThenStillOverrides()
    {
        using var f = new Fixture(); f.Add(1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.Respond = async (n, token) => { if (n == 1) await release.Task.WaitAsync(token); return Reply("摘要 " + n); };
        var manual = f.Hourly.Generate(Epoch);
        Assert.True(f.Summary.Busy);
        await f.Hourly.Pulse(Epoch.AddHours(1));
        Assert.Equal(1, f.Handler.Calls);
        release.SetResult(); await manual;
        await f.Hourly.Pulse(Epoch.AddHours(1).AddSeconds(2));
        Assert.Equal(2, f.Handler.Calls); Assert.True(f.Store.SummaryHeaders()[0].Automatic);
    }

    [Fact] public async Task FailedAutomaticAttemptPreservesSuccessfulManualAndDoesNotLoop()
    {
        using var f = new Fixture(); f.Add(1);
        var manual = await f.Hourly.Generate(Epoch);
        f.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await f.Hourly.Pulse(Epoch.AddHours(1)); await f.Hourly.Pulse(Epoch.AddHours(1).AddSeconds(1));
        Assert.Equal(2, f.Handler.Calls);
        Assert.Equal(GenerationState.Failed, f.Store.SummaryHeaders()[0].State);
        Assert.Equal("摘要 1", f.Store.Summary(manual).Text);
        Assert.Equal(GenerationState.Succeeded, f.Store.Summary(manual).State);
    }

    [Fact] public async Task SleepCatchupIncludesMidnightAndSkipsEmptyHours()
    {
        using var f = new Fixture(); f.Add(23 * 60 + 59);
        await f.Hourly.Pulse(Epoch.AddDays(1).AddSeconds(5));
        var summary = Assert.Single(f.Store.SummaryHeaders());
        Assert.Equal(Epoch.AddHours(23), summary.Range.Start);
        Assert.Equal(Epoch.AddDays(1), summary.Range.End); Assert.Equal(1, f.Handler.Calls);
    }

    [Fact] public async Task RestartAndClockRollbackDoNotRegenerateCompletedHour()
    {
        using var f = new Fixture(); f.Add(1); await f.Hourly.Pulse(Epoch.AddHours(1));
        f.Reopen(Epoch.AddHours(1).AddMinutes(5));
        await f.Hourly.Pulse(Epoch.AddMinutes(50));
        await f.Hourly.Pulse(Epoch.AddHours(1).AddMinutes(10));
        Assert.Equal(1, f.Handler.Calls); Assert.Single(f.Store.SummaryHeaders());
        Assert.Equal("摘要 1", f.Store.SummaryHeaders()[0].Text);
    }

    [Fact] public async Task UnconfiguredQueueWaitsAndResumesWithoutLosingTheHour()
    {
        using var f = new Fixture(); f.Add(1); f.Allowed = false;
        await f.Hourly.Pulse(Epoch.AddHours(1)); Assert.Equal(0, f.Handler.Calls);
        f.Allowed = true; await f.Hourly.Pulse(Epoch.AddHours(1).AddMinutes(5));
        Assert.Equal(1, f.Handler.Calls);
    }

    [Fact] public async Task CancellationQuiescesSchedulerBeforeStoreIsClosed()
    {
        using var f = new Fixture(); f.Add(1);
        f.Handler.Respond = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Reply("unreachable"); };
        var pulse = f.Hourly.Pulse(Epoch.AddHours(1));
        await f.Hourly.Shutdown(); await pulse;
        Assert.False(f.Summary.Busy);
        Assert.Equal(GenerationState.Cancelled, Assert.Single(f.Store.SummaryHeaders()).State);
    }

    [Fact] public void HourBoundaryUsesLocalZoneIncludingFractionalOffsets()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-half", TimeSpan.FromHours(5.5), "test", "test");
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T05:00:00+05:30"), HourlySummaryService.HourStart(Epoch, zone));
    }

    [Fact] public async Task BackupReplacementPreservesHourlyTextAndResetsSchedulerCursor()
    {
        using var f = new Fixture(); f.Add(1); await f.Hourly.Pulse(Epoch.AddHours(1));
        var data = f.Store.ExportData();
        var root = "Screenshots-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(f.Folder, root));
        f.Store.ReplaceData(data, root);
        var header = Assert.Single(f.Store.SummaryHeaders());
        Assert.True(header.Hourly); Assert.True(header.Automatic); Assert.Equal("摘要 1", header.Text);
        Assert.Null(f.Store.Read<DateTimeOffset?>(HourlySummaryService.CursorKey));
    }
}
