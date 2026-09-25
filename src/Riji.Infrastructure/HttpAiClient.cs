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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new AiFailure($"读取模型列表失败：HTTP {(int)response.StatusCode}。", (int)response.StatusCode >= 500);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return doc.RootElement.GetProperty("data").EnumerateArray().Select(item => new ModelInfo(item.GetProperty("id").GetString()!, item.TryGetProperty("context_length", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() / 1000 : null)).OrderBy(item => item.Id).ToArray();
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new AiFailure("读取模型列表超时。", true, networkFailure: true); }
    }
    public async Task<string> Text(AiConfiguration configuration, string key, string prompt, CancellationToken cancellation)
        => await SendStreaming(configuration, key, new object[] { new { role = "user", content = prompt } }, cancellation);

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

    private static HttpRequestMessage CreateRequest(AiConfiguration configuration, string key, object[] messages, bool streaming)
    {
        AiConfiguration.ValidateEndpoint(configuration.Endpoint);
        if (string.IsNullOrWhiteSpace(configuration.Model) || configuration.Model.Length > 200 || string.IsNullOrWhiteSpace(key)) throw new AiFailure("请补全模型名称和 API Key。", authorization: true);
        var endpoint = configuration.Endpoint.TrimEnd('/');
        if (!endpoint.EndsWith("/chat/completions", StringComparison.Ordinal)) endpoint += "/chat/completions";
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(new { model = configuration.Model, messages, stream = streaming }), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellation)
    {
        var status = (int)response.StatusCode;
        if (status is 401 or 403)
            throw new AiFailure("服务拒绝授权，请检查 API Key 和模型权限。", authorization: true, diagnosticResponse: await ReadDiagnosticBody(response.Content, cancellation));
        if (!response.IsSuccessStatusCode)
            throw new AiFailure($"AI 服务返回 HTTP {status}。", status is 408 or 429 || status >= 500, diagnosticResponse: await ReadDiagnosticBody(response.Content, cancellation));
    }

    private static async Task<byte[]> ReadLimited(Stream stream, CancellationToken cancellation, Action? onData = null)
    {
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(chunk, cancellation)) > 0)
        {
            if (buffer.Length + read > 2_000_000) throw new AiFailure("AI 响应超过大小限制。");
            buffer.Write(chunk, 0, read); onData?.Invoke();
        }
        return buffer.ToArray();
    }

    private static string ParseCompletion(byte[] responseBytes, string operation)
    {
        using var doc = JsonDocument.Parse(responseBytes);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var finishReason = choice.GetProperty("finish_reason").GetString();
        if (finishReason != "stop")
        {
            var detail = string.IsNullOrWhiteSpace(finishReason) ? "未提供" : finishReason;
            throw new AiFailure($"AI 未正常完成本次{operation}（finish_reason={detail}），结果未保存。若为 length，通常表示输出长度受限；若为 content_filter，通常表示内容策略拦截。", finishReason == "length", diagnosticResponse: Encoding.UTF8.GetString(responseBytes));
        }
        var content = choice.GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) throw new AiFailure("AI 返回空内容。", true);
        return content;
    }

    private static bool StreamingUnsupported(string diagnostic)
    {
        var message = diagnostic; var code = "";
        try
        {
            using var doc = JsonDocument.Parse(diagnostic);
            var root = doc.RootElement;
            var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var nested) ? nested : root;
            if (error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("param", out var param) && param.ValueKind == JsonValueKind.String
                    && param.GetString()?.Equals("stream", StringComparison.OrdinalIgnoreCase) == true) return true;
                if (error.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String) message = detail.GetString() ?? "";
                if (error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) code = value.GetString() ?? "";
            }
        }
        catch (JsonException) { }
        var description = message + " " + code;
        return description.Contains("stream", StringComparison.OrdinalIgnoreCase)
            && (description.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
                || description.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                || description.Contains("not implemented", StringComparison.OrdinalIgnoreCase)
                || description.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                || description.Contains("不支持", StringComparison.Ordinal));
    }

    private async Task<string> SendStreaming(AiConfiguration configuration, string key, object[] messages, CancellationToken cancellation)
    {
        using var request = CreateRequest(configuration, key, messages, streaming: true);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        total.CancelAfter(TimeSpan.FromMinutes(5));
        using var progress = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        progress.CancelAfter(TimeSpan.FromMinutes(2));
        var receivedContent = false;
        void OnContent() { receivedContent = true; progress.CancelAfter(TimeSpan.FromSeconds(60)); }
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, progress.Token);
            if ((int)response.StatusCode is 400 or 422 or 501)
            {
                var diagnostic = await ReadDiagnosticBody(response.Content, progress.Token);
                if (StreamingUnsupported(diagnostic)) return await Send(configuration, key, messages, total.Token, TimeSpan.FromSeconds(120), "生成");
                throw new AiFailure($"AI 服务返回 HTTP {(int)response.StatusCode}。", (int)response.StatusCode >= 500, diagnosticResponse: diagnostic);
            }
            await EnsureSuccess(response, progress.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(progress.Token);
            string result;
            if (response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
                result = await ReadEventStream(stream, OnContent, progress.Token);
            else result = ParseCompletion(await ReadLimited(stream, progress.Token, OnContent), "生成");
            progress.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            var reason = total.IsCancellationRequested ? "总计超过 5 分钟" : receivedContent ? "连续 60 秒未收到内容" : "2 分钟内未收到内容";
            throw new AiFailure($"AI 文本请求超时：{reason}。", true, networkFailure: true);
        }
        catch (HttpRequestException) { throw new AiFailure("无法连接 AI 服务。", true, networkFailure: true); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        { throw new AiFailure("AI 服务返回了无效的流式响应。", true); }
    }

    private static async Task<string> ReadEventStream(Stream stream, Action onContent, CancellationToken cancellation)
    {
        var output = new StringBuilder(); var data = new StringBuilder();
        using var lineBytes = new MemoryStream();
        var chunk = new byte[8192]; long receivedBytes = 0;
        string? finishReason = null; var eventType = "";

        string Completed()
        {
            if (finishReason != "stop")
            {
                var detail = string.IsNullOrWhiteSpace(finishReason) ? "未提供" : finishReason;
                throw new AiFailure($"AI 未正常完成本次生成（finish_reason={detail}），结果未保存。", finishReason == "length");
            }
            cancellation.ThrowIfCancellationRequested();
            var result = output.ToString();
            if (string.IsNullOrWhiteSpace(result)) throw new AiFailure("AI 返回空内容。", true);
            return result;
        }

        bool Dispatch()
        {
            if (data.Length == 0) return false;
            var payload = data.ToString(); data.Clear();
            if (eventType == "error") throw new AiFailure("AI 服务返回流式错误。", true, diagnosticResponse: payload);
            eventType = "";
            if (payload == "[DONE]") return true;
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out _)) throw new AiFailure("AI 服务返回流式错误。", true, diagnosticResponse: payload);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return false;
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                { output.Append(text); onContent(); }
                if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String && reasoning.GetString() is { Length: > 0 }) onContent();
                if (delta.TryGetProperty("reasoning", out reasoning) && reasoning.ValueKind == JsonValueKind.String && reasoning.GetString() is { Length: > 0 }) onContent();
            }
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            { finishReason = finish.GetString(); return true; }
            return false;
        }

        bool Line(string line)
        {
            line = line.TrimStart('\uFEFF');
            if (line.Length == 0) return Dispatch();
            if (line.StartsWith("event:", StringComparison.Ordinal)) eventType = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
            return false;
        }

        int read;
        while ((read = await stream.ReadAsync(chunk, cancellation)) > 0)
        {
            receivedBytes += read;
            if (receivedBytes > 2_000_000) throw new AiFailure("AI 响应超过大小限制。");
            for (var index = 0; index < read; index++)
            {
                if (chunk[index] != (byte)'\n') { lineBytes.WriteByte(chunk[index]); continue; }
                var line = Encoding.UTF8.GetString(lineBytes.GetBuffer(), 0, (int)lineBytes.Length).TrimEnd('\r');
                lineBytes.SetLength(0);
                if (Line(line)) return Completed();
            }
        }
        if (lineBytes.Length > 0 && Line(Encoding.UTF8.GetString(lineBytes.GetBuffer(), 0, (int)lineBytes.Length).TrimEnd('\r'))) return Completed();
        if (Dispatch()) return Completed();
        return Completed();
    }

    private async Task<string> Send(AiConfiguration configuration, string key, object[] messages, CancellationToken cancellation,
        TimeSpan? requestTimeout = null, string operation = "识别")
    {
        using var request = CreateRequest(configuration, key, messages, streaming: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(90));
        string? diagnosticResponse = null;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await EnsureSuccess(response, timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var responseBytes = await ReadLimited(stream, timeout.Token);
            diagnosticResponse = Encoding.UTF8.GetString(responseBytes);
            return ParseCompletion(responseBytes, operation);
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
