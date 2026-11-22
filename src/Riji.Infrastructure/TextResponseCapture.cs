using System.Text;
using Riji.Core;

namespace Riji.Infrastructure;

// Bound each text response independently from SSE framing and retain small diagnostic excerpts.
internal sealed class TextResponseCapture
{
    internal const int OutputLimit = 2_000_000;
    internal const int ReasoningLimit = 8_000_000;
    internal const int TransportLimit = 64_000_000;
    internal const int EventLimit = 16_000_000;
    private readonly Excerpt output = new(), reasoning = new(), wire = new();
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    public long TransportBytes { get; private set; }
    public long OutputBytes { get; private set; }
    public long ReasoningBytes { get; private set; }
    public int Events { get; set; }
    public string? FinishReason { get; set; }

    // Count actual wire bytes, including comments and protocol metadata, without retaining the stream.
    public void Receive(ReadOnlySpan<byte> bytes)
    {
        TransportBytes += bytes.Length;
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = decoder.GetChars(bytes, chars, flush: false);
        wire.Append(chars.AsSpan(0, count));
        if (TransportBytes > TransportLimit) throw new AiFailure("AI 文本响应的传输数据超过 64 MB 限制（包含流式包装）。");
    }

    // Count decoded content separately so protocol overhead cannot consume the text budget.
    public void Content(string text)
    {
        OutputBytes += Encoding.UTF8.GetByteCount(text);
        output.Append(text);
        if (OutputBytes > OutputLimit) throw new AiFailure("AI 正文超过 2 MB 大小限制。");
    }

    // Observe reasoning only for progress and diagnostics, never as the successful summary text.
    public void Reasoning(string text)
    {
        ReasoningBytes += Encoding.UTF8.GetByteCount(text);
        reasoning.Append(text);
        if (ReasoningBytes > ReasoningLimit) throw new AiFailure("AI 思考内容超过 8 MB 大小限制。");
    }

    // Keep diagnostic evidence under 64 KiB even if an upstream error contains a large body.
    public string Describe(string? errorResponse = null)
    {
        var error = new Excerpt();
        if (errorResponse is not null) error.Append(errorResponse);
        return $"已接收传输字节：{TransportBytes} / {TransportLimit}\n正文 UTF-8 字节：{OutputBytes} / {OutputLimit}\n"
            + $"思考 UTF-8 字节：{ReasoningBytes} / {ReasoningLimit}\n流式事件数：{Events}\nfinish_reason：{FinishReason ?? "未收到"}\n"
            + $"\n【正文片段】\n{output}\n\n【思考片段】\n{reasoning}\n\n【原始响应片段】\n{wire}"
            + (errorResponse is null ? "" : $"\n\n【服务错误详情】\n{error}");
    }

    // Retain the first and last 2048 UTF-16 code units while discarding the middle.
    private sealed class Excerpt
    {
        private const int Size = 2048;
        private readonly StringBuilder head = new(), tail = new();
        private long length;

        // Append bounded text without duplicating the full response in diagnostic memory.
        public void Append(ReadOnlySpan<char> text)
        {
            length += text.Length;
            var take = Math.Min(Size - head.Length, text.Length);
            head.Append(text[..take]);
            text = text[take..];
            if (text.Length >= Size) { tail.Clear(); tail.Append(text[^Size..]); }
            else
            {
                tail.Append(text);
                if (tail.Length > Size) tail.Remove(0, tail.Length - Size);
            }
        }

        // Mark truncation explicitly so an excerpt cannot be mistaken for the complete output.
        public override string ToString() => length == 0 ? "（未收到）"
            : head.ToString() + (length > Size * 2 ? "\n[中间内容已省略，仅保留首尾片段]\n" : "") + tail;
    }
}
