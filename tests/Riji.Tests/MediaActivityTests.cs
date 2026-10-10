using System.Text.Json;
using Riji.Core;
using Xunit;

namespace Riji.Tests;

public sealed class MediaActivityTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-10-10T00:00:00Z");

    // Keep real input and media confirmations independent in all tracking scenarios.
    private static Observation At(double seconds, double input = 0, double? media = null) =>
        new(Epoch.AddSeconds(seconds), seconds, new("chrome", "chrome"), input, MediaActivitySeconds: media);

    [Theory]
    [InlineData(RecordingMode.Default)]
    [InlineData(RecordingMode.NoScreen)]
    // Periodic media confirmations keep both normal modes active through extended inactivity.
    public void ForegroundPlaybackRefreshesIdleWithoutRealInput(RecordingMode mode)
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        tracker.SetMode(mode, mode == RecordingMode.NoScreen ? 10 : null, At(0));
        for (var i = 1; i <= 300; i++) tracker.Observe(At(i, media: i % 5 == 0 ? i : null));
        Assert.Equal(mode, tracker.State.Mode);
        Assert.Equal(300, tracker.Pending.Sum(x => x.Seconds));
        // Losing Playing/matching/read success simply leaves the existing countdown running.
        for (var i = 301; i <= 421; i++) tracker.Observe(At(i));
        Assert.Equal(RecordingMode.Away, tracker.State.Mode);
        Assert.Equal(420, tracker.Pending.Sum(x => x.Seconds));
    }

    [Fact]
    // A brief playback interruption needs no extra grace rule beyond the idle threshold.
    public void PlaybackCanResumeBeforeIdleCountdownExpires()
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        for (var i = 0; i <= 250; i++)
            tracker.Observe(At(i, media: i % 5 == 0 && (i <= 100 || i >= 200) ? i : null));
        Assert.Equal(RecordingMode.Default, tracker.State.Mode);
        Assert.Equal(250, tracker.Pending.Sum(x => x.Seconds));
    }

    [Fact]
    // Disabling the option preserves the last confirmed countdown and persists the explicit choice.
    public void TurningOffMediaKeepsLastCountdownAndSetting()
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        for (var i = 0; i <= 100; i++) tracker.Observe(At(i, media: i % 5 == 0 ? i : null));
        tracker.Configure(tracker.Settings with { MediaAutoStay = false }, At(100));
        for (var i = 101; i < 220; i++) tracker.Observe(At(i, media: i));
        Assert.Equal(RecordingMode.Default, tracker.State.Mode);
        tracker.Observe(At(220, media: 220));
        Assert.Equal(RecordingMode.Away, tracker.State.Mode);
        Assert.Equal(220, tracker.Pending.Sum(x => x.Seconds));
        var restored = JsonSerializer.Deserialize<TrackingSettings>(JsonSerializer.Serialize(tracker.Settings))!;
        Assert.False(restored.MediaAutoStay);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // Neither manual nor automatic away can be resumed by a fresh media sample alone.
    public void MediaNeverResumesAwayButRealInputDoes(bool manual)
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        tracker.Observe(At(0));
        if (manual) tracker.SetMode(RecordingMode.Away, null, At(0));
        for (var i = 1; i <= 120; i++) tracker.Observe(At(i));
        for (var i = 121; i <= 150; i++) tracker.Observe(At(i, media: i));
        Assert.Equal(RecordingMode.Away, tracker.State.Mode);
        tracker.Observe(At(151, input: 151));
        Assert.Equal(RecordingMode.Default, tracker.State.Mode);
    }

    [Fact]
    // Media during a locked mode must not extend its explicit deadline.
    public void LockedDeadlineIsNotExtendedByMedia()
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        tracker.SetMode(RecordingMode.Locked, 3, At(0));
        for (var i = 1; i <= 180; i++) tracker.Observe(At(i, media: i < 180 ? i : null));
        Assert.Equal(RecordingMode.Away, tracker.State.Mode);
        Assert.Equal(180, tracker.Pending.Sum(x => x.Seconds));
    }

    [Fact]
    // System lock discards prior media activity and pauses all foreground accounting.
    public void SystemLockWinsAndDiscardsMediaConfirmation()
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        for (var i = 0; i <= 150; i++) tracker.Observe(At(i, media: i));
        tracker.Observe(At(151, media: 151) with { SystemBlocked = true });
        for (var i = 152; i <= 200; i++) tracker.Observe(At(i, media: i) with { SystemBlocked = true });
        Assert.False(tracker.IsTimingActive);
        Assert.Equal(151, tracker.Pending.Sum(x => x.Seconds));
        tracker.Observe(At(201));
        Assert.Equal(RecordingMode.Away, tracker.State.Mode);
    }

    [Fact]
    // Disabled recording ignores media and cannot accumulate hidden activity.
    public void AutoRecordOffDoesNotAcceptMedia()
    {
        var tracker = new Tracker(new(AutoRecord: false), new(), TimeZoneInfo.Utc);
        for (var i = 0; i <= 150; i++) tracker.Observe(At(i, media: i));
        Assert.Equal(RecordingMode.Away, tracker.State.Mode);
        Assert.Empty(tracker.Pending);
    }

    [Fact]
    // An unknown foreground cannot supply a valid media activity confirmation.
    public void UnknownForegroundDoesNotRefreshIdle()
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        for (var i = 0; i <= 120; i++) tracker.Observe(At(i, media: i) with { App = null });
        Assert.Equal(RecordingMode.Away, tracker.State.Mode);
        Assert.Empty(tracker.Pending);
    }

    [Fact]
    // NoScreen expires on schedule while valid media keeps the resulting default mode active.
    public void NoScreenDeadlineSurvivesMediaActivity()
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        tracker.SetMode(RecordingMode.NoScreen, 3, At(0));
        for (var i = 1; i < 180; i++) tracker.Observe(At(i, media: i % 5 == 0 ? i : null));
        Assert.Equal(RecordingMode.NoScreen, tracker.State.Mode);
        tracker.Observe(At(180, media: 180));
        Assert.Equal(RecordingMode.Default, tracker.State.Mode);
        Assert.Null(tracker.State.Until);
        Assert.Equal(180, tracker.Pending.Sum(x => x.Seconds));
    }

    [Fact]
    // Desktop auto-away keeps its original five-second rule even after watching media.
    public void DesktopAutoAwayKeepsRealInputBoundary()
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        for (var i = 0; i <= 150; i++) tracker.Observe(At(i, media: i));
        tracker.Observe(At(151, media: 151) with { DesktopShown = true });
        Assert.Equal(RecordingMode.Away, tracker.State.Mode);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(121)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    // Reject stale, future and non-finite samples instead of extending the countdown.
    public void InvalidMediaTimesCannotPreventAway(double media)
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        tracker.Observe(At(0));
        tracker.Observe(At(120, media: media));
        Assert.Equal(RecordingMode.Away, tracker.State.Mode);
    }

    [Fact]
    // Cached confirmations must retain their original timestamp, not refresh on every tracking tick.
    public void RepeatingCachedMediaSampleDoesNotKeepRecordingForever()
    {
        var tracker = new Tracker(new(), new(), TimeZoneInfo.Utc);
        for (var i = 0; i <= 200; i++) tracker.Observe(At(i, media: 0));
        Assert.Equal(RecordingMode.Away, tracker.State.Mode);
        Assert.Equal(120, tracker.Pending.Sum(x => x.Seconds));
    }

    [Fact]
    // Older settings gain only the new approved default; existing user choices survive unchanged.
    public void OlderSettingsKeepUserValuesAndEnableNewOption()
    {
        var original = new TrackingSettings(AutoRecord: false, IdleSeconds: 345, DesktopAutoAway: false,
            Theme: "light", NightMode: true, DayStartHour: 7, HourlyMinimumMinutes: 23);
        var legacy = JsonSerializer.SerializeToNode(original)!;
        legacy.AsObject().Remove(nameof(TrackingSettings.MediaAutoStay));
        Assert.Equal(original, legacy.Deserialize<TrackingSettings>());
    }

    [Theory]
    [InlineData("Chrome", "chrome", null, true)]
    [InlineData("PotPlayerMini.exe", "PotPlayerMini", null, true)]
    [InlineData("Package!App", "ApplicationFrameHost", "Package!App", true)]
    [InlineData("OtherPackage!App", "ApplicationFrameHost", "Package!App", false)]
    [InlineData("Chrome.Helper", "chrome", null, false)]
    [InlineData("C:\\chrome.exe", "chrome", null, false)]
    [InlineData("chrome", "msedge", null, false)]
    [InlineData("", "chrome", null, false)]
    // Match exact source identities for browsers, native players and packaged applications.
    public void MediaSourceRequiresExactApplicationMatch(string source, string process, string? appId, bool matches)
    {
        Assert.Equal(matches, MediaSourceMatcher.Matches(source, process, appId));
    }
}
