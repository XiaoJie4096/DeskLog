using Microsoft.Data.Sqlite;

namespace Riji.Infrastructure;

public sealed partial class LocalStore
{
    // Inspect before any writable connection or PRAGMA can alter a future database.
    private static void CheckExistingVersion(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return;
        using var probe = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        probe.Open(); _ = ExistingVersion(probe);
    }

    private static int ExistingVersion(SqliteConnection database)
    {
        using var tables = database.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        if (Convert.ToInt32(tables.ExecuteScalar()) == 0) return 0;
        using var metadata = database.CreateCommand();
        metadata.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='metadata'";
        if (Convert.ToInt32(metadata.ExecuteScalar()) != 1) throw new InvalidOperationException("不是可识别的日迹数据库，未进行升级。");
        using var version = database.CreateCommand(); version.CommandText = "SELECT value FROM metadata WHERE key='schema'";
        if (!int.TryParse(version.ExecuteScalar() as string, out var schema) || schema < 1 || schema > SchemaVersion)
            throw new InvalidOperationException("数据库版本不受当前应用支持，请使用对应版本；未修改数据。");
        return schema;
    }

    // SQLite backup includes committed WAL pages; copying the main file alone is insufficient.
    private string BackupBeforeUpgrade()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "Backups");
        Directory.CreateDirectory(directory);
        var destination = System.IO.Path.Combine(directory, $"before-schema-{SchemaVersion}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        backup.Open(); connection.BackupDatabase(backup);
        using var mode = backup.CreateCommand(); mode.CommandText = "PRAGMA journal_mode=DELETE"; mode.ExecuteNonQuery();
        using var check = backup.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
        if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new InvalidOperationException("升级前备份校验失败，已停止升级。");
        return destination;
    }
}
