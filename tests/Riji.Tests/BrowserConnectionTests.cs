using System.Net;
using System.Net.Http.Json;
using Riji.Core;
using Riji.Infrastructure;
using Xunit;

namespace Riji.Tests;

public sealed class BrowserConnectionTests
{
    private static Observation At(double seconds, string window = "chrome:1") => new(DateTimeOffset.UnixEpoch.AddSeconds(seconds), seconds,
        new("chrome-id", "chrome"), seconds, false, 12, window);

    [Fact] public void SingleUseChallengeRejectsReplayExpiryAndWindowChange()
    {
        var sessions = new BrowserSessions(); var id = Guid.NewGuid().ToString();
        var probe = sessions.Challenge(id, "chrome", At(10), false);
        Assert.Equal(0, sessions.Connections(10));
        var packet = new BrowserReport(id, probe.Nonce, 1, "activity", "example.com", "private title");
        Assert.True(sessions.Report(packet, At(10.2)));
        Assert.Null(sessions.Evidence(At(10.3))!.Title);
        Assert.False(sessions.Report(packet with { Sequence = 2 }, At(10.4)));
        Assert.False(sessions.Report(packet with { Nonce = null!, Sequence = 3 }, At(10.4)));
        Assert.Null(sessions.Evidence(At(12)));
        var expired = sessions.Challenge(id, "chrome", At(13), true);
        Assert.False(sessions.Report(packet with { Nonce = expired.Nonce, Sequence = 4 }, At(16)));
        var changed = sessions.Challenge(id, "chrome", At(17), true);
        Assert.True(sessions.Report(packet with { Nonce = changed.Nonce, Sequence = 5 }, At(17.1, "chrome:2")));
        Assert.Null(sessions.Evidence(At(17.2, "chrome:2")));
    }

    [Fact] public void ExplicitNoneEndsAttributionWhileTransientDoesNotExtendIt()
    {
        var sessions = new BrowserSessions(); var id = Guid.NewGuid().ToString();
        void Send(double at, long sequence, string type)
        {
            var probe = sessions.Challenge(id, "chrome", At(at), false);
            Assert.True(sessions.Report(new(id, probe.Nonce, sequence, type, "example.com"), At(at)));
        }
        Send(10, 1, "activity"); Send(10.4, 2, "transient");
        Assert.NotNull(sessions.Evidence(At(10.5))); Assert.Null(sessions.Evidence(At(11.6)));
        Send(12, 3, "activity"); Send(12.2, 4, "none"); Assert.Null(sessions.Evidence(At(12.3)));
        Assert.Equal(1, sessions.Connections(12.3)); Assert.Equal(0, sessions.Connections(18));
    }

    [Fact] public void ConflictingBrowserProfilesAreNotArbitrarilyAssigned()
    {
        var sessions = new BrowserSessions();
        foreach (var domain in new[] { "one.example", "two.example" })
        {
            var id = Guid.NewGuid().ToString(); var probe = sessions.Challenge(id, "chrome", At(10), false);
            sessions.Report(new(id, probe.Nonce, 1, "activity", domain), At(10.1));
        }
        Assert.Equal(2, sessions.Connections(10.2)); Assert.Null(sessions.Evidence(At(10.2)));
    }

    [Fact] public void DisablingTitlesInvalidatesInFlightConsentAndExistingTitleEvidence()
    {
        var sessions = new BrowserSessions(); var id = Guid.NewGuid().ToString();
        var first = sessions.Challenge(id, "chrome", At(10), true);
        sessions.Report(new(id, first.Nonce, 1, "activity", "example.com", "title"), At(10.1));
        Assert.Equal("title", sessions.Evidence(At(10.2))!.Title);
        var pending = sessions.Challenge(id, "chrome", At(10.3), true);
        sessions.DisableTitles(); Assert.Null(sessions.Evidence(At(10.4))!.Title);
        sessions.Report(new(id, pending.Nonce, 2, "activity", "example.com", "late title"), At(10.5));
        Assert.Null(sessions.Evidence(At(10.6))!.Title);
    }

    [Fact] public void RemovedSnippetFeatureIgnoresLegacyReports()
    {
        var sessions = new BrowserSessions(); var id = Guid.NewGuid().ToString();
        var probe = sessions.Challenge(id, "chrome", At(10), true);
        Assert.Null(probe.Rules); Assert.False(probe.Snippets);
        sessions.Report(new(id, probe.Nonce, 1, "activity", "example.com", "title", "legacy snippet"), At(10.1));
        Assert.Null(sessions.Evidence(At(10.2))!.Snippet);
        Assert.Equal("title", sessions.Evidence(At(10.2))!.Title);
    }

