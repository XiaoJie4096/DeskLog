using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Riji.Core;

namespace Riji.Infrastructure;

public sealed record AppTotal(string AppId, string Name, double Seconds);
public sealed record DayTotal(string Day, double Seconds, int RecordCount = 0, double SampleSeconds = 0);
public sealed record WebsiteTotal(string AppId, string Domain, string? Title, double Seconds, string? Snippet = null, string? AppName = null, string? SourceAppName = null);
public sealed record DayView(List<AppTotal> Apps, List<WebsiteTotal> Websites, List<ActivityRecord> Records);

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

    public DayView ViewDay(string day, TrackingSettings settings, TimeZoneInfo zone)
    {
        var (start, end) = DayRange.Bounds(day, settings, zone);
        if (!settings.NightMode) return new(Apps(day), Websites(day), Records(day));

        var apps = new Dictionary<(string Id, string Name), double>();
        var sites = new Dictionary<(string AppId, string AppName, string SourceName, string Domain, string? Title, string? Snippet), double>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.app_id,a.app_name,a.source_app_name,a.start_utc,a.end_utc,a.seconds,
                   w.domain,w.title,w.snippet,w.seconds,a.id
            FROM activity a LEFT JOIN websites w ON w.parent_id=a.id
            WHERE julianday(a.start_utc)<julianday($end) AND julianday(a.end_utc)>julianday($start)
            ORDER BY a.start_utc
            """;
        command.Parameters.AddWithValue("$start", start.ToString("O"));
        command.Parameters.AddWithValue("$end", end.ToString("O"));
        using var reader = command.ExecuteReader();
        var counted = new HashSet<string>();
        while (reader.Read())
        {
            var sliceStart = reader.GetString(3); var sliceEnd = reader.GetString(4);
            var sliceId = reader.GetString(10);
            var from = DateTimeOffset.Parse(sliceStart); var to = DateTimeOffset.Parse(sliceEnd);
            var fraction = OverlapFraction(from, to, start, end);
            if (counted.Add(sliceId))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                apps[key] = apps.GetValueOrDefault(key) + reader.GetDouble(5) * fraction;
            }
            if (reader.IsDBNull(6)) continue;
            var siteKey = (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8));
            sites[siteKey] = sites.GetValueOrDefault(siteKey) + reader.GetDouble(9) * fraction;
        }
        return new(apps.Select(x => new AppTotal(x.Key.Id, x.Key.Name, x.Value)).OrderByDescending(x => x.Seconds).ToList(),
            sites.Select(x => new WebsiteTotal(x.Key.AppId, x.Key.Domain, x.Key.Title, x.Value, x.Key.Snippet, x.Key.AppName, x.Key.SourceName))
                .OrderByDescending(x => x.Seconds).ToList(), Records(start, end));
    }

    private static double OverlapFraction(DateTimeOffset from, DateTimeOffset to, DateTimeOffset start, DateTimeOffset end)
    {
        var duration = (to - from).TotalSeconds;
        var overlapStart = from > start ? from : start;
        var overlapEnd = to < end ? to : end;
        return duration <= 0 ? 0 : Math.Clamp((overlapEnd - overlapStart).TotalSeconds / duration, 0, 1);
    }

    public List<DayTotal> Days(TrackingSettings settings, TimeZoneInfo zone)
    {
        if (!settings.NightMode) return Days();
        var totals = new Dictionary<string, (double Apps, int Records, double Samples)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT start_utc,end_utc,seconds FROM activity";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var from = DateTimeOffset.Parse(reader.GetString(0)); var to = DateTimeOffset.Parse(reader.GetString(1));
                var originalFrom = from;
                var day = DayRange.Today(from, settings, zone);
                while (from < to)
                {
                    var (_, boundary) = DayRange.Bounds(day, settings, zone);
                    var stop = to < boundary ? to : boundary;
                    var part = reader.GetDouble(2) * OverlapFraction(originalFrom, to, from, stop);
                    var total = totals.GetValueOrDefault(day);
                    totals[day] = (total.Apps + part, total.Records, total.Samples);
                    from = stop;
                    day = DateOnly.ParseExact(day, "yyyy-MM-dd").AddDays(1).ToString("yyyy-MM-dd");
                }
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT utc,seconds FROM records";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var day = DayRange.Today(DateTimeOffset.Parse(reader.GetString(0)), settings, zone);
                var total = totals.GetValueOrDefault(day);
                totals[day] = (total.Apps, total.Records + 1, total.Samples + reader.GetInt32(1));
            }
        }
        return totals.Select(x => new DayTotal(x.Key, x.Value.Apps, x.Value.Records, x.Value.Samples))
            .OrderByDescending(x => x.Day).ToList();
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
