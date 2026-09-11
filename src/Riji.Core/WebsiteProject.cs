namespace Riji.Core;

public sealed record WebsiteProject(string Id, string Name, string[] Domains)
{
    public static WebsiteProject[] Defaults => [
        new("bilibili", "B站", ["bilibili.com"]),
        new("github", "GitHub", ["github.com"]),
        new("chatgpt", "ChatGPT", ["chatgpt.com", "chat.openai.com"]),
        new("xiaohongshu", "小红书", ["xiaohongshu.com", "xhslink.com"]),
        new("zhihu", "知乎", ["zhihu.com"]),
        new("douyin", "抖音", ["douyin.com"]),
        new("feishu", "飞书", ["feishu.cn", "feishu.com"]),
        new("baidu", "百度", ["baidu.com"])
    ];

    // Freeze the winning rule when recording; never resolve historical intervals again.
    public static WebsiteProject? Resolve(string domain, WebsiteProject[]? projects) => (projects ?? Defaults)
        .SelectMany(project => project.Domains.Select(rule => (project, rule)))
        .Where(item => WebsitePrivacy.Matches(domain, item.rule))
        .OrderByDescending(item => item.rule.Length).Select(item => item.project).FirstOrDefault();

    public static void Validate(WebsiteProject[]? projects)
    {
        if (projects is null) return;
        if (projects.Length > 50 || projects.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 100
            || p.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80
            || p.Domains is null || p.Domains.Length is < 1 or > 20
            || p.Domains.Any(d => string.IsNullOrWhiteSpace(d) || WebsitePrivacy.NormalizeDomain(d) != d))
            || projects.Select(p => p.Id).Distinct().Count() != projects.Length)
            throw new ArgumentException("最多 50 个网站应用，每项需名称和 1–20 个有效域名。");
        var domains = projects.SelectMany(p => p.Domains).ToArray();
        if (domains.Distinct().Count() != domains.Length) throw new ArgumentException("同一域名不能重复归属；子域名优先匹配。");
    }
}
