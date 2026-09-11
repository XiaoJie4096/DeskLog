namespace Riji.Core;

public sealed record BrowserChallenge(string Nonce, bool Titles, WebsiteRule[]? Rules = null, bool Snippets = false);
public sealed record BrowserReport(string Session, string Nonce, long Sequence, string Type, string? Domain = null, string? Title = null, string? Snippet = null);

// The host serializes access with foreground boundaries; every report answers one short-lived challenge.
public sealed class BrowserSessions
{
    private sealed class Peer(string browser)
    {
        public readonly string Browser = browser;
        public double Seen = double.NegativeInfinity;
        public string? Nonce;
        public Observation? Expected;
        public bool Titles;
        public long Sequence = -1;
        public string? Window;
        public WebsiteEvidence? Evidence;
    }
    private readonly Dictionary<string, Peer> peers = [];
    public int Connections(double now) => peers.Values.Count(peer => now - peer.Seen < 5);
    public void Clear() => peers.Clear();
    public void DisableTitles()
    {
        foreach (var peer in peers.Values)
        {
            peer.Titles = false;
            if (peer.Evidence is { } evidence) peer.Evidence = evidence with { Title = null };
        }
    }

    public BrowserChallenge Challenge(string session, string browser, Observation now, bool titles)
    {
        if (!Guid.TryParse(session, out _) || browser is not ("chrome" or "msedge" or "firefox")) throw new ArgumentException("浏览器会话无效。");
        foreach (var id in peers.Where(pair => now.MonotonicSeconds - (pair.Value.Expected?.MonotonicSeconds ?? pair.Value.Seen) > 10).Select(pair => pair.Key).ToArray()) peers.Remove(id);
        if (!peers.TryGetValue(session, out var peer))
        {
            if (peers.Count >= 32) throw new InvalidOperationException("浏览器会话过多。");
            peers[session] = peer = new(browser);
        }
        if (peer.Browser != browser) throw new ArgumentException("浏览器会话身份已改变。");
        peer.Nonce = Guid.NewGuid().ToString("N"); peer.Expected = now; peer.Titles = titles;
        // A probe alone does not establish connection health or website evidence.
        return new(peer.Nonce, titles);
    }

    public bool Report(BrowserReport report, Observation now)
    {
        if (!peers.TryGetValue(report.Session, out var peer) || peer.Expected is not { } expected || peer.Nonce is null || peer.Nonce != report.Nonce
            || report.Sequence <= peer.Sequence || now.MonotonicSeconds - expected.MonotonicSeconds is < 0 or > 2) return false;
        peer.Nonce = null; peer.Sequence = report.Sequence; peer.Seen = now.MonotonicSeconds;
        if (now.SystemBlocked || expected.SystemBlocked || now.WindowKey != expected.WindowKey || now.ProcessId != expected.ProcessId
            || !string.Equals(now.App?.Name, peer.Browser, StringComparison.OrdinalIgnoreCase) || report.Type == "none")
        { peer.Evidence = null; return true; }
        if (report.Type == "transient") return true;
        var domain = WebsitePrivacy.NormalizeDomain(report.Domain);
        if (report.Type != "activity" || domain is null) { peer.Evidence = null; return true; }
        peer.Window = now.WindowKey;
        peer.Evidence = new(domain, peer.Titles && report.Title is { Length: > 0 } title ? title[..Math.Min(title.Length, 200)] : null, now.MonotonicSeconds + 1.5);
        return true;
    }

    public WebsiteEvidence? Evidence(Observation now)
    {
        if (now.SystemBlocked) return null;
        var candidates = peers.Values.Where(peer => peer.Window == now.WindowKey && peer.Evidence?.ValidUntil > now.MonotonicSeconds
            && string.Equals(peer.Browser, now.App?.Name, StringComparison.OrdinalIgnoreCase)).Select(peer => peer.Evidence!).ToArray();
        // Conflicting simultaneous profile claims stay unclassified rather than being arbitrarily assigned.
        if (candidates.Select(item => (item.Domain, item.Title, item.Snippet)).Distinct().Count() != 1) return null;
        return candidates.OrderByDescending(item => item.ValidUntil).First();
    }
}