    [Fact] public async Task FirefoxRandomOriginRequiresFirefoxIdentityAndSupportsReporting()
    {
        var sessions = new BrowserSessions();
        var moment = At(10) with { App = new("firefox", "firefox") };
        await using var server = new BrowserHttpServer(0,
            (id, browser) => sessions.Challenge(id, browser, moment, false), report => sessions.Report(report, moment));
        await server.Start(); Assert.Null(server.Error);
        using var http = new HttpClient { BaseAddress = new Uri(server.Address!) };
        var origin = "moz-extension://" + Guid.NewGuid().ToString();
        http.DefaultRequestHeaders.Add("Origin", origin);
        http.DefaultRequestHeaders.Add("X-Riji-Extension", BrowserHttpServer.FirefoxExtensionId);
        var session = Guid.NewGuid().ToString();
        using var response = await http.PostAsJsonAsync("/v1/probe", new { session, browser = "firefox" });
        response.EnsureSuccessStatusCode();
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var probe = await response.Content.ReadFromJsonAsync<BrowserChallenge>();
        using var accepted = await http.PostAsJsonAsync("/v1/report", new BrowserReport(session, probe!.Nonce, 1, "activity", "bilibili.com"));
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal("bilibili.com", sessions.Evidence(moment)!.Domain);
        using var wrongBrowser = await http.PostAsJsonAsync("/v1/probe", new { session, browser = "chrome" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongBrowser.StatusCode);
        http.DefaultRequestHeaders.Remove("X-Riji-Extension");
        http.DefaultRequestHeaders.Add("X-Riji-Extension", BrowserHttpServer.ExtensionId);
        using var wrongIdentity = await http.PostAsJsonAsync("/v1/probe", new { session, browser = "chrome" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongIdentity.StatusCode);
        Assert.False(BrowserHttpServer.IsExtensionOrigin("https://" + Guid.NewGuid()));
        Assert.False(BrowserHttpServer.IsExtensionOrigin(origin + "/path"));
        Assert.False(BrowserHttpServer.IsExtensionOrigin("moz-extension://not-an-id"));
    }

    [Fact] public async Task LoopbackEndpointChecksExtensionRejectsForeignOriginAndConsumesChallenge()
    {
        var sessions = new BrowserSessions(); var moment = At(10);
        await using var server = new BrowserHttpServer(0,
            (id, browser) => sessions.Challenge(id, browser, moment, false), report => sessions.Report(report, moment));
        await server.Start(); Assert.Null(server.Error); Assert.NotNull(server.Address);
        using var http = new HttpClient { BaseAddress = new Uri(server.Address!) };
        var session = Guid.NewGuid().ToString();
        using var unauthenticated = await http.PostAsJsonAsync("/v1/probe", new { session, browser = "chrome" });
        Assert.Equal(HttpStatusCode.Forbidden, unauthenticated.StatusCode);
        http.DefaultRequestHeaders.Add("X-Riji-Extension", BrowserHttpServer.ExtensionId);
        http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        using var foreign = await http.PostAsJsonAsync("/v1/probe", new { session, browser = "chrome" });
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var preflightRequest = new HttpRequestMessage(HttpMethod.Options, "/v1/probe");
        preflightRequest.Headers.Add("Access-Control-Request-Headers", "X-Riji-Extension");
        using var foreignPreflight = await http.SendAsync(preflightRequest);
        Assert.Equal(HttpStatusCode.Forbidden, foreignPreflight.StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        http.DefaultRequestHeaders.Add("Origin", "chrome-extension://pngdbmhpmldhdhiihalmlfecglmhiibk");
        using var response = await http.PostAsJsonAsync("/v1/probe", new { session, browser = "chrome" });
        response.EnsureSuccessStatusCode(); var probe = await response.Content.ReadFromJsonAsync<BrowserChallenge>();
        var packet = new BrowserReport(session, probe!.Nonce, 1, "activity", "example.com");
        using var accepted = await http.PostAsJsonAsync("/v1/report", packet);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode); Assert.Equal("example.com", sessions.Evidence(moment)!.Domain);
        using var duplicate = await http.PostAsJsonAsync("/v1/report", packet);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        // Chromium extension fetches may omit Origin; the custom header is still required.
        using var originless = await http.PostAsJsonAsync("/v1/probe", new { session, browser = "chrome" });
        Assert.Equal(HttpStatusCode.OK, originless.StatusCode);
        http.DefaultRequestHeaders.Remove("X-Riji-Extension");
        http.DefaultRequestHeaders.Add("X-Riji-Extension", "another-extension");
        using var wrongExtension = await http.PostAsJsonAsync("/v1/probe", new { session, browser = "chrome" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongExtension.StatusCode);
    }
}
