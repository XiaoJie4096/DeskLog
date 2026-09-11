using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Riji.Core;

namespace Riji.Infrastructure;

// Restrict loopback reporting to the extension's browser origin and a non-simple request header.
public sealed class BrowserHttpServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly Func<string, string, BrowserChallenge> challenge;
    private readonly Func<BrowserReport, bool> report;
    public const string ExtensionId = "pngdbmhpmldhdhiihalmlfecglmhiibk";
    private const string ExtensionOrigin = "chrome-extension://" + ExtensionId;
    public const string FirefoxExtensionId = "riji-browser@riji.local";

    // Firefox assigns a random installation origin; its stable extension ID is sent separately.
    public static bool IsExtensionOrigin(string origin) => origin == ExtensionOrigin ||
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == "moz-extension"
        && Guid.TryParseExact(uri.Host, "D", out _) && uri.UserInfo == "" && uri.IsDefaultPort
        && uri.AbsolutePath == "/" && uri.Query == "" && uri.Fragment == "";
    public string? Address => app.Urls.FirstOrDefault();
    public string? Error { get; private set; }

    public BrowserHttpServer(int port, Func<string, string, BrowserChallenge> challenge, Func<BrowserReport, bool> report)
    {
        this.challenge = challenge; this.report = report;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(BrowserHttpServer).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 8192;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.MaxConcurrentConnections = 64;
        });
        app = builder.Build();
        app.Run(Handle);
    }

    public async Task Start()
    {
        try { await app.StartAsync(); }
        catch (Exception) { Error = "本机浏览器连接端口无法启动，可能已被占用；没有改用其他端口。"; }
    }

    private async Task Handle(HttpContext context)
    {
        var request = context.Request; var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (request.Host.Host != "127.0.0.1" || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)) { response.StatusCode = 403; return; }
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsExtensionOrigin(origin)) { response.StatusCode = 403; return; }
        if (origin.Length > 0) response.Headers.AccessControlAllowOrigin = origin;
        if (request.Method == "OPTIONS")
        {
            response.Headers.AccessControlAllowMethods = "POST";
            response.Headers.AccessControlAllowHeaders = "Content-Type, X-Riji-Extension";
            response.StatusCode = 204; return;
        }
        // This excludes ordinary web pages, not other processes already running as the local user.
        var extension = request.Headers["X-Riji-Extension"].ToString();
        if (extension is not (ExtensionId or FirefoxExtensionId)
            || origin.Length > 0 && (origin == ExtensionOrigin) != (extension == ExtensionId)) { response.StatusCode = 403; return; }
        if (request.Method != "POST" || !request.HasJsonContentType()) { response.StatusCode = 405; return; }
        try
        {
            if (request.Path == "/v1/probe")
            {
                var body = await request.ReadFromJsonAsync<ProbeRequest>(context.RequestAborted) ?? throw new ArgumentException();
                if (extension == FirefoxExtensionId ? body.Browser != "firefox" : body.Browser is not ("chrome" or "msedge"))
                { response.StatusCode = 403; return; }
                await response.WriteAsJsonAsync(challenge(body.Session, body.Browser), context.RequestAborted);
            }
            else if (request.Path == "/v1/report")
            {
                var body = await request.ReadFromJsonAsync<BrowserReport>(context.RequestAborted) ?? throw new ArgumentException();
                response.StatusCode = report(body) ? 204 : 409;
            }
            else response.StatusCode = 404;
        }
        catch (Exception e) when (e is ArgumentException or JsonException or BadHttpRequestException) { response.StatusCode = 400; }
        catch (OperationCanceledException) { }
        catch (Exception) { response.StatusCode = 503; }
    }

    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    private sealed record ProbeRequest(string Session, string Browser);
}
