using System.Text.Json;
using Riji.Core;

namespace Riji.Infrastructure;

public sealed record SummaryListEntry(string Id, SummaryRange Range, DateTimeOffset Created, GenerationState State, string? Error, int SourceCount, int CompletedBatches, bool Hourly = false, bool Automatic = false, string? Text = null);

public sealed partial class LocalStore
{
    private void InitializeSummaries()
    {
        Execute("CREATE TABLE IF NOT EXISTS summaries(id TEXT PRIMARY KEY,created TEXT NOT NULL,payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS summary_headers(id TEXT PRIMARY KEY REFERENCES summaries(id) ON DELETE CASCADE,created TEXT NOT NULL,payload TEXT NOT NULL);");
        using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM summaries WHERE id NOT IN (SELECT id FROM summary_headers)";
        List<SummaryDocument> missing = [];
        using (var reader = command.ExecuteReader()) while (reader.Read()) missing.Add(JsonSerializer.Deserialize<SummaryDocument>(reader.GetString(0))!);
        foreach (var summary in missing)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO summary_headers VALUES($id,$created,$payload)";
            insert.Parameters.AddWithValue("$id", summary.Id); insert.Parameters.AddWithValue("$created", summary.Created.ToString("O"));
            insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new SummaryListEntry(summary.Id, summary.Range, summary.Created, summary.State, summary.Error, summary.Sources.Length, summary.Calls?.Count(call => call.Result is not null) ?? 0, summary.Hourly, summary.Automatic, summary.Hourly ? summary.Text : null)));
            insert.ExecuteNonQuery();
        }
    }

    public void SaveSummary(SummaryDocument summary)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO summaries VALUES($id,$created,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload; INSERT INTO summary_headers VALUES($id,$created,$header) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
        command.Parameters.AddWithValue("$id", summary.Id); command.Parameters.AddWithValue("$created", summary.Created.ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(summary));
        command.Parameters.AddWithValue("$header", JsonSerializer.Serialize(new SummaryListEntry(summary.Id, summary.Range, summary.Created, summary.State, summary.Error, summary.Sources.Length, summary.Calls?.Count(call => call.Result is not null) ?? 0, summary.Hourly, summary.Automatic, summary.Hourly ? summary.Text : null)));
        command.ExecuteNonQuery(); transaction.Commit();
    }

    public void SaveChatDraft(string id, string text)
    {
        if (text.Length > 10000) throw new ArgumentException("对话草稿最多 10000 字。");
        // Update only the draft logically; avoid materializing sources and rebuilding history headers.
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE summaries SET payload=json_set(payload,'$.ChatDraft',$text) WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$text", text);
        if (command.ExecuteNonQuery() != 1) throw new ArgumentException("总结不存在。");
    }

    // Keep frequent UI refreshes independent of potentially large frozen evidence and prompt bodies.
    public List<SummaryListEntry> SummaryHeaders()
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM summary_headers ORDER BY created DESC";
        using var reader = command.ExecuteReader(); List<SummaryListEntry> rows = [];
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<SummaryListEntry>(reader.GetString(0))!); return rows;
    }

    public SummaryDocument Summary(string id)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM summaries WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<SummaryDocument>(payload)! : throw new ArgumentException("总结不存在。");
    }

    public List<SummaryDocument> Summaries()
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM summaries ORDER BY created DESC";
        using var reader = command.ExecuteReader(); var rows = new List<SummaryDocument>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<SummaryDocument>(reader.GetString(0))!);
        return rows;
    }
}
