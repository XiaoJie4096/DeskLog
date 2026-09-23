using System.Text;
using System.Text.Json;
using Riji.Core;

namespace Riji.Infrastructure;

public enum DiagnosticEvent { Startup, WebViewStartup, StorageCommit, Command, Snapshot, MaintenanceRecovery }

// General diagnostics omit content; recognition failure reports retain the evidence needed for local debugging.
public sealed class DiagnosticLog(string dataDirectory, int maxBytes = 262144, int retainedFiles = 4)
{
    private readonly object gate = new();
    private readonly Dictionary<DiagnosticEvent, long> last = [];
    public string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "Logs");
    public bool WriteFailed { get; private set; }

    public void RecognitionFailure(string? jobId, DateTimeOffset? sampledAt, string stage, Exception error,
        string? endpoint = null, string? model = null, string? prompt = null, string? screenshotPath = null, string? apiKey = null)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var response = (error as AiFailure)?.DiagnosticResponse;
                if (!string.IsNullOrEmpty(apiKey) && response is not null) response = response.Replace(apiKey, "[API Key 已隐藏]", StringComparison.Ordinal);
                if (prompt is not null && !string.IsNullOrEmpty(apiKey)) prompt = prompt.Replace(apiKey, "[API Key 已隐藏]", StringComparison.Ordinal);
                var screenshotUri = screenshotPath is not null && File.Exists(screenshotPath) ? new Uri(Path.GetFullPath(screenshotPath)).AbsoluteUri : null;
                WriteFailureReport(jobId, sampledAt, stage, error, endpoint, model, prompt, screenshotPath, screenshotUri, response);
                WriteRecognitionFailureIndex(Path.Combine(DirectoryPath, "recognition-failures.html"));
                WriteFailed = false;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            { WriteFailed = true; }
        }
    }

    public string RecognitionFailureIndex()
    {
        lock (gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "recognition-failures.html");
            WriteRecognitionFailureIndex(path);
            return path;
        }
    }

    public void Failure(DiagnosticEvent code, Exception error)
    {
        lock (gate)
        {
            var now = Environment.TickCount64;
            if (last.TryGetValue(code, out var previous) && now - previous < 60000) return;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "diagnostic.jsonl");
                var line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, code = code.ToString(), exception = error.GetType().Name, hresult = error.HResult }) + "\n";
                Rotate(path, line);
                File.AppendAllText(path, line, new UTF8Encoding(false));
                last[code] = now; WriteFailed = false;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            { WriteFailed = true; }
        }
    }

    private void Rotate(string path, string line)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) <= maxBytes) return;
        for (var index = retainedFiles; index >= 1; index--)
        {
            var source = index == 1 ? path : path + "." + (index - 1);
            if (File.Exists(source)) File.Move(source, path + "." + index, overwrite: true);
        }
    }

    private void WriteFailureReport(string? jobId, DateTimeOffset? sampledAt, string stage, Exception error,
        string? endpoint, string? model, string? prompt, string? screenshotPath, string? screenshotUri, string? response)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var safeId = string.IsNullOrWhiteSpace(jobId) ? "unknown" : new string(jobId.Where(char.IsLetterOrDigit).Take(32).ToArray());
        var path = Path.Combine(DirectoryPath, $"recognition-failure-{stamp}-{safeId}.html");
        static string E(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "（无）");
        var image = screenshotUri is null ? "<p>该任务没有可查看的完整截图文件。</p>" : $"<p><a href=\"{E(screenshotUri)}\">打开原始截图</a></p><img alt=\"识别时的截图\" src=\"{E(screenshotUri)}\">";
        var html = $"<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>日迹识别故障详情</title>" +
            "<style>body{font:14px Segoe UI, sans-serif;max-width:1100px;margin:32px auto;padding:0 20px;background:#171b1b;color:#eee}h1{font-size:22px}section{margin:24px 0;padding-top:14px;border-top:1px solid #45504c}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#222928;padding:14px;border-radius:6px;max-height:55vh;overflow:auto}img{display:block;max-width:100%;height:auto;border:1px solid #45504c}a{color:#ddb777}</style><h1>截图识别故障详情</h1>" +
            $"<section><p><b>失败时间：</b>{E(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"))}</p><p><b>采样时间：</b>{E(sampledAt?.ToString("O"))}</p><p><b>任务 ID：</b>{E(jobId)}</p><p><b>失败阶段：</b>{E(stage)}</p><p><b>具体原因：</b>{E(RecognitionPipeline.DescribeFailure(stage, error))}</p><p><b>异常类型 / HRESULT：</b>{E(error.GetType().Name)} / 0x{error.HResult:X8}</p><p><b>接口：</b>{E(endpoint)}</p><p><b>模型：</b>{E(model)}</p><p><b>截图文件：</b>{E(screenshotPath)}</p></section>" +
            $"<section><h2>识别时的截图</h2>{image}</section><section><h2>实际发送的提示词</h2><pre>{E(prompt)}</pre></section><section><h2>AI 服务原始响应正文（最多 64 KiB）</h2><pre>{E(response)}</pre></section></html>";
        File.WriteAllText(path, html, new UTF8Encoding(false));
    }

    private void WriteRecognitionFailureIndex(string path)
    {
        var reports = Directory.GetFiles(DirectoryPath, "recognition-failure-*.html")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(file => $"<li><a href=\"{System.Net.WebUtility.HtmlEncode(new Uri(file).AbsoluteUri)}\">{System.Net.WebUtility.HtmlEncode(Path.GetFileNameWithoutExtension(file))}</a> · {File.GetLastWriteTime(file):yyyy-MM-dd HH:mm:ss}</li>")
            .ToArray();
        var html = "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>日迹识别故障日志</title>" +
            "<style>body{font:14px Segoe UI,sans-serif;max-width:1000px;margin:32px auto;padding:0 20px;background:#171b1b;color:#eee}a{color:#ddb777}li{padding:10px 0}</style><h1>截图识别故障日志</h1><p>报告保存在本机数据目录，仅供本机排查。</p><ul>" +
            (reports.Length > 0 ? string.Join("", reports) : "<li>还没有故障报告。</li>") + "</ul></html>";
        File.WriteAllText(path, html, new UTF8Encoding(false));
    }
}
