using Riji.Core;
using Xunit;

namespace Riji.Tests;

public sealed class WebsiteTests
{
    [Theory]
    [InlineData("example.com?token=secret")]
    [InlineData("https://example.com")]
    [InlineData("user@example.com")]
    [InlineData("example.com/path")]
    [InlineData("example.com#private")]
    public void SensitiveOrNonDomainInputIsRejected(string input) => Assert.Null(WebsitePrivacy.NormalizeDomain(input));

    [Fact] public void DomainRulesRespectLabelBoundaries()
    {
        Assert.True(WebsitePrivacy.Matches("docs.example.com", "example.com"));
        Assert.False(WebsitePrivacy.Matches("evil-example.com", "example.com"));
        Assert.False(WebsitePrivacy.Matches("example.com.evil.test", "example.com"));
        WebsiteRule[] rules = [new("example.com", false), new("docs.example.com", true)];
        WebsitePrivacy.ValidateRules(rules);
        Assert.False(WebsitePrivacy.Allowed("private.example.com", rules));
        Assert.True(WebsitePrivacy.Allowed("docs.example.com", rules));
        Assert.True(WebsitePrivacy.Allowed("sub.docs.example.com", rules));
        Assert.True(WebsitePrivacy.Allowed("evil-example.com", rules));
        Assert.Throws<ArgumentException>(() => WebsitePrivacy.ValidateRules([new("https://example.com", false)]));
        Assert.Throws<ArgumentException>(() => WebsitePrivacy.ValidateRules([new("example.com", false), new("example.com", true)]));
    }

    [Fact] public void WebsiteTimeIsOnlyTheFreshSubsetOfForegroundTime()
    {
        var t = new Tracker(new(), new(), TimeZoneInfo.Utc);
        var epoch = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
        for (var i = 0; i <= 10; i++)
            t.Observe(new(epoch.AddSeconds(i), i, new("browser", "browser"), i, Website: new("example.com", null, 3.5)));
        Assert.Equal(10, t.Pending.Sum(x => x.Seconds));
        Assert.Equal(3.5, t.PendingWebsites.Sum(x => x.Seconds));
        Assert.All(t.PendingWebsites, x => Assert.Null(x.Title));
    }

    [Fact] public void LegacyBlockingRulesNoLongerSuppressWebsiteEvidence()
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        var epoch = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
        Observation At(int second, string domain = "example.com") => new(epoch.AddSeconds(second), second,
            new("chrome", "chrome"), second, Website: new(domain, null, second + 2));
        for (var second = 0; second <= 5; second++) tracker.Observe(At(second));
        tracker.Configure(tracker.Settings with { WebsiteRules = [new("example.com", false), new("docs.example.com", true)] }, At(5));
        for (var second = 6; second <= 10; second++) tracker.Observe(At(second));
        tracker.Observe(At(10, "docs.example.com"));
        for (var second = 11; second <= 15; second++) tracker.Observe(At(second, "docs.example.com"));
        Assert.Equal(15, tracker.Pending.Sum(slice => slice.Seconds));
        Assert.Equal(10, tracker.PendingWebsites.Where(slice => slice.Domain == "example.com").Sum(slice => slice.Seconds));
        Assert.Equal(5, tracker.PendingWebsites.Where(slice => slice.Domain == "docs.example.com").Sum(slice => slice.Seconds));
        Assert.Equal(15, tracker.PendingWebsites.Sum(slice => slice.Seconds));
        foreach (var parent in tracker.Pending)
            Assert.True(tracker.PendingWebsites.Where(site => site.ParentId == parent.Id).Sum(site => site.Seconds) <= parent.Seconds);
    }

    [Fact] public void SiteChangeAndLossOfForegroundNeverDoubleCount()
    {
        var t = new Tracker(new(), new(), TimeZoneInfo.Utc);
        var epoch = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
        for (var i = 0; i <= 10; i++)
            t.Observe(new(epoch.AddSeconds(i), i, new(i < 6 ? "browser" : "editor", "app"), i,
                Website: i < 6 ? new(i < 3 ? "a.com" : "b.com", null, i + 2) : null));
        Assert.Equal(10, t.Pending.Sum(x => x.Seconds));
        Assert.Equal(6, t.PendingWebsites.Sum(x => x.Seconds));
        Assert.Equal(3, t.PendingWebsites.Where(x => x.Domain == "a.com").Sum(x => x.Seconds));
        Assert.Equal(3, t.PendingWebsites.Where(x => x.Domain == "b.com").Sum(x => x.Seconds));
    }
}
