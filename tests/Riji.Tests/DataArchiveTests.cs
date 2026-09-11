using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Riji.Core;
using Riji.Infrastructure;
using Xunit;

namespace Riji.Tests;

public sealed class DataArchiveTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-11T00:00:00Z");
    private sealed class Fixture : IDisposable
    {
        public string Folder = Path.Combine(Path.GetTempPath(), "RijiTests", Guid.NewGuid().ToString("N"));
        public LocalStore Store;
        public Fixture() { Store = new(Path.Combine(Folder, "test.db")); Directory.CreateDirectory(Path.Combine(Folder, "Screenshots")); }
        public void Seed(double seconds)
        {
            var activity = new ActivitySlice(Guid.NewGuid().ToString("N"), "session", "chrome", "chrome", "2026-09-11", Epoch, Epoch.AddSeconds(seconds), seconds);
            Store.Save([activity], new(WebsiteSnippets: true), new(), [new(Guid.NewGuid().ToString("N"), activity.Id, activity.Day, "example.com", null, seconds / 2, "合成网页摘要")]);
            var id = Guid.NewGuid().ToString("N"); var job = new RecognitionJob(id, Epoch, "2026-09-11", 60, id + ".png", Category.Defaults, JobStatus.Pending);
            Store.SaveJob(job); File.WriteAllBytes(Path.Combine(Folder, "Screenshots", job.Image), [137, 80, 78, 71]);
            var successful = job with { Id = Guid.NewGuid().ToString("N") }; successful = successful with { Image = successful.Id + ".png" };
            Store.SaveJob(successful); Store.Complete(successful, new("测试阅读", "learning", 0.9));
            var record = Store.Records("2026-09-11").Single();
            Store.SaveSummary(new(Guid.NewGuid().ToString("N"), new(Epoch, Epoch.AddHours(1), TimeZoneInfo.Utc.Id), Epoch, Epoch, "回顾", [record],
                new("https://example.com/v1", "test", "identity", 4000), GenerationState.Succeeded, "观察到阅读", Conversation: [new("chat", Epoch, "问题", "实际提示词", new("https://example.com/v1", "test", "identity", 4000), GenerationState.Succeeded, "回答")]));
            Store.SaveValue("ai-active", new AiConfiguration("https://example.com/v1", "test", "DO-NOT-EXPORT-CREDENTIAL"));
        }
        public void Dispose() { Store.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(Folder, true); }
    }

    [Fact] public void ArchiveRoundTripRestoresRecordsImagesAndConversationsButNotCredentials()
    {
        using var source = new Fixture(); source.Seed(10); using var target = new Fixture(); target.Seed(90);
        var archive = Path.Combine(source.Folder, "backup.riji"); DataArchive.Export(source.Store, source.Folder, archive);
        using (var zip = ZipFile.OpenRead(archive))
        using (var reader = new StreamReader(zip.GetEntry("data.json")!.Open())) Assert.DoesNotContain("DO-NOT-EXPORT-CREDENTIAL", reader.ReadToEnd());
        var prepared = DataArchive.Prepare(archive, target.Folder); target.Store.ReplaceData(prepared.Data, prepared.ImageRoot);
        Assert.Equal(10, target.Store.Apps("2026-09-11").Single().Seconds); Assert.Equal(5, target.Store.Websites("2026-09-11").Single().Seconds);
        Assert.Equal("合成网页摘要", target.Store.Websites("2026-09-11").Single().Snippet);
        Assert.True(target.Store.Read<TrackingSettings>("settings")!.WebsiteSnippets);
        Assert.Single(target.Store.Jobs(JobStatus.Pending)); Assert.Single(target.Store.Records("2026-09-11"));
        Assert.Equal("回答", target.Store.Summaries().Single().Conversation!.Single().Reply);
        Assert.Single(Directory.GetFiles(prepared.FullImagePath)); Assert.False(target.Store.Read<TrackingSettings>("settings")!.AutoRecord);
        Assert.False(target.Store.Read<CaptureSettings>("capture")!.Enabled);
        Assert.Null(DataArchive.CleanupRetired(target.Store, target.Folder)); Assert.False(Directory.Exists(Path.Combine(target.Folder, "Screenshots")));
        var again = DataArchive.Prepare(archive, target.Folder); target.Store.ReplaceData(again.Data, again.ImageRoot);
        Assert.Equal(10, target.Store.Apps("2026-09-11").Single().Seconds); Assert.Single(target.Store.Summaries());
    }

    [Fact] public void ImportFailureRollsBackAllRowsAndActiveImageReference()
    {
        using var source = new Fixture(); source.Seed(10); using var target = new Fixture(); target.Seed(90);
        var archive = Path.Combine(source.Folder, "backup.riji"); DataArchive.Export(source.Store, source.Folder, archive);
        var prepared = DataArchive.Prepare(archive, target.Folder);
        using var connection = new SqliteConnection("Data Source=" + target.Store.Path); connection.Open();
        using (var command = connection.CreateCommand()) { command.CommandText = "CREATE TRIGGER fail_import BEFORE INSERT ON activity BEGIN SELECT RAISE(FAIL,'injected'); END"; command.ExecuteNonQuery(); }
        Assert.Throws<SqliteException>(() => target.Store.ReplaceData(prepared.Data, prepared.ImageRoot));
        Assert.Equal(90, target.Store.Apps("2026-09-11").Single().Seconds); Assert.Equal("Screenshots", target.Store.ImageRootName());
        Assert.Single(target.Store.Summaries()); Assert.Single(Directory.GetFiles(Path.Combine(target.Folder, "Screenshots")));
        DataArchive.DeleteGeneration(target.Folder, prepared.ImageRoot, target.Store.ImageRootName());
    }

    [Fact] public void BackupVersionsPreserveOldImportsAndRejectMismatchedOrDowngradedDetails()
    {
        using var fixture = new Fixture(); fixture.Seed(10);
        var data = fixture.Store.ExportData(); Assert.Equal(3, data.Format);
        Assert.Throws<ArgumentException>(() => (data with { Format = 1 }).Validate());
        var old = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(data))!;
        old["Format"] = 1;
        old["Settings"]!.AsObject().Remove("WebsiteSnippets");
        foreach (var site in old["Websites"]!.AsArray()) site!.AsObject().Remove("Snippet");
        var bytes = System.Text.Encoding.UTF8.GetBytes(old.ToJsonString());
        var archive = Path.Combine(fixture.Folder, "old-format.riji");
        void WriteArchive(int manifestVersion)
        {
            using var file = File.Create(archive); using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            using (var output = zip.CreateEntry("data.json").Open()) output.Write(bytes);
            using var metadata = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(metadata, new ArchiveManifest("Riji", manifestVersion,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), []));
        }
        WriteArchive(1);
        var prepared = DataArchive.Prepare(archive, fixture.Folder);
        Assert.False(prepared.Data.Settings.WebsiteSnippets);
        Assert.Null(Assert.Single(prepared.Data.Websites).Snippet);
        Assert.Equal(10, Assert.Single(prepared.Data.Activities).Seconds);
        using (var target = new Fixture())
        {
            var legacy = DataArchive.Prepare(archive, target.Folder);
            target.Store.ReplaceData(legacy.Data, legacy.ImageRoot);
            Assert.Equal(5, Assert.Single(target.Store.Websites("2026-09-11")).Seconds);
            Assert.Null(Assert.Single(target.Store.Websites("2026-09-11")).Snippet);
            Assert.Equal("回答", Assert.Single(target.Store.Summaries()).Conversation!.Single().Reply);
        }
        DataArchive.DeleteGeneration(fixture.Folder, prepared.ImageRoot, null);
        WriteArchive(2);
        Assert.Throws<InvalidDataException>(() => DataArchive.Prepare(archive, fixture.Folder));
        WriteArchive(999);
        Assert.Throws<InvalidDataException>(() => DataArchive.Prepare(archive, fixture.Folder));
        Assert.Equal("合成网页摘要", Assert.Single(fixture.Store.Websites("2026-09-11")).Snippet);
        Assert.Single(Directory.GetDirectories(fixture.Folder));
    }

    [Fact] public void CorruptOrUndeclaredArchiveEntriesCannotChangeLiveData()
    {
        using var fixture = new Fixture(); fixture.Seed(10); var archive = Path.Combine(fixture.Folder, "backup.riji"); DataArchive.Export(fixture.Store, fixture.Folder, archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) writer.Write("not allowed");
        Assert.Throws<InvalidDataException>(() => DataArchive.Prepare(archive, fixture.Folder));
        Assert.Equal(10, fixture.Store.Apps("2026-09-11").Single().Seconds);
        Assert.False(File.Exists(Path.Combine(fixture.Folder, "escaped.txt")));
        Assert.Single(Directory.GetDirectories(fixture.Folder));
        DataArchive.Export(fixture.Store, fixture.Folder, archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update)) { zip.GetEntry("data.json")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("data.json").Open()); writer.Write("{}"); }
        Assert.Throws<InvalidDataException>(() => DataArchive.Prepare(archive, fixture.Folder));
        Assert.Equal(10, fixture.Store.Apps("2026-09-11").Single().Seconds);
    }

    [Fact] public void InvalidParentTotalsAreRejectedBeforeReplacement()
    {
        using var fixture = new Fixture(); fixture.Seed(10); var data = fixture.Store.ExportData();
        data = data with { Websites = [data.Websites.Single() with { Seconds = 99 }] };
        Assert.Throws<ArgumentException>(() => fixture.Store.ReplaceData(data, "Screenshots-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(10, fixture.Store.Apps("2026-09-11").Single().Seconds);
        Assert.Throws<ArgumentException>(() => DataArchive.DeleteGeneration(fixture.Folder, "../other", null));
        Assert.Throws<ArgumentException>(() => DataArchive.DeleteGeneration(fixture.Folder, "Screenshots", "Screenshots"));
    }

    private sealed class InflightHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Started.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
    }

    [Fact] public async Task QuiescingBeforeClearPreventsInflightTaskFromWritingBack()
    {
        using var fixture = new Fixture(); fixture.Seed(10); using var handler = new InflightHandler();
        using var pipeline = new RecognitionPipeline(fixture.Store, new(new HttpClient(handler)), fixture.Folder,
            path => File.WriteAllBytes(path, [1, 2, 3]), () => true, value => value, value => value);
        pipeline.Configure(new(true), Epoch.AddMinutes(-1)); var inflight = pipeline.Pulse(Epoch);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await pipeline.Shutdown();
        var empty = fixture.Store.ExportData() with { Activities = [], Websites = [], Jobs = [], Records = [], Summaries = [] };
        var root = "Screenshots-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(Path.Combine(fixture.Folder, root));
        fixture.Store.ReplaceData(empty, root); await inflight;
        Assert.Empty(fixture.Store.Jobs(Enum.GetValues<JobStatus>())); Assert.Empty(fixture.Store.Records("2026-09-11")); Assert.Empty(fixture.Store.Summaries());
        Assert.Null(DataArchive.CleanupRetired(fixture.Store, fixture.Folder)); Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Folder, root)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Folder, "Screenshots")));
    }
}
