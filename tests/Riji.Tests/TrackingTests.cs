using Riji.Core;
using Riji.Infrastructure;
using Xunit;

namespace Riji.Tests;

public sealed class TrackingTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
    private static Observation At(double seconds, string app = "a", double? input = null, bool blocked = false) =>
        new(Epoch.AddSeconds(seconds), seconds, new(app, app), input ?? seconds, blocked);
    private static Tracker New() => new(new(), new(), TimeZoneInfo.Utc);

    [Fact] public void DisplayedTimingStateMatchesUnknownDisabledAwayAndSystemBoundaries()
    {
        var tracker = New(); Assert.False(tracker.IsTimingActive);
        tracker.Observe(At(0)); Assert.True(tracker.IsTimingActive);
        tracker.Observe(At(1) with { App = null }); Assert.False(tracker.IsTimingActive);
        Assert.Contains("无法确认前台", tracker.TimingStatus);
        tracker.Observe(At(2)); Assert.True(tracker.IsTimingActive);
        tracker.SetMode(RecordingMode.NoScreen, 15, At(3)); Assert.True(tracker.IsTimingActive);
        tracker.Observe(At(4, blocked: true)); Assert.False(tracker.IsTimingActive);
        tracker.Observe(At(5)); Assert.True(tracker.IsTimingActive);
        tracker.Configure(tracker.Settings with { AppTiming = false }, At(6)); Assert.False(tracker.IsTimingActive);
        Assert.Equal("应用计时已关闭", tracker.TimingStatus);
        tracker.Configure(tracker.Settings with { AutoRecord = false, AppTiming = true }, At(7)); Assert.False(tracker.IsTimingActive);
        Assert.Equal("自动记录已关闭", tracker.TimingStatus);
        tracker.Configure(tracker.Settings with { AutoRecord = true }, At(8));
        tracker.SetMode(RecordingMode.Away, null, At(9)); Assert.False(tracker.IsTimingActive);
        Assert.Contains("离开", tracker.TimingStatus);
    }

    [Fact] public void ForegroundIntervalsNeverOverlap()
    {
        var t = New();
        for (var i = 0; i <= 300; i++) t.Observe(At(i, i < 120 ? "a" : "b"));
        Assert.Equal(120, t.Pending.Where(x => x.AppId == "a").Sum(x => x.Seconds));
        Assert.Equal(180, t.Pending.Where(x => x.AppId == "b").Sum(x => x.Seconds));
    }

    [Fact] public void MidnightSplitsWithoutChangingTotal()
    {
        var t = New();
        var start = DateTimeOffset.Parse("2026-09-10T23:59:30Z");
        for (var i = 0; i <= 60; i++) t.Observe(At(i) with { Utc = start.AddSeconds(i) });
        Assert.Equal(30, t.Pending.Where(x => x.Day == "2026-09-10").Sum(x => x.Seconds));
        Assert.Equal(30, t.Pending.Where(x => x.Day == "2026-09-11").Sum(x => x.Seconds));
    }

    [Theory] [InlineData(3600)] [InlineData(-3600)]
    public void WallClockCorrectionDoesNotChangeDuration(int correction)
    {
        var t = New();
        for (var i = 0; i <= 60; i++) t.Observe(At(i) with { Utc = Epoch.AddSeconds(i + (i >= 30 ? correction : 0)) });
        Assert.Equal(60, t.Pending.Sum(x => x.Seconds));
    }

    [Fact] public void IdleEndsAtThresholdAndInputResumes()
    {
        var t = New();
        for (var i = 0; i <= 150; i++) t.Observe(At(i, input: 0));
        Assert.Equal(120, t.Pending.Sum(x => x.Seconds));
        Assert.Equal(RecordingMode.Away, t.State.Mode);
        t.Observe(At(151)); t.Observe(At(152));
        Assert.Equal(121, t.Pending.Sum(x => x.Seconds));
    }

    [Fact] public void ManualAwayHonorsCooldownButExplicitResumeWins()
    {
        var t = New(); t.Observe(At(0)); t.SetMode(RecordingMode.Away, null, At(1));
        for (var i = 2; i <= 30; i++) t.Observe(At(i));
        Assert.Equal(RecordingMode.Away, t.State.Mode);
        Assert.Equal(1, t.Pending.Sum(x => x.Seconds));
        t.SetMode(RecordingMode.Default, null, At(30)); t.Observe(At(31));
        Assert.Equal(2, t.Pending.Sum(x => x.Seconds));
    }

    [Theory] [InlineData(RecordingMode.Locked)] [InlineData(RecordingMode.NoScreen)]
    public void TimedModesExpireEvenAfterLongGap(RecordingMode mode)
    {
        var t = New(); t.SetMode(mode, 1, At(0));
        for (var i = 1; i <= 30; i++) t.Observe(At(i, input: 0));
        t.Observe(At(600, input: 0));
        Assert.Equal(30, t.Pending.Sum(x => x.Seconds));
        Assert.Equal(RecordingMode.Away, t.State.Mode);
    }

    [Fact] public void InvalidModeLeavesIntentUntouched()
    {
        var t = New();
        Assert.Throws<ArgumentException>(() => t.SetMode(RecordingMode.Locked, 0, At(0)));
        Assert.Equal(RecordingMode.Default, t.State.Mode);
    }

    [Fact] public void SystemLockWinsOverLockedMode()
    {
        var t = New(); t.SetMode(RecordingMode.Locked, 30, At(0));
        t.Observe(At(1, blocked: true)); t.Observe(At(2, blocked: true));
        t.Observe(At(3)); t.Observe(At(4));
        Assert.Equal(2, t.Pending.Sum(x => x.Seconds));
    }

    [Fact] public void ShutdownAndCrashNeverExtendLastSession()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RijiTests", Guid.NewGuid().ToString(), "test.db");
        try
        {
            var t = New(); t.Observe(At(0)); t.Observe(At(1));
            using (var store = new LocalStore(path))
            {
                store.Save(t.Pending, t.Settings, t.State);
                store.Save(t.Pending, t.Settings, t.State);
                Assert.Equal(1, store.Apps("2026-09-10").Single().Seconds);
            }
            using (var store = new LocalStore(path))
            {
                var restarted = new Tracker(store.Read<TrackingSettings>("settings")!, store.Read<ModeState>("mode")!, TimeZoneInfo.Utc);
                restarted.Observe(At(601)); restarted.Observe(At(602));
                store.Save(restarted.Pending, restarted.Settings, restarted.State);
                Assert.Equal(2, store.Apps("2026-09-10").Single().Seconds);
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(System.IO.Path.GetDirectoryName(path)!, true); }
    }

    [Fact] public void CheckpointsExtendOneIntervalAndRejectOlderRetries()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RijiTests", Guid.NewGuid().ToString(), "test.db");
        try
        {
            using var store = new LocalStore(path);
            var t = New(); t.Observe(At(0)); t.Observe(At(1));
            var oldBatch = t.Pending.ToArray();
            store.Save(t.Pending, t.Settings, t.State); t.Acknowledge();
            t.Observe(At(2)); store.Save(t.Pending, t.Settings, t.State); t.Acknowledge();
            store.Save(oldBatch, t.Settings, t.State);
            Assert.Equal(2, store.Apps("2026-09-10").Single().Seconds);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(System.IO.Path.GetDirectoryName(path)!, true); }
    }

    [Fact] public void FailedBatchRollsBackEarlierRowsAndSettings()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RijiTests", Guid.NewGuid().ToString(), "test.db");
        try
        {
            using var store = new LocalStore(path);
            var t = New(); t.Observe(At(0)); t.Observe(At(1));
            var valid = t.Pending.Single();
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.Save([valid, valid with { Id = "invalid", Seconds = -1 }], t.Settings, t.State));
            Assert.Empty(store.Apps("2026-09-10"));
            Assert.Null(store.Read<TrackingSettings>("settings"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(System.IO.Path.GetDirectoryName(path)!, true); }
    }

    [Fact] public void ExplicitResumeIsEffectiveEvenWithoutFreshHookEvent()
    {
        var t = New(); t.Observe(At(200, input: 0));
        t.SetMode(RecordingMode.Default, null, At(201, input: 0));
        t.Observe(At(202, input: 0)); t.Observe(At(203, input: 0));
        Assert.Equal(2, t.Pending.Sum(x => x.Seconds));
        Assert.Equal(RecordingMode.Default, t.State.Mode);
    }

    [Fact] public void DisabledTimingAndUnknownForegroundDoNotAccumulate()
    {
        var t = New(); t.Observe(At(0));
        t.Configure(t.Settings with { AppTiming = false }, At(1)); t.Observe(At(2));
        t.Configure(t.Settings with { AppTiming = true }, At(3) with { App = null });
        t.Observe(At(4)); t.Observe(At(5));
        Assert.Equal(2, t.Pending.Sum(x => x.Seconds));
    }

    [Fact] public void TickRolloverKeepsIdleDurationShort()
    { Assert.Equal(0.032, InputActivity.IdleSeconds(16, uint.MaxValue - 15), 6); }

    [Fact] public void SlowMouseDriftDoesNotCountAsAnInteraction()
    {
        var input = new InputActivity(new());
        for (var i = 0; i < 100; i++) Assert.False(input.MouseMoved(i, 0, i * 0.1));
        Assert.True(input.MouseMoved(150, 0, 10));
    }

    [Fact] public void CooldownExpiresOnlyOnNewEffectiveInput()
    {
        var t = New(); t.SetMode(RecordingMode.Away, null, At(0));
        for (var i = 1; i <= 29; i++) t.Observe(At(i));
        t.Observe(At(30, input: 29));
        Assert.Equal(RecordingMode.Away, t.State.Mode);
        t.Observe(At(31));
        Assert.Equal(RecordingMode.Default, t.State.Mode);
        Assert.Empty(t.Pending);
    }

    [Theory] [InlineData(RecordingMode.Locked)] [InlineData(RecordingMode.NoScreen)]
    public void TimedModeIgnoresIdleUntilExactExpiry(RecordingMode mode)
    {
        var t = New(); t.SetMode(mode, 3, At(0));
        for (var i = 1; i <= 181; i++) t.Observe(At(i, input: 0));
        Assert.Equal(180, t.Pending.Sum(x => x.Seconds));
        Assert.Equal(RecordingMode.Away, t.State.Mode);
    }

    [Fact] public void NonUtcMidnightAndTimezoneChangeConserveTime()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test8", TimeSpan.FromHours(8), "test8", "test8");
        var t = new Tracker(new(), new(), zone);
        var start = DateTimeOffset.Parse("2026-09-10T15:59:59.5Z");
        t.Observe(At(0) with { Utc = start });
        t.Observe(At(1) with { Utc = start.AddSeconds(1) });
        Assert.Equal(0.5, t.Pending.Where(x => x.Day == "2026-09-10").Sum(x => x.Seconds));
        Assert.Equal(0.5, t.Pending.Where(x => x.Day == "2026-09-11").Sum(x => x.Seconds));
        t.ChangeZone(TimeZoneInfo.Utc, At(1) with { Utc = start.AddSeconds(1) });
        t.Observe(At(2) with { Utc = start.AddSeconds(2) });
        Assert.Equal(2, t.Pending.Sum(x => x.Seconds));
    }
}
