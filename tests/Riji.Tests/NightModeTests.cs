using Microsoft.Data.Sqlite;
using Riji.Core;
using Riji.Infrastructure;
using Xunit;

namespace Riji.Tests;

public sealed class NightModeTests
{
    [Fact]
    public void BoundaryAssignsEarlyMorningToPreviousDay()
    {
        var settings = new TrackingSettings(NightMode: true, DayStartHour: 5);
        Assert.Equal("2026-09-22", DayRange.Today(DateTimeOffset.Parse("2026-09-23T04:59:59Z"), settings, TimeZoneInfo.Utc));
        Assert.Equal("2026-09-23", DayRange.Today(DateTimeOffset.Parse("2026-09-23T05:00:00Z"), settings, TimeZoneInfo.Utc));
        var (start, end) = DayRange.Bounds("2026-09-22", settings, TimeZoneInfo.Utc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-22T05:00:00Z"), start);
        Assert.Equal(DateTimeOffset.Parse("2026-09-23T05:00:00Z"), end);
        Assert.Throws<ArgumentException>(() => Tracker.ValidateSettings(settings with { DayStartHour = 10 }));
    }

    [Fact]
    public void HistoryIsReclassifiedWithoutChangingStoredTimes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RijiTests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = new LocalStore(Path.Combine(folder, "night.db")))
            {
                var from = DateTimeOffset.Parse("2026-09-23T04:50:00Z");
                var to = DateTimeOffset.Parse("2026-09-23T05:10:00Z");
                var slice = new ActivitySlice("slice", "session", "browser", "浏览器", "2026-09-23", from, to, 1200);
                var site = new WebsiteSlice("site", slice.Id, "2026-09-23", "example.com", "页面", 1200);
                store.Save([slice], new(), new(), [site]);
                foreach (var (id, utc) in new[] { ("before", from.AddMinutes(9)), ("after", from.AddMinutes(10)) })
                {
                    var job = new RecognitionJob(id, utc, "2026-09-23", 60, id + ".png", Category.Defaults);
                    store.SaveJob(job);
                    store.Complete(job, new("测试", Category.Defaults[0].Id, 0.9));
                }

                var night = new TrackingSettings(NightMode: true, DayStartHour: 5);
                var yesterday = store.ViewDay("2026-09-22", night, TimeZoneInfo.Utc);
                var today = store.ViewDay("2026-09-23", night, TimeZoneInfo.Utc);
                Assert.Equal(600, yesterday.Apps.Single().Seconds);
                Assert.Equal(600, today.Apps.Single().Seconds);
                Assert.Equal(600, yesterday.Websites.Single().Seconds);
                Assert.Equal(600, today.Websites.Single().Seconds);
                Assert.Equal("before", yesterday.Records.Single().Id);
                Assert.Equal("after", today.Records.Single().Id);
                Assert.Equal(new[] { ("2026-09-23", 600d, 1), ("2026-09-22", 600d, 1) },
                    store.Days(night, TimeZoneInfo.Utc).Select(x => (x.Day, x.Seconds, x.RecordCount)));

                var natural = store.ViewDay("2026-09-23", new(), TimeZoneInfo.Utc);
                Assert.Equal(1200, natural.Apps.Single().Seconds);
                Assert.Equal(2, natural.Records.Count);
                Assert.Equal("2026-09-23", store.ExportData().Activities.Single().Day);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
