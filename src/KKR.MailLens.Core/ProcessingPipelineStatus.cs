using System.Globalization;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

enum ProcessingStageKind
{
    Download,
    Extract,
    Ocr,
    Transcribe,
    Embed,
    Other,
}

sealed record ProcessingStageSnapshot(
    ProcessingStageKind Stage,
    string JobType,
    long Pending,
    long Ready,
    long Running,
    long Completed,
    long Failed)
{
    public long Total => Pending + Running + Completed + Failed;
}

sealed record ProcessingPipelineSnapshot(
    IReadOnlyList<ProcessingStageSnapshot> Stages,
    DateTimeOffset? NextPendingAt,
    DateTimeOffset? NextLeaseExpiryAt)
{
    public long Pending => Stages.Sum(stage => stage.Pending);
    public long Ready => Stages.Sum(stage => stage.Ready);
    public long Running => Stages.Sum(stage => stage.Running);
    public long Completed => Stages.Sum(stage => stage.Completed);
    public long Failed => Stages.Sum(stage => stage.Failed);
    public long Total => Stages.Sum(stage => stage.Total);
    public bool HasActiveJobs => Pending > 0 || Running > 0;
    public DateTimeOffset? NextActionAt
    {
        get
        {
            DateTimeOffset[] values = new[] { NextPendingAt, NextLeaseExpiryAt }
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToArray();
            return values.Length == 0 ? null : values.Min();
        }
    }
}

static class ProcessingPipelineStatus
{
    static readonly (ProcessingStageKind Stage, string JobType)[] KnownStages =
    [
        (ProcessingStageKind.Download, "download"),
        (ProcessingStageKind.Extract, "extract"),
        (ProcessingStageKind.Ocr, "ocr"),
        (ProcessingStageKind.Transcribe, "transcribe"),
        (ProcessingStageKind.Embed, "embed"),
    ];

    public static ProcessingPipelineSnapshot Read(
        SqliteConnection connection,
        long minimumJobIdExclusive,
        long? mailboxSourceId = null,
        long? mailboxImportRunId = null,
        DateTimeOffset? clock = null)
    {
        if (minimumJobIdExclusive < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumJobIdExclusive));
        if (mailboxSourceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(mailboxSourceId));
        if (mailboxImportRunId <= 0)
            throw new ArgumentOutOfRangeException(nameof(mailboxImportRunId));

        DateTimeOffset now = clock ?? DateTimeOffset.UtcNow;
        var rows = new Dictionary<string, MutableStage>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT job.job_type,job.status,count(*),
                       SUM(CASE
                           WHEN job.status='pending' AND job.available_at<=$now THEN 1
                           ELSE 0
                       END)
                FROM processing_jobs AS job
                LEFT JOIN mail_attachments AS attachment ON attachment.id=job.attachment_id
                LEFT JOIN mails AS mail ON mail.entry_id=attachment.mail_entry_id
                WHERE job.id>$baseline
                  AND ($allSources=1 OR mail.mailbox_source_id=$source)
                  AND ($allRuns=1 OR mail.mailbox_source_id IN (
                      SELECT mailbox_source_id
                      FROM mailbox_import_run_sources
                      WHERE run_id=$run
                  ))
                GROUP BY job.job_type,job.status;
                """;
            command.Parameters.AddWithValue("$now", Stamp(now));
            command.Parameters.AddWithValue("$baseline", minimumJobIdExclusive);
            command.Parameters.AddWithValue("$allSources", mailboxSourceId.HasValue ? 0 : 1);
            command.Parameters.AddWithValue("$source", (object?)mailboxSourceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$allRuns", mailboxImportRunId.HasValue ? 0 : 1);
            command.Parameters.AddWithValue("$run", (object?)mailboxImportRunId ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string jobType = reader.GetString(0);
                string status = reader.GetString(1);
                long count = reader.GetInt64(2);
                long ready = reader.GetInt64(3);
                if (!rows.TryGetValue(jobType, out MutableStage? stage))
                {
                    stage = new MutableStage();
                    rows[jobType] = stage;
                }
                switch (status)
                {
                    case "pending":
                        stage.Pending += count;
                        stage.Ready += ready;
                        break;
                    case "running":
                        stage.Running += count;
                        break;
                    case "completed":
                        stage.Completed += count;
                        break;
                    case "failed":
                        stage.Failed += count;
                        break;
                }
            }
        }

        var stages = new List<ProcessingStageSnapshot>();
        foreach ((ProcessingStageKind kind, string jobType) in KnownStages)
        {
            rows.Remove(jobType, out MutableStage? counts);
            stages.Add(ToSnapshot(kind, jobType, counts));
        }
        foreach ((string jobType, MutableStage counts) in rows.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            stages.Add(ToSnapshot(ProcessingStageKind.Other, jobType, counts));

        DateTimeOffset? nextPendingAt = null;
        DateTimeOffset? nextLeaseExpiryAt = null;
        using (var next = connection.CreateCommand())
        {
            next.CommandText = """
                SELECT
                    MIN(CASE WHEN job.status='pending' THEN job.available_at END),
                    MIN(CASE WHEN job.status='running' THEN job.lease_until END)
                FROM processing_jobs AS job
                LEFT JOIN mail_attachments AS attachment ON attachment.id=job.attachment_id
                LEFT JOIN mails AS mail ON mail.entry_id=attachment.mail_entry_id
                WHERE job.id>$baseline
                  AND job.status IN ('pending','running')
                  AND ($allSources=1 OR mail.mailbox_source_id=$source)
                  AND ($allRuns=1 OR mail.mailbox_source_id IN (
                      SELECT mailbox_source_id
                      FROM mailbox_import_run_sources
                      WHERE run_id=$run
                  ));
                """;
            next.Parameters.AddWithValue("$baseline", minimumJobIdExclusive);
            next.Parameters.AddWithValue("$allSources", mailboxSourceId.HasValue ? 0 : 1);
            next.Parameters.AddWithValue("$source", (object?)mailboxSourceId ?? DBNull.Value);
            next.Parameters.AddWithValue("$allRuns", mailboxImportRunId.HasValue ? 0 : 1);
            next.Parameters.AddWithValue("$run", (object?)mailboxImportRunId ?? DBNull.Value);
            using var reader = next.ExecuteReader();
            if (reader.Read())
            {
                nextPendingAt = ParseOptional(reader, 0);
                nextLeaseExpiryAt = ParseOptional(reader, 1);
            }
        }

        return new ProcessingPipelineSnapshot(stages, nextPendingAt, nextLeaseExpiryAt);
    }

    static ProcessingStageSnapshot ToSnapshot(
        ProcessingStageKind kind,
        string jobType,
        MutableStage? counts)
        => new(
            kind,
            jobType,
            counts?.Pending ?? 0,
            counts?.Ready ?? 0,
            counts?.Running ?? 0,
            counts?.Completed ?? 0,
            counts?.Failed ?? 0);

    static string Stamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    static DateTimeOffset? ParseOptional(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        string value = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    sealed class MutableStage
    {
        public long Pending;
        public long Ready;
        public long Running;
        public long Completed;
        public long Failed;
    }
}
