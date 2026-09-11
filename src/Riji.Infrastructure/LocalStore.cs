using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Riji.Core;

namespace Riji.Infrastructure;

public sealed record AppTotal(string AppId, string Name, double Seconds);
public sealed record DayTotal(string Day, double Seconds, int RecordCount = 0, double SampleSeconds = 0);
public sealed record WebsiteTotal(string AppId, string Domain, string? Title, double Seconds, string? Snippet = null, string? AppName = null, string? SourceAppName = null);

// Own one database connection on the host thread; all writes are transactional.
public sealed partial class LocalStore : IDisposable
{
    private readonly SqliteConnection connection;
    public const int SchemaVersion = 4;
    public string? UpgradeBackupPath { get; private set; }
    public string Path { get; }
    public LocalStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        CheckExistingVersion(Path);
        connection = new(new SqliteConnectionStringBuilder { DataSource = Path }.ToString());
        try
        {
        connection.Open();
        var previous = ExistingVersion(connection);
        if (previous > 0 && previous < SchemaVersion) UpgradeBackupPath = BackupBeforeUpgrade();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=2000;");
        Execute("BEGIN IMMEDIATE;");
        try
        {
        Execute("""
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS activity(
              id TEXT PRIMARY KEY, session TEXT NOT NULL, app_id TEXT NOT NULL, app_name TEXT NOT NULL,
              day TEXT NOT NULL, start_utc TEXT NOT NULL, end_utc TEXT NOT NULL, seconds REAL NOT NULL CHECK(seconds>0));
            CREATE INDEX IF NOT EXISTS activity_day ON activity(day);
            CREATE TABLE IF NOT EXISTS websites(id TEXT PRIMARY KEY,parent_id TEXT NOT NULL REFERENCES activity(id),day TEXT NOT NULL,domain TEXT NOT NULL,title TEXT,seconds REAL NOT NULL CHECK(seconds>0));
            CREATE INDEX IF NOT EXISTS websites_day ON websites(day);
            INSERT OR IGNORE INTO metadata VALUES('schema','1');
            """);
        InitializeRecognition();
        InitializeSummaries();
        if (previous < 3) Execute("ALTER TABLE websites ADD COLUMN snippet TEXT;");
        if (previous < 4) Execute("ALTER TABLE activity ADD COLUMN source_app_id TEXT; ALTER TABLE activity ADD COLUMN source_app_name TEXT;");
        Execute("UPDATE metadata SET value='4' WHERE key='schema'; COMMIT;");
        }
        catch { Execute("ROLLBACK;"); throw; }
        }
        catch { connection.Dispose(); throw; }
    }

    public T? Read<T>(string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        var value = command.ExecuteScalar() as string;
        if (key == "schema") return (T?)(object?)value;
        return value is null ? default : JsonSerializer.Deserialize<T>(value);
    }

    // Commit interval IDs and state together; retrying a batch cannot double-count it.
    public void Save(IReadOnlyList<ActivitySlice> slices, TrackingSettings settings, ModeState mode, IReadOnlyList<WebsiteSlice>? websites = null)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var slice in slices)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO activity(id,session,app_id,app_name,day,start_utc,end_utc,seconds,source_app_id,source_app_name) VALUES($id,$session,$app,$name,$day,$start,$end,$seconds,$source,$sourceName)
                ON CONFLICT(id) DO UPDATE SET end_utc=excluded.end_utc, seconds=excluded.seconds
                WHERE excluded.seconds>activity.seconds
                """;
            command.Parameters.AddWithValue("$id", slice.Id);
            command.Parameters.AddWithValue("$session", slice.Session);
            command.Parameters.AddWithValue("$app", slice.AppId);
            command.Parameters.AddWithValue("$name", slice.AppName);
            command.Parameters.AddWithValue("$day", slice.Day);
            command.Parameters.AddWithValue("$start", slice.StartUtc.ToString("O"));
            command.Parameters.AddWithValue("$end", slice.EndUtc.ToString("O"));
            command.Parameters.AddWithValue("$seconds", slice.Seconds);
            command.Parameters.AddWithValue("$source", (object?)slice.SourceAppId ?? DBNull.Value);
            command.Parameters.AddWithValue("$sourceName", (object?)slice.SourceAppName ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        foreach (var site in websites ?? [])
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO websites(id,parent_id,day,domain,title,seconds,snippet) VALUES($id,$parent,$day,$domain,$title,$seconds,$snippet) ON CONFLICT(id) DO UPDATE SET seconds=excluded.seconds WHERE excluded.seconds>websites.seconds";
            command.Parameters.AddWithValue("$id", site.Id); command.Parameters.AddWithValue("$parent", site.ParentId);
            command.Parameters.AddWithValue("$day", site.Day); command.Parameters.AddWithValue("$domain", site.Domain);
            command.Parameters.AddWithValue("$title", (object?)site.Title ?? DBNull.Value); command.Parameters.AddWithValue("$seconds", site.Seconds);
            command.Parameters.AddWithValue("$snippet", (object?)site.Snippet ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        Put("settings", settings, transaction);
        Put("mode", mode, transaction);
        transaction.Commit();
    }

    public List<AppTotal> Apps(string day)
    {
        if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("日期无效。");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT app_id,app_name,SUM(seconds) FROM activity WHERE day=$day GROUP BY app_id,app_name ORDER BY SUM(seconds) DESC";
        command.Parameters.AddWithValue("$day", day);
        using var reader = command.ExecuteReader();
        List<AppTotal> rows = [];
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetDouble(2)));
        return rows;
    }

    public List<DayTotal> Days()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT day,SUM(app_seconds),SUM(record_count),SUM(sample_seconds) FROM (
                SELECT day,SUM(seconds) AS app_seconds,0 AS record_count,0 AS sample_seconds FROM activity GROUP BY day
                UNION ALL
                SELECT day,0,COUNT(*),SUM(seconds) FROM records GROUP BY day
            ) GROUP BY day ORDER BY day DESC
            """;
        using var reader = command.ExecuteReader();
        List<DayTotal> rows = [];
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetDouble(1), reader.GetInt32(2), reader.GetDouble(3)));
        return rows;
    }

    public List<WebsiteTotal> Websites(string day)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.app_id,w.domain,w.title,SUM(w.seconds),w.snippet,a.app_name,COALESCE(a.source_app_name,a.app_name) FROM websites w JOIN activity a ON a.id=w.parent_id WHERE w.day=$day GROUP BY a.app_id,a.app_name,a.source_app_name,w.domain,w.title,w.snippet ORDER BY SUM(w.seconds) DESC";
        command.Parameters.AddWithValue("$day", day);
        using var reader = command.ExecuteReader(); List<WebsiteTotal> rows = [];
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetDouble(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return rows;
    }

    private void Put<T>(string key, T value, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO metadata VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value));
        command.ExecuteNonQuery();
    }
    private void Execute(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public void Dispose() => connection.Dispose();
}
