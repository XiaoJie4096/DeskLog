using System.Text;
using System.Text.Json;

namespace Riji.Infrastructure;

public enum DiagnosticEvent { Startup, WebViewStartup, StorageCommit, Command, Snapshot, MaintenanceRecovery }

// Accept fixed event codes and exception metadata only, never messages or user content.
public sealed class DiagnosticLog(string dataDirectory, int maxBytes = 262144, int retainedFiles = 4)
{
    private readonly object gate = new();
    private readonly Dictionary<DiagnosticEvent, long> last = [];
    public string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "Logs");
    public bool WriteFailed { get; private set; }

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
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > maxBytes)
                {
                    for (var index = retainedFiles; index >= 1; index--)
                    {
                        var source = index == 1 ? path : path + "." + (index - 1);
                        if (File.Exists(source)) File.Move(source, path + "." + index, overwrite: true);
                    }
                }
                File.AppendAllText(path, line, new UTF8Encoding(false));
                last[code] = now; WriteFailed = false;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            { WriteFailed = true; }
        }
    }
}
