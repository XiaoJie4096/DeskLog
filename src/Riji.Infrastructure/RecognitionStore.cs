using System.Text.Json;
using Riji.Core;

namespace Riji.Infrastructure;

public sealed partial class LocalStore
{
    private void InitializeRecognition() => Execute("""
        CREATE TABLE IF NOT EXISTS recognition_jobs(id TEXT PRIMARY KEY,status TEXT NOT NULL,utc TEXT NOT NULL,retry_utc TEXT NOT NULL,payload TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS jobs_ready ON recognition_jobs(status,retry_utc);
        CREATE TABLE IF NOT EXISTS records(id TEXT PRIMARY KEY REFERENCES recognition_jobs(id),utc TEXT NOT NULL,day TEXT NOT NULL,seconds INTEGER NOT NULL CHECK(seconds>0),payload TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS records_day ON records(day);
        CREATE INDEX IF NOT EXISTS records_utc ON records(utc);
        """);

    public void SaveValue<T>(string key, T value)
    { using var transaction = connection.BeginTransaction(); Put(key, value, transaction); transaction.Commit(); }

    public void SaveJob(RecognitionJob job)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO recognition_jobs VALUES($id,$status,$utc,$retry,$payload) ON CONFLICT(id) DO UPDATE SET status=excluded.status,retry_utc=excluded.retry_utc,payload=excluded.payload";
        command.Parameters.AddWithValue("$id", job.Id); command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$utc", job.Utc.ToString("O")); command.Parameters.AddWithValue("$retry", (job.RetryAt ?? job.Utc).ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(job)); command.ExecuteNonQuery();
    }

    public List<RecognitionJob> Jobs(params JobStatus[] states)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM recognition_jobs WHERE status IN (" + string.Join(',', states.Select((_, i) => "$s" + i)) + ") ORDER BY utc";
        for (var i = 0; i < states.Length; i++) command.Parameters.AddWithValue("$s" + i, states[i].ToString());
        using var reader = command.ExecuteReader(); List<RecognitionJob> jobs = [];
        while (reader.Read()) jobs.Add(JsonSerializer.Deserialize<RecognitionJob>(reader.GetString(0))!);
        return jobs;
    }

    public RecognitionJob? NextJob(DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM recognition_jobs WHERE status IN ('Pending','Retry') AND retry_utc<=$now ORDER BY utc LIMIT 1";
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<RecognitionJob>(json) : null;
    }

    // A successful record and task state are committed in the same transaction.
    public void Complete(RecognitionJob job, RecognitionResult result)
    {
        RecognitionValidation.Validate(result, job.Categories);
        var record = new ActivityRecord(job.Id, job.Utc, job.Day, job.IntervalSeconds, result.Description.Trim(), job.Categories.Single(x => x.Id == result.CategoryId), result.Confidence);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO records VALUES($id,$utc,$day,$seconds,$payload)";
        command.Parameters.AddWithValue("$id", record.Id); command.Parameters.AddWithValue("$utc", record.Utc.ToString("O"));
        command.Parameters.AddWithValue("$day", record.Day); command.Parameters.AddWithValue("$seconds", record.Seconds);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record)); command.ExecuteNonQuery();
        using var done = connection.CreateCommand(); done.Transaction = transaction;
        done.CommandText = "UPDATE recognition_jobs SET status='Succeeded',payload=$payload WHERE id=$id";
        done.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(job with { Status = JobStatus.Succeeded, Error = null, CleanupPending = true }));
        done.Parameters.AddWithValue("$id", job.Id); done.ExecuteNonQuery(); transaction.Commit();
    }

    public List<ActivityRecord> Records(string day)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM records WHERE day=$day ORDER BY utc";
        command.Parameters.AddWithValue("$day", day); using var reader = command.ExecuteReader(); List<ActivityRecord> records = [];
        while (reader.Read()) records.Add(JsonSerializer.Deserialize<ActivityRecord>(reader.GetString(0))!);
        return records;
    }

    public List<ActivityRecord> Records(DateTimeOffset start, DateTimeOffset end)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM records WHERE utc>=$start AND utc<$end ORDER BY utc";
        command.Parameters.AddWithValue("$start", start.ToUniversalTime().ToString("O")); command.Parameters.AddWithValue("$end", end.ToUniversalTime().ToString("O"));
        using var reader = command.ExecuteReader(); List<ActivityRecord> records = [];
        while (reader.Read()) records.Add(JsonSerializer.Deserialize<ActivityRecord>(reader.GetString(0))!);
        return records;
    }

    public List<JobHealth> JobCounts()
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT status,COUNT(*) FROM recognition_jobs GROUP BY status";
        using var reader = command.ExecuteReader(); List<JobHealth> rows = [];
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetInt32(1))); return rows;
    }

    // Return only operational metadata; screenshots and activity descriptions stay out of this view.
    public JobPage UnfinishedJobs(int offset)
    {
        if (offset < 0) throw new ArgumentException("任务页码无效。");
        const string filter = "status <> 'Succeeded' OR json_extract(payload,'$.CleanupPending') = 1";
        using var count = connection.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM recognition_jobs WHERE " + filter;
        var total = Convert.ToInt32(count.ExecuteScalar());
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM recognition_jobs WHERE " + filter + " ORDER BY utc DESC,id LIMIT 50 OFFSET $offset";
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader(); List<JobDetail> rows = [];
        while (reader.Read())
        {
            var job = JsonSerializer.Deserialize<RecognitionJob>(reader.GetString(0))!;
            rows.Add(new(job.Id, job.Utc, job.Status, job.Attempts, job.RetryAt, job.Error, job.CleanupPending));
        }
        return new(rows.ToArray(), total);
    }

    public DateTimeOffset? LatestRecognizedSample()
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT MAX(utc) FROM records";
        return command.ExecuteScalar() is string utc ? DateTimeOffset.Parse(utc) : null;
    }
}
