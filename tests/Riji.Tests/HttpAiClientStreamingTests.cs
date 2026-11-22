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

    // Read large fixtures at normal network chunk sizes instead of the UTF-8 split-test size.
    private static HttpResponseMessage LargeEvents(string body)
    {
        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)));
        content.Headers.ContentType = new("text/event-stream");
        return new(HttpStatusCode.OK) { Content = content };
    }

    // Emit one protocol event without hand-escaping model content.
    private static string Delta(string? content = null, string? reasoning = null, string? finish = null) => "data: "
        + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content, reasoning_content = reasoning }, finish_reason = finish } } }) + "\n\n";

    // Generate oversized protocol overhead without allocating a complete 64 MB response.
    private sealed class RepeatedWire(byte[] bytes, int repeats) : MemoryStream
    {
        private int remaining = repeats;

        // Return a complete bounded comment or data block on each simulated network read.
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining-- <= 0) return ValueTask.FromResult(0);
            bytes.AsMemory().CopyTo(buffer);
            return ValueTask.FromResult(bytes.Length);
        }
    }

    // Simulate a transport exception only after a complete partial-content event has arrived.
    private sealed class FailureAfterData(byte[] bytes, Func<Exception> failure) : MemoryStream(bytes)
    {
        // Preserve real read ordering so the parser has already captured the text before failure.
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= Length) throw failure();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    // SSE metadata exceeding the old combined limit must not consume the decoded text budget.
    public async Task LargeProtocolOverheadWithShortTextSucceeds()
    {
        var padding = new string('p', 4096);
        var body = string.Concat(Enumerable.Repeat("data: " + JsonSerializer.Serialize(new { metadata = padding,
            choices = new[] { new { delta = new { content = "字" }, finish_reason = (string?)null } } }) + "\n\n", 600)) + Delta(finish: "stop");
        Assert.True(Encoding.UTF8.GetByteCount(body) > 2_000_000);
        using var http = new HttpClient(new Handler(() => LargeEvents(body)));
        Assert.Equal(new string('字', 600), await new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // Reasoning has its own allowance in SSE, ordinary JSON and the non-streaming fallback.
    public async Task LargeReasoningDoesNotConsumeBodyBudget(bool fallback)
    {
        var attempt = 0;
        var reasoning = new string('r', 3_000_000);
        var body = Delta(reasoning: reasoning) + Delta(content: "短摘要", finish: "stop");
        var handler = new Handler(() => fallback && ++attempt == 1
            ? new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"stream not supported\"}}") }
            : fallback ? new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] {
                new { finish_reason = "stop", message = new { content = "短摘要", reasoning_content = reasoning } } } }), Encoding.UTF8, "application/json") }
            : LargeEvents(body));
        using var http = new HttpClient(handler);
        Assert.Equal("短摘要", await new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None));
        Assert.Equal(fallback ? 2 : 1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // The decoded UTF-8 body limit applies consistently to SSE and JSON compatibility responses.
    public async Task OversizedBodyHasSpecificErrorAndBoundedHeadAndTail(bool json)
    {
        var output = "正文开头" + new string('文', 666_667) + "正文结尾";
        var handler = new Handler(() => json ? new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
            choices = new[] { new { finish_reason = "stop", message = new { content = output } } } }), Encoding.UTF8, "application/json") }
            : LargeEvents(Delta(content: output, finish: "stop")));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AiFailure>(() => new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None));
        Assert.Contains("正文超过 2 MB", error.Message);
        Assert.False(error.Retryable);
        Assert.Contains("正文开头", error.DiagnosticResponse);
        Assert.Contains("正文结尾", error.DiagnosticResponse);
        Assert.Contains("中间内容已省略", error.DiagnosticResponse);
        Assert.InRange(Encoding.UTF8.GetByteCount(error.DiagnosticResponse!), 1, 65536);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // The reasoning cap terminates runaway thinking while preserving bounded evidence.
    public async Task OversizedReasoningHasIndependentError(bool json)
    {
        var thought = "思考开头" + new string('r', 8_000_000) + "思考结尾";
        using var http = new HttpClient(new Handler(() => json ? new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop",
            message = new { content = "已写正文", reasoning_content = thought } } } }), Encoding.UTF8, "application/json") }
            : LargeEvents(Delta(content: "已写正文", reasoning: thought))));
        var error = await Assert.ThrowsAsync<AiFailure>(() => new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None));
        Assert.Contains("思考内容超过 8 MB", error.Message);
        Assert.Contains("已写正文", error.DiagnosticResponse);
        Assert.Contains("思考开头", error.DiagnosticResponse);
        Assert.Contains("思考结尾", error.DiagnosticResponse);
        Assert.InRange(Encoding.UTF8.GetByteCount(error.DiagnosticResponse!), 1, 65536);
    }

    [Fact]
    // Exact decoded UTF-8 bytes at the limit remain valid despite escaped JSON and SSE overhead.
    public async Task ExactUtf8BodyLimitSucceeds()
    {
        var text = new string('文', 666_666) + "ab";
        Assert.Equal(2_000_000, Encoding.UTF8.GetByteCount(text));
        using var http = new HttpClient(new Handler(() => LargeEvents(Delta(content: text, finish: "stop"))));
        Assert.Equal(text, await new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // Timeouts and connection loss retain received evidence and the existing network retry classification.
    public async Task TransportFailureKeepsPartialEvidence(bool timeout)
    {
        using var http = new HttpClient(new Handler(() =>
        {
            var content = new StreamContent(new FailureAfterData(Encoding.UTF8.GetBytes(Delta(content: "断线前的内容", reasoning: "断线前的思考")),
                () => timeout ? new OperationCanceledException() : new IOException("broken connection")));
            content.Headers.ContentType = new("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        }));
        var error = await Assert.ThrowsAsync<AiFailure>(() => new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None));
        Assert.True(error.NetworkFailure);
        Assert.True(error.Retryable);
        Assert.Contains(timeout ? "超时" : "连接在接收响应时中断", error.Message);
        Assert.Contains("断线前的内容", error.DiagnosticResponse);
        Assert.Contains("断线前的思考", error.DiagnosticResponse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // Protocol comments and malformed oversized lines both remain bounded despite the higher transport cap.
    public async Task ProtocolOverheadAndSingleLineAreStillLimited(bool unterminatedLine)
    {
        var block = Encoding.UTF8.GetBytes(unterminatedLine ? new string('x', 8192) : ":" + new string('x', 8189) + "\n\n");
        using var http = new HttpClient(new Handler(() =>
        {
            var content = new StreamContent(new RepeatedWire(block, 8000));
            content.Headers.ContentType = new("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        }));
        var error = await Assert.ThrowsAsync<AiFailure>(() => new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None));
        Assert.Contains(unterminatedLine ? "单行流式数据超过 16 MB" : "传输数据超过 64 MB", error.Message);
        Assert.Contains("正文 UTF-8 字节：0", error.DiagnosticResponse);
        Assert.InRange(Encoding.UTF8.GetByteCount(error.DiagnosticResponse!), 1, 65536);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("length")]
    [InlineData("eof")]
    [InlineData("error")]
    // Failed partial streams must expose their received text without returning it as a successful answer.
    public async Task FailedStreamsKeepDecodedAndWireEvidence(string ending)
    {
        var body = Delta(content: "收到的部分正文", reasoning: "收到的思考") + (ending switch
        {
            "malformed" => "data: {invalid-json}\n\n",
            "length" => Delta(finish: "length"),
            "error" => "event: error\ndata: {\"message\":\"服务异常\"}\n\n",
            _ => ""
        });
        using var http = new HttpClient(new Handler(() => Events(body)));
        var error = await Assert.ThrowsAsync<AiFailure>(() => new HttpAiClient(http).Text(Configuration, "key", "hello", CancellationToken.None));
        Assert.Contains("收到的部分正文", error.DiagnosticResponse);
        Assert.Contains("收到的思考", error.DiagnosticResponse);
        Assert.Contains("原始响应片段", error.DiagnosticResponse);
        Assert.Equal(ending is "length" or "malformed" or "error", error.Retryable);
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
