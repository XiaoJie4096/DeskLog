namespace Riji.Core;

public enum RecordingMode { Default, Away, Locked, NoScreen }
public sealed record AppIdentity(string Id, string Name);
public sealed record Observation(DateTimeOffset Utc, double MonotonicSeconds, AppIdentity? App,
    double LastInputSeconds, bool SystemBlocked = false, int ProcessId = 0, string WindowKey = "", WebsiteEvidence? Website = null);
public sealed record TrackingSettings(bool AutoRecord = true, bool AppTiming = true, int IdleSeconds = 120,
    string Theme = "dark", bool WebsiteTitles = true, bool FollowSystemTheme = true, bool StartWithWindows = true, WebsiteRule[]? WebsiteRules = null, bool WebsiteSnippets = false,
    WebsiteProject[]? WebsiteProjects = null);
public sealed record ModeState(RecordingMode Mode = RecordingMode.Default, DateTimeOffset? Until = null,
    DateTimeOffset? CooldownUntil = null);
public sealed record ActivitySlice(string Id, string Session, string AppId, string AppName, string Day,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, double Seconds, string? SourceAppId = null, string? SourceAppName = null);

// Track only observed, bounded intervals; persistence is acknowledged by the caller.
public sealed class Tracker
{
    private Observation? previous;
    private readonly string session = Guid.NewGuid().ToString("N");
    private readonly List<ActivitySlice> pending = [];
    private ActivitySlice? segment;
    private WebsiteSlice? websiteSegment;
    private readonly List<WebsiteSlice> pendingWebsites = [];
    public TrackingSettings Settings { get; private set; }
    public ModeState State { get; private set; }
    public TimeZoneInfo Zone { get; private set; }
    public string Health { get; private set; } = "等待前台检测";
    public AppIdentity? CurrentApp => previous?.App;
    public bool IsTimingActive => Settings.AutoRecord && Settings.AppTiming && State.Mode != RecordingMode.Away
        && previous is { SystemBlocked: false, App: not null };
    public string TimingStatus => !Settings.AutoRecord ? "自动记录已关闭" : !Settings.AppTiming ? "应用计时已关闭"
        : previous?.SystemBlocked == true ? "系统锁屏或休眠，计时暂停" : State.Mode == RecordingMode.Away ? "离开，计时暂停"
        : previous?.App is null ? "无法确认前台应用，计时暂停" : "前台应用计时中";
    public IReadOnlyList<ActivitySlice> Pending => pending;
    public IReadOnlyList<WebsiteSlice> PendingWebsites => pendingWebsites;

    public Tracker(TrackingSettings settings, ModeState state, TimeZoneInfo zone)
    {
        ValidateSettings(settings);
        Settings = settings with { WebsiteRules = null, WebsiteSnippets = false, WebsiteProjects = settings.WebsiteProjects ?? WebsiteProject.Defaults };
        State = state;
        Zone = zone;
    }

    // Validate before changing user intent.
    public static void ValidateSettings(TrackingSettings value)
    {
        WebsitePrivacy.ValidateRules(value.WebsiteRules);
        WebsiteProject.Validate(value.WebsiteProjects);
        if (value.IdleSeconds is < 30 or > 3600 || value.Theme is not ("dark" or "light"))
            throw new ArgumentException("空闲时间应为 30–3600 秒，主题应为深色或浅色。");
    }

    // Attribute elapsed time to the last observed foreground application.
    public void Observe(Observation next)
    {
        if (previous is { } prior && next.LastInputSeconds < prior.LastInputSeconds)
            next = next with { LastInputSeconds = prior.LastInputSeconds };
        if (previous is { } old)
        {
            var elapsed = next.MonotonicSeconds - old.MonotonicSeconds;
            if (elapsed is > 0 and <= 3)
            {
                var allowed = elapsed;
                if (State.Mode == RecordingMode.Default)
                    allowed = Math.Min(allowed, Math.Max(0, old.LastInputSeconds + Settings.IdleSeconds - old.MonotonicSeconds));
                if (State.Mode is RecordingMode.Locked or RecordingMode.NoScreen && State.Until is { } until)
                {
                    var toExpiry = Math.Max(0, (until - old.Utc).TotalSeconds);
                    if (toExpiry < elapsed && old.MonotonicSeconds + toExpiry - old.LastInputSeconds >= Settings.IdleSeconds)
                        allowed = Math.Min(allowed, toExpiry);
                }
                if (Settings.AutoRecord && Settings.AppTiming && State.Mode != RecordingMode.Away && !old.SystemBlocked && old.App is { } app && allowed > 0)
                    AddSlices(old, app, allowed);
                else segment = null;
                Health = next.SystemBlocked ? "系统锁屏或休眠，计时暂停" : next.App is null ? "无法确认前台应用，未累计未知时间" : "正常";
            }
            else if (elapsed > 3 || elapsed < 0)
            { Health = "检测中断，未知空档未补算"; segment = null; }
        }
        if (State.Until is { } deadline && next.Utc >= deadline) State = new();
        if (!next.SystemBlocked)
        {
            if (State.Mode == RecordingMode.Default && next.MonotonicSeconds - next.LastInputSeconds >= Settings.IdleSeconds)
                State = new(RecordingMode.Away);
            else if (State.Mode == RecordingMode.Away && next.LastInputSeconds > (previous?.LastInputSeconds ?? next.LastInputSeconds)
                && (State.CooldownUntil is null || next.Utc >= State.CooldownUntil))
                State = new();
        }
        previous = next;
    }

