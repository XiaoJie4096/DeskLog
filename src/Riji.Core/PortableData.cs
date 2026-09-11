namespace Riji.Core;

public sealed record PortableData(int Format, DateTimeOffset Exported, TrackingSettings Settings, CaptureSettings Capture,
    Category[] Categories, PromptPreset[] Presets, ActivitySlice[] Activities, WebsiteSlice[] Websites,
    RecognitionJob[] Jobs, ActivityRecord[] Records, SummaryDocument[] Summaries)
{
    // Validate the complete replacement before touching live rows; imported SQL is never executed.
    public void Validate()
    {
        if (Format is not (1 or 2 or 3)) throw new ArgumentException("不支持此备份格式版本。");
        if (Format < 3 && (Settings.WebsiteProjects is not null || Activities.Any(a => a.SourceAppId is not null || a.SourceAppName is not null)))
            throw new ArgumentException("网站归属快照需要备份格式 3。");
        if (Format == 1 && (Settings.WebsiteSnippets || Websites.Any(item => item.Snippet is not null)))
            throw new ArgumentException("网页摘要需要备份格式 2，不能标记为旧格式。");
        Tracker.ValidateSettings(Settings); Capture.Validate(); Category.Validate(Categories);
        static void Unique(IEnumerable<string> ids)
        { var list = ids.ToArray(); if (list.Any(id => !Guid.TryParseExact(id, "N", out _)) || list.Distinct().Count() != list.Length) throw new ArgumentException("备份存在无效或重复记录 ID。"); }
        static void Day(string day)
        { if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) throw new ArgumentException("备份日期无效。"); }
        Unique(Activities.Select(item => item.Id)); Unique(Websites.Select(item => item.Id)); Unique(Jobs.Select(item => item.Id));
        Unique(Records.Select(item => item.Id)); Unique(Summaries.Select(item => item.Id));
        var parents = Activities.ToDictionary(item => item.Id);
        foreach (var activity in Activities)
        {
            Day(activity.Day);
            if ((activity.SourceAppId is null) != (activity.SourceAppName is null)
                || activity.SourceAppId is { } source && (string.IsNullOrWhiteSpace(source) || source.Length > 200)
                || activity.SourceAppName is { } sourceName && (string.IsNullOrWhiteSpace(sourceName) || sourceName.Length > 500))
                throw new ArgumentException("备份来源应用无效。");
            if (!double.IsFinite(activity.Seconds) || activity.Seconds is <= 0 or > 172800 || activity.StartUtc > activity.EndUtc
                || string.IsNullOrWhiteSpace(activity.AppId) || activity.AppId.Length > 200 || string.IsNullOrWhiteSpace(activity.AppName) || activity.AppName.Length > 500)
                throw new ArgumentException("备份应用区间无效。");
        }
        foreach (var website in Websites)
        {
            if (!parents.TryGetValue(website.ParentId, out var parent) || website.Day != parent.Day || WebsitePrivacy.NormalizeDomain(website.Domain) != website.Domain
                || !double.IsFinite(website.Seconds) || website.Seconds <= 0 || website.Title?.Length > 200 || website.Snippet?.Length > 300) throw new ArgumentException("备份网站区间无效。");
        }
        foreach (var group in Websites.GroupBy(item => item.ParentId))
            if (group.Sum(item => item.Seconds) > parents[group.Key].Seconds + 0.001) throw new ArgumentException("网站时长超过应用父区间。");
        var jobs = Jobs.ToDictionary(item => item.Id);
        foreach (var job in Jobs)
        {
            Day(job.Day); Category.Validate(job.Categories);
            if (job.Prompt is { } prompt && (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 20000)
                || job.Context?.AppName?.Length > 120 || job.Context?.BrowserTitle?.Length > 200)
                throw new ArgumentException("备份识别提示词或辅助信息无效。");
            if (job.Image != job.Id + ".png" || job.IntervalSeconds is not (60 or 120 or 300) || !Enum.IsDefined(job.Status) || job.Attempts is < 0 or > 10000)
                throw new ArgumentException("备份识别任务无效。");
        }
        foreach (var record in Records)
        {
            Day(record.Day);
            if (!jobs.TryGetValue(record.Id, out var job) || job.Status != JobStatus.Succeeded || record.Utc != job.Utc || record.Day != job.Day || record.Seconds != job.IntervalSeconds
                || !job.Categories.Contains(record.Category)) throw new ArgumentException("成功记录与任务快照不一致。");
            RecognitionValidation.Validate(new(record.Description, record.Category.Id, record.Confidence), job.Categories);
        }
        if (Jobs.Count(item => item.Status == JobStatus.Succeeded) != Records.Length) throw new ArgumentException("备份缺少成功任务的活动记录。");
        foreach (var summary in Summaries)
        {
            summary.Range.Validate();
            if (summary.ChatDraft is null || summary.ChatDraft.Length > 10000 || !Enum.IsDefined(summary.State) || string.IsNullOrWhiteSpace(summary.Prompt) || summary.Prompt.Length > 10000
                || summary.Sources.Length == 0 || summary.State == GenerationState.Succeeded && string.IsNullOrWhiteSpace(summary.Text)) throw new ArgumentException("备份总结无效。");
            foreach (var source in summary.Sources)
            {
                if (source.Utc < summary.Range.Start || source.Utc >= summary.Range.End || source.Seconds is not (60 or 120 or 300)) throw new ArgumentException("总结来源范围无效。");
                Category.Validate([source.Category]); RecognitionValidation.Validate(new(source.Description, source.Category.Id, source.Confidence), [source.Category]);
            }
            foreach (var turn in summary.Conversation ?? [])
                if (!Enum.IsDefined(turn.State) || string.IsNullOrWhiteSpace(turn.Question) || turn.State == GenerationState.Succeeded && string.IsNullOrWhiteSpace(turn.Reply)) throw new ArgumentException("备份对话无效。");
        }
        if (Presets.Length > 100 || Presets.Select(item => item.Id).Distinct().Count() != Presets.Length
            || Presets.Any(item => string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 40 || string.IsNullOrWhiteSpace(item.Prompt) || item.Prompt.Length > 10000))
            throw new ArgumentException("备份提示词预设无效。");
    }
}
