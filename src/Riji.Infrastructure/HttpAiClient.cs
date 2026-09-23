using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Riji.Core;

namespace Riji.Infrastructure;

// Use a provider-configurable Chat Completions wire format, without logging request bodies or credentials.
public sealed record ModelInfo(string Id, int? ContextK);
public sealed class HttpAiClient(HttpClient client)
{
    public async Task<bool> CanReach(AiConfiguration configuration, CancellationToken cancellation)
    {
        var endpoint = configuration.Endpoint.TrimEnd('/');
        if (!endpoint.EndsWith("/chat/completions", StringComparison.Ordinal)) endpoint += "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return true;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return false; }
    }

    public async Task<ModelInfo[]> Models(string endpoint, string key, CancellationToken cancellation = default)
    {
        AiConfiguration.ValidateEndpoint(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.TrimEnd('/') + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await client.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode) throw new AiFailure($"读取模型列表失败：HTTP {(int)response.StatusCode}。", (int)response.StatusCode >= 500);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(item => new ModelInfo(item.GetProperty("id").GetString()!, item.TryGetProperty("context_length", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() / 1000 : null)).OrderBy(item => item.Id).ToArray();
    }
    public async Task<string> Text(AiConfiguration configuration, string key, string prompt, CancellationToken cancellation)
        => await Send(configuration, key, new object[] { new { role = "user", content = prompt } }, cancellation);

    public async Task<RecognitionResult> Recognize(AiConfiguration configuration, string key, byte[] png, Category[] categories, CancellationToken cancellation, string? savedPrompt = null)
    {
        var prompt = "描述截图中可观察到的活动，按活动目的分类，不推断任务完成。只返回 JSON：{\"description\":\"非空描述\",\"categoryId\":\"分类ID\",\"confidence\":0到1}。分类：" + JsonSerializer.Serialize(categories.Where(x => x.Enabled));
        var response = await Send(configuration, key, new object[] { new { role = "user", content = new object[] {
            new { type = "text", text = savedPrompt ?? prompt }, new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(png) } } } } }, cancellation);
        string diagnosticResponse = response;
        var clean = response.Trim();
        if (clean.StartsWith("```")) { var firstLine = clean.IndexOf('\n'); if (firstLine >= 0 && clean.EndsWith("```")) clean = clean[(firstLine + 1)..^3].Trim(); }
        try
        {
            using var parsed = JsonDocument.Parse(clean);
            if (!parsed.RootElement.TryGetProperty("confidence", out var confidence) || confidence.ValueKind != JsonValueKind.Number) throw new JsonException();
            RecognitionResult result;
            if (savedPrompt is not null)
            {
                if (!parsed.RootElement.TryGetProperty("categoryName", out var categoryName) || categoryName.ValueKind != JsonValueKind.String
                    || !parsed.RootElement.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String) throw new JsonException();
                var selected = categories.SingleOrDefault(c => c.Enabled && c.Name == categoryName.GetString());
                if (selected is null) throw new AiFailure("识别服务返回了本次分类列表之外的名称。", true, diagnosticResponse: response);
                result = new(description.GetString()!, selected.Id, confidence.GetDouble());
            }
            else result = JsonSerializer.Deserialize<RecognitionResult>(clean, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException();
            try { RecognitionValidation.Validate(result, categories); }
            catch (AiFailure failure) { throw new AiFailure(failure.Message, failure.Retryable, failure.Authorization, response); }
            return result;
        }
        catch (JsonException) { throw new AiFailure("识别服务返回了无法解析的结构。", true, diagnosticResponse: diagnosticResponse); }
    }

    private async Task<string> Send(AiConfiguration configuration, string key, object[] messages, CancellationToken cancellation)
    {
        AiConfiguration.ValidateEndpoint(configuration.Endpoint);
        if (string.IsNullOrWhiteSpace(configuration.Model) || configuration.Model.Length > 200 || string.IsNullOrWhiteSpace(key)) throw new AiFailure("请补全模型名称和 API Key。", authorization: true);
        var endpoint = configuration.Endpoint.TrimEnd('/');
        if (!endpoint.EndsWith("/chat/completions", StringComparison.Ordinal)) endpoint += "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(new { model = configuration.Model, messages, stream = false }), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        string? diagnosticResponse = null;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (status is 401 or 403)
                throw new AiFailure("服务拒绝授权，请检查 API Key 和模型权限。", authorization: true, diagnosticResponse: await ReadDiagnosticBody(response.Content, timeout.Token));
            if (!response.IsSuccessStatusCode)
                throw new AiFailure($"AI 服务返回 HTTP {status}。", status is 408 or 429 || status >= 500, diagnosticResponse: await ReadDiagnosticBody(response.Content, timeout.Token));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            { if (buffer.Length + read > 2_000_000) throw new AiFailure("AI 响应超过大小限制。"); buffer.Write(chunk, 0, read); }
            var responseBytes = buffer.ToArray();
            diagnosticResponse = Encoding.UTF8.GetString(responseBytes);
            using var doc = JsonDocument.Parse(responseBytes);
            var choice = doc.RootElement.GetProperty("choices")[0];
            var finishReason = choice.GetProperty("finish_reason").GetString();
            if (finishReason != "stop")
            {
                var detail = string.IsNullOrWhiteSpace(finishReason) ? "未提供" : finishReason;
                throw new AiFailure($"AI 未正常完成本次识别（finish_reason={detail}），结果未保存。若为 length，通常表示输出长度受限；若为 content_filter，通常表示内容策略拦截。", finishReason == "length", diagnosticResponse: diagnosticResponse);
            }
            var content = choice.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) throw new AiFailure("AI 返回空内容。", true);
            return content;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new AiFailure("AI 请求超时。", true, networkFailure: true); }
        catch (HttpRequestException) { throw new AiFailure("无法连接 AI 服务。", true, networkFailure: true); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { throw new AiFailure("AI 服务返回了无效响应。", true, diagnosticResponse: diagnosticResponse); }
    }

    private static async Task<string> ReadDiagnosticBody(HttpContent content, CancellationToken cancellation)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellation);
        using var buffer = new MemoryStream(); var chunk = new byte[4096]; int read;
        while (buffer.Length < 65536 && (read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, 65536 - (int)buffer.Length)), cancellation)) > 0)
            buffer.Write(chunk, 0, read);
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        return buffer.Length == 65536 ? text + "\n[响应正文已截断至 64 KiB]" : text;
    }
}