    // Apply an explicit mode command after flushing the old boundary.
    public void SetMode(RecordingMode mode, int? minutes, Observation now)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("记录状态无效。");
        if (mode is RecordingMode.Locked or RecordingMode.NoScreen && (minutes is null or < 1 or > 1440))
            throw new ArgumentException("持续时间应为 1–1440 分钟。");
        Observe(now);
        State = new(mode, mode is RecordingMode.Locked or RecordingMode.NoScreen ? now.Utc.AddMinutes(minutes!.Value) : null,
            mode == RecordingMode.Away ? now.Utc.AddSeconds(30) : null);
        // An explicit resume is an effective interaction even if the OS input callback is delayed.
        if (mode == RecordingMode.Default) previous = now with { LastInputSeconds = now.MonotonicSeconds };
    }

    public void Configure(TrackingSettings settings, Observation now)
    {
        ValidateSettings(settings);
        Observe(now);
        Settings = settings with { WebsiteRules = null, WebsiteSnippets = false, WebsiteProjects = settings.WebsiteProjects ?? WebsiteProject.Defaults };
    }

    // Flush before changing date attribution rules.
    public void ChangeZone(TimeZoneInfo zone, Observation now) { Observe(now); Zone = zone; }
    public void Acknowledge() { pending.Clear(); pendingWebsites.Clear(); }

    // Split a monotonic duration at local midnight without using wall-clock deltas as duration.
    private void AddSlices(Observation start, AppIdentity app, double duration)
    {
        var utc = start.Utc;
        var remaining = duration;
        while (remaining > 0.000001)
        {
            var local = TimeZoneInfo.ConvertTime(utc, Zone);
            var midnight = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
            while (Zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
            var boundary = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, Zone));
            var seconds = Math.Min(remaining, Math.Max(0.001, (boundary - utc).TotalSeconds));
            var consumed = duration - remaining;
            var freshSeconds = start.Website is { } evidence
                ? Math.Max(0, evidence.ValidUntil - start.MonotonicSeconds - consumed) : 0;
            // Treat floating-point residue as expired so the loop always advances.
            if (freshSeconds < 0.000001) freshSeconds = 0;
            var project = freshSeconds > 0 && start.Website is { } current
                ? WebsiteProject.Resolve(current.Domain, Settings.WebsiteProjects) : null;
            if (freshSeconds > 0) seconds = Math.Min(seconds, freshSeconds);
            var owner = project is null ? app : new AppIdentity("website:" + project.Id, project.Name);
            var end = utc.AddSeconds(seconds);
            var day = local.ToString("yyyy-MM-dd");
            if (segment is { } active && active.AppId == owner.Id && active.AppName == owner.Name
                && active.SourceAppId == app.Id && active.SourceAppName == app.Name && active.Day == day && Math.Abs((active.EndUtc - utc).TotalSeconds) < 0.05)
            {
                segment = active with { EndUtc = end, Seconds = active.Seconds + seconds };
                if (pending.Count > 0 && pending[^1].Id == active.Id) pending[^1] = segment;
                else pending.Add(segment);
            }
            else
            {
                segment = new(Guid.NewGuid().ToString("N"), session, owner.Id, owner.Name, day, utc, end, seconds, app.Id, app.Name);
                pending.Add(segment);
            }
            var siteSeconds = Math.Min(seconds, freshSeconds);
            if (start.Website is { } site && siteSeconds > 0)
            {
                var title = Settings.WebsiteTitles ? site.Title : null;
                string? snippet = null;
                if (websiteSegment is { } prior && prior.ParentId == segment.Id && prior.Domain == site.Domain && prior.Title == title && prior.Snippet == snippet)
                {
                    websiteSegment = prior with { Seconds = prior.Seconds + siteSeconds };
                    if (pendingWebsites.Count > 0 && pendingWebsites[^1].Id == prior.Id) pendingWebsites[^1] = websiteSegment;
                    else pendingWebsites.Add(websiteSegment);
                }
                else
                {
                    websiteSegment = new(Guid.NewGuid().ToString("N"), segment.Id, day, site.Domain, title, siteSeconds, snippet);
                    pendingWebsites.Add(websiteSegment);
                }
            }
            else websiteSegment = null;
            remaining -= seconds;
            utc = end;
        }
    }
}
