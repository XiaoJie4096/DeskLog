namespace Riji.Core;

public static class MediaSourceMatcher
{
    // Match exact application identities; never infer ownership from titles or substrings.
    public static bool Matches(string source, string processName, string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (!string.IsNullOrEmpty(appUserModelId) && source.Equals(appUserModelId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (source.Contains('!') || source.Contains('\\') || source.Contains('/')) return false;
        var name = source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? source[..^4] : source;
        return name.Equals(processName, StringComparison.OrdinalIgnoreCase);
    }
}
