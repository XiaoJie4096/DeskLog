using System.Text.Json;
using Microsoft.Data.Sqlite;
using Riji.Core;

namespace Riji.Infrastructure;

public sealed partial class LocalStore
{
    public string ImageRootName()
    {
        var name = Read<string>("image-root") ?? "Screenshots";
        if (name != "Screenshots" && (!name.StartsWith("Screenshots-", StringComparison.Ordinal) || !Guid.TryParseExact(name[12..], "N", out _)))
            throw new InvalidDataException("截图目录引用无效。");
        return name;
    }

    public PortableData ExportData()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,session,app_id,app_name,day,start_utc,end_utc,seconds,source_app_id,source_app_name FROM activity ORDER BY start_utc";
        List<ActivitySlice> activities = [];
        using (var reader = command.ExecuteReader()) while (reader.Read()) activities.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)), DateTimeOffset.Parse(reader.GetString(6)), reader.GetDouble(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        command.CommandText = "SELECT id,parent_id,day,domain,title,seconds,snippet FROM websites";
        List<WebsiteSlice> websites = [];
        using (var reader = command.ExecuteReader()) while (reader.Read()) websites.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        command.CommandText = "SELECT payload FROM records ORDER BY utc"; List<ActivityRecord> records = [];
        using (var reader = command.ExecuteReader()) while (reader.Read()) records.Add(JsonSerializer.Deserialize<ActivityRecord>(reader.GetString(0))!);
        return new(3, DateTimeOffset.UtcNow, Read<TrackingSettings>("settings") ?? new(), Read<CaptureSettings>("capture") ?? new(),
            Read<Category[]>("categories") ?? Category.Defaults, Read<PromptPreset[]>("summary-presets") ?? PromptPreset.Defaults,
            activities.ToArray(), websites.ToArray(), Jobs(Enum.GetValues<JobStatus>()).ToArray(), records.ToArray(), Summaries().ToArray());
    }

    // Replacement semantics make repeated imports idempotent; credentials remain local and collection stays off.
    public void ReplaceData(PortableData data, string imageRoot)
    {
        data.Validate();
        if (!imageRoot.StartsWith("Screenshots-", StringComparison.Ordinal) || !Guid.TryParseExact(imageRoot[12..], "N", out _)) throw new ArgumentException("导入截图目录无效。");
        var retired = Read<string[]>("retired-image-roots") ?? [];
        var previous = ImageRootName();
        if (previous == imageRoot || !Directory.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, imageRoot))) throw new ArgumentException("新的截图目录尚未准备完成。");
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM summary_headers; DELETE FROM summaries; DELETE FROM records; DELETE FROM recognition_jobs; DELETE FROM websites; DELETE FROM activity; DELETE FROM metadata WHERE key IN ('summary-form','summary-draft','hourly-summary-cursor');";
            clear.ExecuteNonQuery();
        }
        void Insert(string sql, params (string Name, object? Value)[] values)
        {
            using var insert = connection.CreateCommand(); insert.Transaction = transaction; insert.CommandText = sql;
            foreach (var (name, value) in values) insert.Parameters.AddWithValue(name, value ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
        foreach (var item in data.Activities) Insert("INSERT INTO activity(id,session,app_id,app_name,day,start_utc,end_utc,seconds,source_app_id,source_app_name) VALUES($id,$session,$app,$name,$day,$start,$end,$seconds,$source,$sourceName)",
            ("$id", item.Id), ("$session", item.Session), ("$app", item.AppId), ("$name", item.AppName), ("$day", item.Day), ("$start", item.StartUtc.ToString("O")), ("$end", item.EndUtc.ToString("O")), ("$seconds", item.Seconds), ("$source", item.SourceAppId), ("$sourceName", item.SourceAppName));
        foreach (var item in data.Websites) Insert("INSERT INTO websites(id,parent_id,day,domain,title,seconds,snippet) VALUES($id,$parent,$day,$domain,$title,$seconds,$snippet)",
            ("$id", item.Id), ("$parent", item.ParentId), ("$day", item.Day), ("$domain", item.Domain), ("$title", item.Title), ("$seconds", item.Seconds), ("$snippet", item.Snippet));
        foreach (var item in data.Jobs) Insert("INSERT INTO recognition_jobs VALUES($id,$status,$utc,$retry,$payload)",
            ("$id", item.Id), ("$status", item.Status.ToString()), ("$utc", item.Utc.ToString("O")), ("$retry", (item.RetryAt ?? item.Utc).ToString("O")), ("$payload", JsonSerializer.Serialize(item)));
        foreach (var item in data.Records) Insert("INSERT INTO records VALUES($id,$utc,$day,$seconds,$payload)",
            ("$id", item.Id), ("$utc", item.Utc.ToString("O")), ("$day", item.Day), ("$seconds", item.Seconds), ("$payload", JsonSerializer.Serialize(item)));
        foreach (var item in data.Summaries)
        {
            Insert("INSERT INTO summaries VALUES($id,$created,$payload)", ("$id", item.Id), ("$created", item.Created.ToString("O")), ("$payload", JsonSerializer.Serialize(item)));
            var header = new SummaryListEntry(item.Id, item.Range, item.Created, item.State, item.Error, item.Sources.Length, item.Calls?.Count(call => call.Result is not null) ?? 0, item.Hourly, item.Automatic, item.Hourly ? item.Text : null);
            Insert("INSERT INTO summary_headers VALUES($id,$created,$payload)", ("$id", item.Id), ("$created", item.Created.ToString("O")), ("$payload", JsonSerializer.Serialize(header)));
        }
        Put("settings", data.Settings with { AutoRecord = false }, transaction); Put("mode", new ModeState(), transaction);
        Put("capture", data.Capture with { Enabled = false }, transaction); Put("categories", data.Categories, transaction); Put("summary-presets", data.Presets, transaction);
        Put("image-root", imageRoot, transaction); Put("retired-image-roots", retired.Append(previous).Distinct().ToArray(), transaction);
        transaction.Commit();
    }
}
