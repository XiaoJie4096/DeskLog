using System.Text.Json;
using Riji.Infrastructure;
using Xunit;

namespace Riji.Tests;

public sealed class DiagnosticLogTests
{
    [Fact] public void RotationAndRateLimitExcludeMessagesAndInnerExceptions()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RijiLogs", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DiagnosticLog(folder, 200, 2);
            foreach (var code in Enum.GetValues<DiagnosticEvent>())
            {
                log.Failure(code, new IOException("sensitive-key", new Exception("private-activity")));
                log.Failure(code, new IOException("sensitive-key"));
            }
            var files = Directory.GetFiles(log.DirectoryPath);
            Assert.InRange(files.Length, 1, 3);
            var content = string.Join("", files.Select(File.ReadAllText));
            Assert.DoesNotContain("sensitive-key", content); Assert.DoesNotContain("private-activity", content);
            Assert.Contains("IOException", content); Assert.False(log.WriteFailed);
            foreach (var file in files) Assert.True(new FileInfo(file).Length <= 200);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact] public void UnwritableDestinationDoesNotCrashTheCaller()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RijiLogs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "Logs"), "occupied");
        try { var log = new DiagnosticLog(folder); log.Failure(DiagnosticEvent.StorageCommit, new IOException()); Assert.True(log.WriteFailed); }
        finally { Directory.Delete(folder, true); }
    }

    [Fact] public void StartupTimingRecordsStagesWithoutUserContent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RijiLogs", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DiagnosticLog(folder);
            log.StartupTiming([new("database_open", 12.5, 20.5), new("webview_environment_wait", 30, 50.5, true)]);
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(log.DirectoryPath, "diagnostic.jsonl")));
            var entry = document.RootElement;
            Assert.Equal("StartupTiming", entry.GetProperty("code").GetString());
            Assert.Equal(50.5, entry.GetProperty("totalMs").GetDouble());
            var stages = entry.GetProperty("stages");
            Assert.Equal("database_open", stages[0].GetProperty("name").GetString());
            Assert.Equal(12.5, stages[0].GetProperty("durationMs").GetDouble());
            Assert.True(stages[1].GetProperty("asyncWait").GetBoolean());
            Assert.False(log.WriteFailed);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact] public void StartupUiDelayRecordsProbeWindowAndLongDispatches()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RijiLogs", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DiagnosticLog(folder);
            var posted = DateTimeOffset.Parse("2026-09-28T02:55:00Z");
            log.StartupUiDelays(190, 3200, [new(posted, 420, 790, 370)]);
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(log.DirectoryPath, "diagnostic.jsonl")));
            var entry = document.RootElement;
            Assert.Equal("StartupUiDelay", entry.GetProperty("code").GetString());
            Assert.Equal(190, entry.GetProperty("monitoringStartMs").GetDouble());
            Assert.Equal(3200, entry.GetProperty("monitoringEndMs").GetDouble());
            Assert.Equal(50, entry.GetProperty("probeIntervalMs").GetInt32());
            Assert.Equal(100, entry.GetProperty("thresholdMs").GetInt32());
            var delay = entry.GetProperty("delays")[0];
            Assert.Equal(420, delay.GetProperty("postedSinceStartMs").GetDouble());
            Assert.Equal(370, delay.GetProperty("delayMs").GetDouble());
            Assert.False(log.WriteFailed);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
