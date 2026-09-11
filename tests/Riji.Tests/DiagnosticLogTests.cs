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
}
