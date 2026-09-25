using System.Net;
using System.Text;
using System.Text.Json;
using Riji.Core;
using Riji.Infrastructure;
using Xunit;

namespace Riji.Tests;

public sealed class HttpAiClientStreamingTests
{
    private static readonly AiConfiguration Configuration = new("https://example.com/v1", "text-model", "protected-key");

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        public string? Body;
        public List<string> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(Body);
            return response();
        }
    }

    private sealed class SmallReads(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }

    private sealed class BlockingAfterData(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static HttpResponseMessage Events(string body)
    {
        var content = new StreamContent(new SmallReads(Encoding.UTF8.GetBytes(body)));
        content.Headers.ContentType = new("text/event-stream");
        return new(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public async Task TextCollectsChunkedContentButNotReasoning()
    {
        var handler = new Handler(() => Events(
            ": keepalive\n\n" +
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"思考中\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"你\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"好\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n"));
        using var http = new HttpClient(handler);

        var result = await new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None);

        Assert.Equal("你好", result);
        Assert.Equal(1, handler.Calls);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.True(request.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("text-model", request.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task IncompleteOrTruncatedStreamCannotBeSavedAsSuccess()
    {
        foreach (var ending in new[] { "", "data: [DONE]\n\n", "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n" })
        {
            var handler = new Handler(() => Events("data: {\"choices\":[{\"delta\":{\"content\":\"部分结果\"},\"finish_reason\":null}]}\n\n" + ending));
            using var http = new HttpClient(handler);
            await Assert.ThrowsAsync<AiFailure>(() => new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None));
        }
    }

    [Fact]
    public async Task JsonResponseToStreamingRequestUsesSameResponse()
    {
        var handler = new Handler(() => new(HttpStatusCode.OK) { Content = new StringContent(
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"完成\"}}]}", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);

        Assert.Equal("完成", await new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationDoesNotBecomeTimeoutOrPartialSuccess()
    {
        var handler = new Handler(() =>
        {
            var content = new StreamContent(new BlockingAfterData(Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"部分\"},\"finish_reason\":null}]}\n\n")));
            content.Headers.ContentType = new("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HttpAiClient(http).Text(Configuration, "key", "hello", cancellation.Token));
    }

    [Fact]
    public async Task ExplicitUnsupportedStreamFallsBackOnce()
    {
        var attempt = 0;
        var handler = new Handler(() => ++attempt == 1
            ? new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"stream is not supported\"}}") }
            : new(HttpStatusCode.OK) { Content = new StringContent(
                "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"兼容结果\"}}]}", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);

        Assert.Equal("兼容结果", await new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None));
        Assert.Equal(2, handler.Calls);
        using var streamRequest = JsonDocument.Parse(handler.Requests[0]);
        using var fallbackRequest = JsonDocument.Parse(handler.Requests[1]);
        Assert.True(streamRequest.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(fallbackRequest.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task OtherProviderErrorsDoNotResendThePrompt()
    {
        var handler = new Handler(() => new(HttpStatusCode.BadRequest)
        { Content = new StringContent("{\"error\":{\"message\":\"model not found\"}}") });
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<AiFailure>(() =>
            new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }
}
