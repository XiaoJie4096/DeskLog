namespace Riji.Core;

public sealed record Category(string Id, string Name, string Meaning, string Color, bool Enabled = true)
{
    public static Category[] Defaults => [new("development", "开发", "编写、调试和维护软件", "#ddb777"),
        new("learning", "学习", "阅读、课程和知识整理", "#91a58e"), new("communication", "交流", "与他人沟通和协作", "#bfc8ae"),
        new("creation", "创作", "写作、绘画、设计和内容制作", "#ad8566"), new("affairs", "事务", "生活或工作中的日常事务处理", "#c9bca3"),
        new("entertainment", "娱乐", "休闲、游戏和娱乐内容", "#879ba1"), new("other", "其他", "无法归入其他目的的可观察活动", "#777d72")];

    public static void Validate(IReadOnlyList<Category> categories)
    {
        if (categories.Count(x => x.Enabled) is < 1 or > 12) throw new ArgumentException("请启用 1–12 个分类。");
        if (categories.Count > 100 || categories.Select(x => x.Id).Distinct().Count() != categories.Count || categories.Select(x => x.Name.Trim()).Distinct().Count() != categories.Count)
            throw new ArgumentException("分类 ID 和名称不能重复。");
        foreach (var category in categories)
            if (string.IsNullOrWhiteSpace(category.Id) || category.Id.Length > 80 || string.IsNullOrWhiteSpace(category.Name) || category.Name.Trim().Length > 10
                || string.IsNullOrWhiteSpace(category.Meaning) || category.Meaning.Trim().Length > 80 || !System.Text.RegularExpressions.Regex.IsMatch(category.Color, "^#[0-9a-fA-F]{6}$"))
                throw new ArgumentException("分类名称最多 10 字、说明最多 80 字，颜色应为有效色值。");
    }
}

public sealed record AiConfiguration(string Endpoint, string Model, string ProtectedKey, int InputBudget = 100000, string? SummaryModel = null)
{
    public string Identity => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Endpoint + "\n" + Model)))[..16];
    public static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("请输入 HTTPS 接口地址；本机服务可用 HTTP。地址不能包含凭据、查询参数或片段。");
        return uri;
    }
}
public sealed record CaptureSettings(bool Enabled = false, int IntervalSeconds = 60, bool KeepImages = false, int MaxAttempts = 5, string? Prompt = null)
{
    public void Validate()
    {
        if (IntervalSeconds is not (60 or 120 or 300) || MaxAttempts is < 1 or > 10) throw new ArgumentException("采样间隔仅支持 1、2、5 分钟，自动尝试次数为 1–10。");
        if (Prompt is not null && (string.IsNullOrWhiteSpace(Prompt) || Prompt.Length > 10000)) throw new ArgumentException("识别提示词不能为空，最多 10000 字。");
    }
}
public enum JobStatus { Capturing, Pending, Running, Retry, Succeeded, Manual }
public sealed record RecognitionJob(string Id, DateTimeOffset Utc, string Day, int IntervalSeconds, string Image,
    Category[] Categories, JobStatus Status = JobStatus.Capturing, int Attempts = 0, DateTimeOffset? RetryAt = null,
    string? Error = null, bool CleanupPending = false, string? Prompt = null, RecognitionContext? Context = null);
public sealed record RecognitionResult(string Description, string CategoryId, double Confidence);
public sealed record ActivityRecord(string Id, DateTimeOffset Utc, string Day, int Seconds, string Description, Category Category, double Confidence, string? AppName = null, string? BrowserTitle = null, string? Website = null);
public sealed record JobHealth(string Status, int Count);
public sealed record JobDetail(string Id, DateTimeOffset Utc, JobStatus Status, int Attempts, DateTimeOffset? RetryAt, string? Error, bool CleanupPending);
public sealed record JobPage(JobDetail[] Items, int Total);

public sealed class AiFailure(string safeMessage, bool retryable = false, bool authorization = false) : Exception(safeMessage)
{
    public bool Retryable { get; } = retryable;
    public bool Authorization { get; } = authorization;
}

public static class RecognitionValidation
{
    public static void Validate(RecognitionResult result, IReadOnlyList<Category> categories)
    {
        if (string.IsNullOrWhiteSpace(result.Description) || result.Description.Length > 4000 || result.Description.TrimStart().StartsWith('<')
            || !categories.Any(x => x.Id == result.CategoryId && x.Enabled) || !double.IsFinite(result.Confidence) || result.Confidence is < 0 or > 1)
            throw new AiFailure("识别结果缺少有效描述、分类或置信信息。", true);
    }
}
