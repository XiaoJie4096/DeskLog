using Microsoft.Data.Sqlite;
using Riji.Core;
using Riji.Infrastructure;
using Xunit;

namespace Riji.Tests;

public sealed class WebsiteAttributionTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-12T23:59:58Z");
    private static Observation At(double second, string browser = "chrome", string? domain = "www.bilibili.com", double? expiry = null) =>
        new(Epoch.AddSeconds(second), second, new(browser, browser), second, Website: domain is null ? null : new(domain, "测试标题", expiry ?? second + 1.5));

    [Fact] public void ExpirySplitsOwnerAndMidnightWithoutDuplicatingTime()
    {
        var tracker = new Tracker(new(WebsiteTitles: true), new(), TimeZoneInfo.Utc);
        tracker.Observe(At(0, expiry: 1.5)); tracker.Observe(At(3, domain: null));
        Assert.Equal(3, tracker.Pending.Sum(a => a.Seconds));
        Assert.Equal(1.5, tracker.Pending.Where(a => a.AppName == "B站").Sum(a => a.Seconds));
        Assert.Equal(1.5, tracker.Pending.Where(a => a.AppName == "chrome").Sum(a => a.Seconds));
        Assert.Equal(2, tracker.Pending.Where(a => a.Day == "2026-09-12").Sum(a => a.Seconds));
        Assert.Equal(1, tracker.Pending.Where(a => a.Day == "2026-09-13").Sum(a => a.Seconds));
        Assert.Equal(1.5, tracker.PendingWebsites.Sum(a => a.Seconds));
        Assert.All(tracker.Pending, a => Assert.Equal("chrome", a.SourceAppName));
    }

    [Fact] public void RenameDeleteAndEmptyRulesOnlyAffectFutureIntervals()
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        tracker.Observe(At(0));
        tracker.Configure(tracker.Settings with { WebsiteProjects = [new("bilibili", "学习视频", ["bilibili.com"])] }, At(1));
        tracker.Configure(tracker.Settings with { WebsiteProjects = [] }, At(2));
        tracker.Observe(At(3));
        Assert.Equal(new[] { "B站", "学习视频", "chrome" }, tracker.Pending.Select(a => a.AppName));
        Assert.All(tracker.Pending, a => Assert.Equal(1, a.Seconds));
        Assert.Empty(tracker.Settings.WebsiteProjects!);
    }

    [Fact] public void FractionalExpiryDoesNotStallOrLoseTheRemainingInterval()
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        tracker.Observe(At(0, expiry: 0.1)); tracker.Observe(At(1));
        Assert.Equal(1, tracker.Pending.Sum(a => a.Seconds), 8);
        Assert.Equal(0.1, tracker.Pending.Where(a => a.AppName == "B站").Sum(a => a.Seconds), 8);
        Assert.Equal(0.9, tracker.Pending.Where(a => a.AppName == "chrome").Sum(a => a.Seconds), 8);
    }

    [Fact] public void AllBrowsersShareOwnerAndPersistenceKeepsSourceAndNameSnapshots()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RijiAttribution", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "test.db");
        try
        {
            var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
            tracker.Observe(At(10, "chrome")); tracker.Observe(At(11, "msedge"));
            tracker.Observe(At(12, "firefox")); tracker.Observe(At(13, domain: null));
            using (var store = new LocalStore(path))
            {
                store.Save(tracker.Pending, tracker.Settings, tracker.State, tracker.PendingWebsites);
                store.Save(tracker.Pending, tracker.Settings, tracker.State, tracker.PendingWebsites);
            }
            using var reopened = new LocalStore(path);
            var app = Assert.Single(reopened.Apps("2026-09-13"));
            Assert.Equal("B站", app.Name); Assert.Equal(3, app.Seconds);
            Assert.Equal(new[] { "chrome", "firefox", "msedge" }, reopened.Websites("2026-09-13").Select(w => w.SourceAppName).Order());
            var data = reopened.ExportData(); data.Validate();
            Assert.Equal(3, data.Format);
            Assert.Throws<ArgumentException>(() => (data with { Format = 2 }).Validate());
            var imageRoot = "Screenshots-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(folder, imageRoot));
            reopened.ReplaceData(data, imageRoot);
            Assert.Equal(data.Activities, reopened.ExportData().Activities);
            Assert.Equal(3, Assert.Single(reopened.Apps("2026-09-13")).Seconds);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Fact] public void SpecificDomainsWinAndUnmatchedWebsitesRemainBrowserTime()
    {
        WebsiteProject[] projects = [new("a", "站点", ["example.com"]), new("b", "文档", ["docs.example.com"])];
        WebsiteProject.Validate(projects);
        Assert.Equal("b", WebsiteProject.Resolve("docs.example.com", projects)!.Id);
        Assert.Null(WebsiteProject.Resolve("evil-example.com", projects));
        Assert.Throws<ArgumentException>(() => WebsiteProject.Validate([projects[0], new("c", "重复", ["example.com"])]));
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        tracker.Observe(At(0, domain: "unknown.example")); tracker.Observe(At(1));
        Assert.Equal("chrome", Assert.Single(tracker.Pending).AppName);
    }
}
