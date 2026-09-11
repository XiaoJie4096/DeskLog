namespace Riji.Core;

public sealed record WebsiteEvidence(string Domain, string? Title, double ValidUntil, string? Snippet = null);
public sealed record WebsiteSlice(string Id, string ParentId, string Day, string Domain, string? Title, double Seconds, string? Snippet = null);
public sealed record WebsiteRule(string Domain, bool Allow);

public static class WebsitePrivacy
{
    public static void ValidateRules(WebsiteRule[]? rules)
    {
        if (rules is null) return;
        if (rules.Length > 50 || rules.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.Domain) || NormalizeDomain(rule.Domain) != rule.Domain)
            || rules.Select(rule => rule.Domain).Distinct().Count() != rules.Length)
            throw new ArgumentException("网站规则最多 50 条，域名必须有效且不能重复，请勿包含协议、路径或查询参数。");
    }

    public static bool Allowed(string domain, WebsiteRule[]? rules) => rules?
        .Where(rule => Matches(domain, rule.Domain)).OrderByDescending(rule => rule.Domain.Length).FirstOrDefault()?.Allow ?? true;

    // Accept a domain only; reject ports, paths, credentials, query strings and fragments.
    public static string? NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253 || domain.IndexOfAny(['/', '\\', ':', '?', '#', '@', ' ']) >= 0) return null;
        try
        {
            var ascii = new System.Globalization.IdnMapping().GetAscii(domain.TrimEnd('.')).ToLowerInvariant();
            if (Uri.CheckHostName(ascii) != UriHostNameType.Dns) return null;
            if (ascii.Split('.').Any(label => label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-'))) return null;
            return ascii;
        }
        catch (ArgumentException) { return null; }
    }

    public static bool Matches(string domain, string rule) => domain == rule || domain.EndsWith("." + rule, StringComparison.Ordinal);
}
