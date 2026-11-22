using System.Text;
using Riji.Core;

namespace Riji.Infrastructure;

public sealed partial class DiagnosticLog
{
    private const int TextReportsRetained = 40;

    // Save bounded local response evidence without request prompts, headers or API credentials.
    public void TextFailure(string summaryId, string callId, AiFailure error, AiConfiguration configuration, string apiKey)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                // Redact the active credential before applying the per-field size bound.
                string Clean(string? value)
                {
                    var clean = value ?? "（无）";
                    if (!string.IsNullOrEmpty(apiKey)) clean = clean.Replace(apiKey, "[API Key 已隐藏]", StringComparison.Ordinal);
                    var bytes = Encoding.UTF8.GetBytes(clean);
                    return bytes.Length <= 65536 ? clean : Encoding.UTF8.GetString(bytes, 0, 65536) + "\n[诊断内容已截断]";
                }
                // Render model-controlled response excerpts only as escaped text.
                string E(string? value) => System.Net.WebUtility.HtmlEncode(Clean(value));
                var safeId = new string(summaryId.Where(char.IsLetterOrDigit).Take(32).ToArray());
                var path = Path.Combine(DirectoryPath, $"text-failure-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{safeId}-{Guid.NewGuid():N}.html");
                var html = "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>日迹文本 AI 故障详情</title>"
                    + "<style>body{font:14px Segoe UI,sans-serif;max-width:1100px;margin:32px auto;padding:0 20px}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f2f2f2;padding:16px}p{overflow-wrap:anywhere}</style>"
                    + $"<h1>文本 AI 故障详情</h1><p>失败时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}</p>"
                    + $"<p>总结 ID：{E(summaryId)}</p><p>调用：{E(callId)}</p><p>模型：{E(configuration.Model)}</p>"
                    + $"<p>接口：{E(configuration.Endpoint)}</p><p>原因：{E(error.Message)}</p>"
                    + "<p>以下是失败时已接收的数据片段，不代表完整结果，也不会作为摘要保存。没有收到的内容无法恢复。</p>"
                    + $"<pre>{E(error.DiagnosticResponse)}</pre></html>";
                File.WriteAllText(path, html, new UTF8Encoding(false));
                foreach (var old in TextReports().Skip(TextReportsRetained)) File.Delete(old);
                WriteTextFailureIndex();
                WriteFailed = false;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { WriteFailed = true; }
        }
    }

    // Open a small index of the newest forty text failures without exposing arbitrary file paths.
    public string TextFailureIndex()
    {
        lock (gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            return WriteTextFailureIndex();
        }
    }

    // Order only text reports so retention never deletes recognition reports or general diagnostics.
    private IEnumerable<string> TextReports() => Directory.GetFiles(DirectoryPath, "text-failure-*.html")
        .OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(Path.GetFileName);

    // Keep the index readable even before the first failure or after older reports are removed.
    private string WriteTextFailureIndex()
    {
        var path = Path.Combine(DirectoryPath, "text-failures.html");
        var links = TextReports().Take(TextReportsRetained).Select(file => $"<li><a href=\"{System.Net.WebUtility.HtmlEncode(new Uri(file).AbsoluteUri)}\">{System.Net.WebUtility.HtmlEncode(Path.GetFileNameWithoutExtension(file))}</a></li>").ToArray();
        File.WriteAllText(path, "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>日迹文本 AI 故障日志</title>"
            + "<style>body{font:14px Segoe UI,sans-serif;margin:32px}li{padding:10px 0}</style><h1>文本 AI 故障日志</h1>"
            + "<p>仅保存在本机，保留最近 40 次失败。报告包含接收到的有限片段；旧版本未保存的输出无法恢复。</p><ul>"
            + (links.Length == 0 ? "<li>暂无文本 AI 故障报告。</li>" : string.Join("", links)) + "</ul></html>", new UTF8Encoding(false));
        return path;
    }
}
