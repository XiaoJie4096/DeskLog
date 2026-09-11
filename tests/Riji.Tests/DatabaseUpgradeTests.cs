using Microsoft.Data.Sqlite;
using Riji.Infrastructure;
using Xunit;

namespace Riji.Tests;

public sealed class DatabaseUpgradeTests : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RijiUpgrade", Guid.NewGuid().ToString("N"));
    private string Path => System.IO.Path.Combine(directory, "riji.db");
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    private static void Sql(SqliteConnection database, string sql) { using var command = database.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static string? Value(SqliteConnection database, string sql) { using var command = database.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar()?.ToString(); }

    [Fact] public void FutureVersionIsRejectedBeforeSchemaOrJournalChanges()
    {
        using (var database = Open()) Sql(database, "CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT); INSERT INTO metadata VALUES('schema','999');");
        var bytes = File.ReadAllBytes(Path);
        Assert.Throws<InvalidOperationException>(() => new LocalStore(Path));
        Assert.Equal(bytes, File.ReadAllBytes(Path));
        Assert.False(File.Exists(Path + "-wal"));
        using var reopened = Open();
        Assert.Equal("1", Value(reopened, "SELECT COUNT(*) FROM sqlite_master WHERE type='table'"));
    }

    [Fact] public void SchemaTwoWebsiteRowsSurviveSnippetMigrationAndReopen()
    {
        using (var store = new LocalStore(Path))
        {
            var start = DateTimeOffset.Parse("2026-09-11T00:00:00Z");
            store.Save([new("app", "session", "chrome", "chrome", "2026-09-11", start, start.AddSeconds(10), 10)], new(), new(),
                [new("site", "app", "2026-09-11", "example.com", "historic title", 5)]);
        }
        using (var database = Open()) Sql(database, "ALTER TABLE websites DROP COLUMN snippet; ALTER TABLE activity DROP COLUMN source_app_id; ALTER TABLE activity DROP COLUMN source_app_name; UPDATE metadata SET value='2' WHERE key='schema';");
        using (var upgraded = new LocalStore(Path))
        {
            Assert.NotNull(upgraded.UpgradeBackupPath);
            Assert.Equal("4", upgraded.Read<string>("schema"));
            var site = Assert.Single(upgraded.Websites("2026-09-11"));
            Assert.Equal(5, site.Seconds); Assert.Equal("historic title", site.Title); Assert.Null(site.Snippet);
            Assert.False((upgraded.Read<Riji.Core.TrackingSettings>("settings"))!.WebsiteSnippets);
        }
        using var reopened = new LocalStore(Path);
        Assert.Null(reopened.UpgradeBackupPath);
        Assert.Equal(10, Assert.Single(reopened.Apps("2026-09-11")).Seconds);
    }

    [Fact] public void UpgradeBacksUpCommittedWalAndRunsOnlyOnce()
    {
        using var old = Open();
        Sql(old, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL); INSERT INTO metadata VALUES('schema','1'); INSERT INTO metadata VALUES('sentinel','\"已保存\"');");
        string backup;
        using (var store = new LocalStore(Path))
        {
            Assert.Equal(LocalStore.SchemaVersion.ToString(), store.Read<string>("schema")); Assert.Equal("已保存", store.Read<string>("sentinel"));
            backup = Assert.IsType<string>(store.UpgradeBackupPath);
        }
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            copy.Open(); Assert.Equal("1", Value(copy, "SELECT value FROM metadata WHERE key='schema'"));
            Assert.Equal("\"已保存\"", Value(copy, "SELECT value FROM metadata WHERE key='sentinel'"));
        }
        using var again = new LocalStore(Path); Assert.Null(again.UpgradeBackupPath);
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(directory, "Backups")));
    }

    [Fact] public void MalformedSummaryRollsBackTablesAndVersionAndRetainsBackup()
    {
        using (var database = Open()) Sql(database, "CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL); INSERT INTO metadata VALUES('schema','1'); CREATE TABLE summaries(id TEXT PRIMARY KEY,created TEXT NOT NULL,payload TEXT NOT NULL); INSERT INTO summaries VALUES('bad','2026-09-11','invalid-json');");
        Assert.Throws<System.Text.Json.JsonException>(() => new LocalStore(Path));
        using var check = Open();
        Assert.Equal("1", Value(check, "SELECT value FROM metadata WHERE key='schema'"));
        Assert.Equal("2", Value(check, "SELECT COUNT(*) FROM sqlite_master WHERE type='table'"));
        Assert.Equal("invalid-json", Value(check, "SELECT payload FROM summaries"));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(directory, "Backups")));
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
