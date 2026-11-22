using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Riji.Core;
using Riji.Infrastructure;
using Xunit;

namespace Riji.Tests;

public sealed class CategoryReviewTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    // Each trigger uses its own cutoff and evaluates the threshold per category.
    public void ScheduledAndFinalReviewsUseTheRecordsAvailableAtEachCutoff()
    {
        using var f = new Fixture();
        f.Pulse(Day.AddHours(21));
        f.Add("development", 18, 30);
        f.Add("learning", 19, 29);
        f.Add("entertainment", 20, 29);
        Assert.Empty(f.Store.SummaryHeaders());

        f.Pulse(Day.AddHours(22));
        var scheduled = Assert.Single(f.Store.SummaryHeaders());
        Assert.Equal(CategoryReviewStage.Scheduled, scheduled.CategoryReview?.Stage);
        Assert.Equal("development", scheduled.CategoryReview?.CategoryId);
        Assert.Equal(Day.AddHours(22), scheduled.Range.End);

        f.Add("learning", 22, 1);
        f.Add("creation", 24, 30);
        f.Pulse(Day.AddHours(23));
        Assert.Single(f.Store.SummaryHeaders());
        f.Pulse(Day.AddDays(1));
        var finals = f.Store.SummaryHeaders();
        Assert.Equal(2, finals.Count);
        Assert.All(finals, result => Assert.Equal(CategoryReviewStage.Final, result.CategoryReview?.Stage));
        Assert.All(finals, result => Assert.Equal(Day.AddDays(1), result.Range.End));
        Assert.Equal(scheduled.Id, finals.Single(item => item.CategoryReview?.CategoryId == "development").Id);
        var learning = f.Store.Summary(finals.Single(item => item.CategoryReview?.CategoryId == "learning").Id);
        Assert.Equal(30 * 60, learning.Sources.Sum(record => record.Seconds));
        Assert.All(learning.Sources, record => Assert.Equal("learning", record.Category.Id));
        Assert.Equal(2, f.Provider.Calls);
    }

    [Fact]
    // Missed scheduled work becomes a single full-day review upon restart.
    public void RestartAfterNightOwlBoundaryRunsOnlyFinalForTheClosedDay()
    {
        using var f = new Fixture();
        f.Settings = new(NightMode: true, DayStartHour: 5, CategoryReviewMinute: 2 * 60 + 30);
        f.Add("development", 26, 30);
        f.Add("development", 28, 5);
        f.Pulse(Day.AddHours(26.25));
        Assert.Empty(f.Store.SummaryHeaders());
        f.Restart();
        f.Pulse(Day.AddHours(30));
        var result = Assert.Single(f.Store.SummaryHeaders());
        Assert.Equal("2026-11-20", result.CategoryReview?.Day);
        Assert.Equal(CategoryReviewStage.Final, result.CategoryReview?.Stage);
        Assert.Equal(Day.AddHours(5), result.Range.Start);
        Assert.Equal(Day.AddHours(29), result.Range.End);
        Assert.Equal(35 * 60, f.Store.Summary(result.Id).Sources.Sum(record => record.Seconds));
        f.Restart();
        f.Pulse(Day.AddHours(30.5));
        Assert.Equal(1, f.Provider.Calls);
    }

    [Fact]
    // An after-midnight schedule belongs to the previous night owl review day.
    public void NightOwlScheduleUsesTheClockTimeOnTheFollowingCalendarDate()
    {
        using var f = new Fixture();
        f.Settings = new(NightMode: true, DayStartHour: 5, CategoryReviewMinute: 2 * 60 + 30);
        f.Add("learning", 25, 30);
        f.Pulse(Day.AddHours(26).AddMinutes(29));
        Assert.Empty(f.Store.SummaryHeaders());
        f.Pulse(Day.AddHours(26).AddMinutes(30));
        var result = Assert.Single(f.Store.SummaryHeaders());
        Assert.Equal("2026-11-20", result.CategoryReview?.Day);
        Assert.Equal(CategoryReviewStage.Scheduled, result.CategoryReview?.Stage);
        Assert.Equal(Day.AddHours(26).AddMinutes(30), result.Range.End);
    }

    [Fact]
    // Turning off automatic reviews leaves below-threshold manual requests available.
    public void ManualReviewWorksWhenDisabledAndBelowTheAutomaticThreshold()
    {
        using var f = new Fixture();
        f.Settings = new(CategoryReviewEnabled: false);
        f.Add("development", 18, 30);
        f.Add("learning", 20, 1);
        f.Pulse(Day.AddHours(22));
        f.Pulse(Day.AddHours(25));
        Assert.Empty(f.Store.SummaryHeaders());
        var id = f.Reviews.QueueGenerate("2026-11-20", "learning", Day.AddHours(25), f.Settings, TimeZoneInfo.Utc);
        var result = f.Store.Summary(id);
        Assert.Equal(GenerationState.Succeeded, result.State);
        Assert.Equal(CategoryReviewStage.Manual, result.CategoryReview?.Stage);
        Assert.False(result.Automatic);
        Assert.Equal("learning", Assert.Single(result.Sources).Category.Id);
        Assert.Equal(1, f.Provider.Calls);
    }

    [Fact]
    // A changed category gets a fresh final version and survives backup replacement.
    public void LateActivitiesRefreshTheFinalSummaryAndPersistThroughImport()
    {
        using var f = new Fixture();
        f.Add("development", 18, 30);
        f.Pulse(Day.AddHours(22));
        f.Add("development", 23, 5);
        f.Pulse(Day.AddHours(24));
        var result = f.Store.SummaryHeaders().Single(item => item.CategoryReview?.Stage == CategoryReviewStage.Final);
        Assert.Equal(GenerationState.Succeeded, result.State);
        Assert.Equal(35 * 60, f.Store.Summary(result.Id).Sources.Sum(record => record.Seconds));
        Assert.Equal(2, f.Provider.Calls);

        var backup = f.Store.ExportData();
        var imageRoot = "Screenshots-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(f.Folder, imageRoot));
        f.Store.ReplaceData(backup, imageRoot);
        var imported = f.Store.SummaryHeaders().Single(item => item.Id == result.Id);
        Assert.Equal(result.CategoryReview, imported.CategoryReview);
        Assert.Equal(result.Text, imported.Text);
        Assert.Null(f.Store.Read<object>(CategoryReviewService.CursorKey));
    }

    // Provide a deterministic model response without sending activity to a real provider.
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;

        // Return a distinct result for each model request.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var message = new { content = $"分类摘要 {++Calls}" };
            var reply = new { choices = new[] { new { finish_reason = "stop", message } } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(reply)) });
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Folder = Path.Combine(Path.GetTempPath(), "RijiTests", Guid.NewGuid().ToString("N"));
        public readonly Handler Provider = new();
        public TrackingSettings Settings = new();
        public LocalStore Store = null!;
        public CategoryReviewService Reviews = null!;

        // Open an isolated database for scheduler integration tests.
        public Fixture() => Open();

        // Recreate the service with the same persisted database and provider.
        private void Open()
        {
            Store = new(Path.Combine(Folder, "test.db"));
            var config = new AiConfiguration("https://example.com/v1", "test", "encrypted", 100000);
            var summaries = new SummaryService(Store, new(new HttpClient(Provider, disposeHandler: false)), () => (config, "test-secret"));
            Reviews = new(Store, summaries, () => true);
        }

        // Run the scheduler with the configured review day and time.
        public void Pulse(DateTimeOffset now) => Reviews.Pulse(now, Settings, TimeZoneInfo.Utc);

        // Add successful samples with the same weights used by the recognition pipeline.
        public void Add(string categoryId, int hour, int minutes)
        {
            for (var minute = 0; minute < minutes; minute++)
            {
                var utc = Day.AddHours(hour).AddMinutes(minute);
                var id = Guid.NewGuid().ToString("N");
                var job = new RecognitionJob(id, utc, DayRange.Today(utc, Settings, TimeZoneInfo.Utc), 60, id + ".png", Category.Defaults);
                Store.SaveJob(job);
                Store.Complete(job, new("阅读资料并整理项目记录", categoryId, 0.9));
            }
        }

        // Reopen the database to reset only the in-memory scheduler state.
        public void Restart()
        {
            Store.Dispose();
            Open();
        }

        // Release the isolated database and remove only its test folder.
        public void Dispose()
        {
            Store.Dispose();
            Provider.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Folder, true);
        }
    }
}
