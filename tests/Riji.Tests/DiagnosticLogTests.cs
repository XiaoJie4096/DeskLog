using Riji.Core;
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

    [Fact]
    // Text evidence is escaped, credential-redacted and retained independently of recognition logs.
    public void TextFailuresRetainBoundedReportsAndEscapeProviderContent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RijiLogs", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DiagnosticLog(folder);
            Directory.CreateDirectory(log.DirectoryPath);
            var recognition = Path.Combine(log.DirectoryPath, "recognition-failure-keep.html");
            File.WriteAllText(recognition, "keep existing report");
            for (var index = 0; index < 42; index++)
                log.TextFailure("summary", "0:" + index, new AiFailure("failed secret-token", diagnosticResponse:
                    "<script>alert('secret-token')</script>\n正文片段\n" + new string('文', 40000)),
                    new("https://example.com/v1", "text-model", "encrypted"), "secret-token");
            var files = Directory.GetFiles(log.DirectoryPath, "text-failure-*.html");
            Assert.Equal(40, files.Length);
            foreach (var file in files)
            {
                var html = File.ReadAllText(file);
                Assert.DoesNotContain("secret-token", html);
                Assert.DoesNotContain("<script>", html);
                Assert.Contains("&lt;script&gt;", html);
                Assert.Contains("API Key", html);
                Assert.Contains("text-model", html);
                Assert.InRange(new FileInfo(file).Length, 1, 512_000);
            }
            var indexHtml = File.ReadAllText(log.TextFailureIndex());
            Assert.Equal(40, indexHtml.Split("<li>").Length - 1);
            Assert.True(File.Exists(recognition));
            Assert.False(log.WriteFailed);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    // A broken diagnostic destination must not replace the provider error or crash the scheduler.
    public void UnwritableTextReportDoesNotCrashTheCaller()
    {
        var folder = Path.Combine(Path.GetTempPath(), "RijiLogs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "Logs"), "occupied");
        try
        {
            var log = new DiagnosticLog(folder);
            log.TextFailure("summary", "0:0", new AiFailure("failed", diagnosticResponse: "partial"), new("https://example.com/v1", "text", "encrypted"), "key");
            Assert.True(log.WriteFailed);
        }
        finally { Directory.Delete(folder, true); }
    }

}
