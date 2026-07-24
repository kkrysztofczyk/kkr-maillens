using System.Globalization;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

enum MailboxImportRunStatus
{
    Queued,
    Importing,
    Processing,
    Completed,
    CompletedWithErrors,
    Cancelled,
    Failed,
}

enum MailboxImportSourceStatus
{
    Queued,
    Importing,
    Imported,
    Failed,
    Cancelled,
}

sealed record MailboxImportRunRecord(
    long Id,
    MailboxImportRunStatus Status,
    bool CancelRequested,
    bool ForceFull,
    string? LastErrorCode,
    long ProcessingJobBaselineId,
    string CreatedAt,
    string? StartedAt,
    string? ImportCompletedAt,
    string? CompletedAt);

sealed record MailboxImportSourceRecord(
    long Id,
    long RunId,
    long? MailboxSourceId,
    MailboxProvider Provider,
    string ExternalKey,
    string DisplayName,
    string? CredentialReference,
    string SettingsJson,
    int QueuePosition,
    MailboxImportSourceStatus Status,
    string Phase,
    long Processed,
    long? Total,
    long Inserted,
    long Updated,
    long Deleted,
    long Errors,
    string? LastErrorCode,
    string? StartedAt,
    string? CompletedAt);

sealed record MailboxImportProgress(
    string Phase,
    long Processed,
    long? Total = null,
    long Inserted = 0,
    long Updated = 0,
    long Deleted = 0,
    long Errors = 0);

static class MailboxImportRunRepository
{
    public static MailboxImportRunRecord Create(
        SqliteConnection connection,
        IEnumerable<long> mailboxSourceIds,
        bool forceFull = false)
    {
        long[] sourceIds = mailboxSourceIds.ToArray();
        if (sourceIds.Length == 0)
            throw new ArgumentException("Kolejka importu nie zawiera żadnej skrzynki.", nameof(mailboxSourceIds));
        if (sourceIds.Any(id => id <= 0) || sourceIds.Distinct().Count() != sourceIds.Length)
            throw new ArgumentException("Kolejka importu zawiera nieprawidłowe lub powtórzone źródło.", nameof(mailboxSourceIds));

        string now = Now();
        using var transaction = connection.BeginTransaction();
        using (var active = Command(connection, transaction, """
            SELECT count(*) FROM mailbox_import_runs
            WHERE status IN ('queued','importing','processing');
            """))
        {
            if (Convert.ToInt64(active.ExecuteScalar()) != 0)
                throw new InvalidOperationException("Inna kolejka importu jest już aktywna.");
        }
        var sources = sourceIds
            .Select(id => ReadConfiguredSource(connection, transaction, id))
            .ToArray();
        if (sources.Any(source => !source.Enabled))
            throw new InvalidOperationException("Wyłączona skrzynka nie może zostać dodana do kolejki importu.");

        long runId;
        using (var insertRun = Command(connection, transaction, """
            INSERT INTO mailbox_import_runs(
                status,cancel_requested,force_full,processing_job_baseline_id,created_at)
            VALUES(
                'queued',0,$forceFull,
                COALESCE((SELECT MAX(id) FROM processing_jobs),0),
                $now);
            """,
            ("$forceFull", forceFull ? 1 : 0),
            ("$now", now)))
        {
            insertRun.ExecuteNonQuery();
            runId = LastInsertRowId(connection, transaction);
        }

        for (int position = 0; position < sources.Length; position++)
        {
            MailboxSourceRecord source = sources[position];
            using var insertSource = Command(connection, transaction, """
                INSERT INTO mailbox_import_run_sources(
                    run_id,mailbox_source_id,source_provider,source_external_key,
                    source_display_name,source_credential_ref,source_settings_json,
                    queue_position,status,phase)
                VALUES(
                    $run,$source,$provider,$externalKey,$displayName,$credentialRef,$settings,
                    $position,'queued','queued');
                """,
                ("$run", runId),
                ("$source", source.Id),
                ("$provider", MailboxSourceRepository.ProviderName(source.Provider)),
                ("$externalKey", source.ExternalKey),
                ("$displayName", source.DisplayName),
                ("$credentialRef", (object?)source.CredentialReference ?? DBNull.Value),
                ("$settings", source.SettingsJson),
                ("$position", position));
            insertSource.ExecuteNonQuery();
        }

        transaction.Commit();
        return Find(connection, runId)
            ?? throw new InvalidOperationException("Nie zapisano kolejki importu.");
    }

    public static MailboxImportSourceRecord AppendSource(
        SqliteConnection connection,
        long runId,
        long mailboxSourceId)
    {
        using var transaction = connection.BeginTransaction();
        using (var run = Command(connection, transaction, """
            SELECT count(*) FROM mailbox_import_runs
            WHERE id=$run
              AND status IN ('queued','importing')
              AND cancel_requested=0;
            """, ("$run", runId)))
        {
            if (Convert.ToInt64(run.ExecuteScalar()) != 1)
                throw new InvalidOperationException("Do tej kolejki nie można już dodać skrzynki.");
        }

        MailboxSourceRecord source = ReadConfiguredSource(connection, transaction, mailboxSourceId);
        if (!source.Enabled)
            throw new InvalidOperationException("Wyłączona skrzynka nie może zostać dodana do kolejki importu.");
        using (var duplicate = Command(connection, transaction, """
            SELECT count(*) FROM mailbox_import_run_sources
            WHERE run_id=$run AND mailbox_source_id=$source;
            """, ("$run", runId), ("$source", source.Id)))
        {
            if (Convert.ToInt64(duplicate.ExecuteScalar()) != 0)
                throw new InvalidOperationException("Skrzynka znajduje się już w kolejce importu.");
        }

        long sourceRunId;
        using (var insert = Command(connection, transaction, """
            INSERT INTO mailbox_import_run_sources(
                run_id,mailbox_source_id,source_provider,source_external_key,
                source_display_name,source_credential_ref,source_settings_json,
                queue_position,status,phase)
            VALUES(
                $run,$source,$provider,$externalKey,$displayName,$credentialRef,$settings,
                COALESCE((
                    SELECT MAX(queue_position)+1 FROM mailbox_import_run_sources
                    WHERE run_id=$run),0),
                'queued','queued');
            """,
            ("$run", runId),
            ("$source", source.Id),
            ("$provider", MailboxSourceRepository.ProviderName(source.Provider)),
            ("$externalKey", source.ExternalKey),
            ("$displayName", source.DisplayName),
            ("$credentialRef", (object?)source.CredentialReference ?? DBNull.Value),
            ("$settings", source.SettingsJson)))
        {
            insert.ExecuteNonQuery();
            sourceRunId = LastInsertRowId(connection, transaction);
        }

        transaction.Commit();
        return FindSource(connection, sourceRunId)
            ?? throw new InvalidOperationException("Nie dodano skrzynki do kolejki importu.");
    }

    public static MailboxImportRunRecord? Find(SqliteConnection connection, long runId)
    {
        using var command = Command(connection, null, """
            SELECT id,status,cancel_requested,force_full,last_error_code,processing_job_baseline_id,created_at,started_at,
                   import_completed_at,completed_at
            FROM mailbox_import_runs
            WHERE id=$id
            LIMIT 1;
            """, ("$id", runId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    public static MailboxImportRunRecord? FindActive(SqliteConnection connection)
    {
        using var command = Command(connection, null, """
            SELECT id,status,cancel_requested,force_full,last_error_code,processing_job_baseline_id,created_at,started_at,
                   import_completed_at,completed_at
            FROM mailbox_import_runs
            WHERE status IN ('queued','importing','processing')
            ORDER BY id DESC
            LIMIT 1;
            """);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    public static IReadOnlyList<MailboxImportSourceRecord> ListSources(
        SqliteConnection connection,
        long runId)
    {
        var result = new List<MailboxImportSourceRecord>();
        using var command = Command(connection, null, SourceSelect + "\n" + """
            WHERE run_id=$run
            ORDER BY queue_position,id;
            """, ("$run", runId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(ReadSource(reader));
        return result;
    }

    public static MailboxImportSourceRecord? FindLatestSourceRun(
        SqliteConnection connection,
        long mailboxSourceId)
    {
        if (mailboxSourceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(mailboxSourceId));
        using var command = Command(connection, null, """
            SELECT id,run_id,mailbox_source_id,source_provider,source_external_key,
                   source_display_name,source_credential_ref,source_settings_json,
                   queue_position,status,phase,processed,total,inserted,updated,
                   deleted,errors,last_error_code,started_at,completed_at
            FROM mailbox_import_run_sources
            WHERE mailbox_source_id=$source
            ORDER BY id DESC
            LIMIT 1;
            """, ("$source", mailboxSourceId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSource(reader) : null;
    }

    public static bool Start(SqliteConnection connection, long runId)
    {
        using var command = Command(connection, null, """
            UPDATE mailbox_import_runs
            SET status='importing',started_at=COALESCE(started_at,$now)
            WHERE id=$id AND status='queued' AND cancel_requested=0;
            """, ("$now", Now()), ("$id", runId));
        return command.ExecuteNonQuery() == 1;
    }

    public static MailboxImportSourceRecord? StartNextSource(
        SqliteConnection connection,
        long runId)
    {
        using var transaction = connection.BeginTransaction();
        using (var run = Command(connection, transaction, """
            SELECT count(*) FROM mailbox_import_runs
            WHERE id=$run AND status='importing' AND cancel_requested=0;
            """, ("$run", runId)))
        {
            if (Convert.ToInt64(run.ExecuteScalar()) != 1)
                return null;
        }

        long? sourceId;
        using (var select = Command(connection, transaction, """
            SELECT id FROM mailbox_import_run_sources
            WHERE run_id=$run AND status='queued'
              AND NOT EXISTS(
                  SELECT 1 FROM mailbox_import_run_sources
                  WHERE run_id=$run AND status='importing')
            ORDER BY queue_position,id
            LIMIT 1;
            """, ("$run", runId)))
        {
            object? value = select.ExecuteScalar();
            sourceId = value is null or DBNull ? null : Convert.ToInt64(value);
        }

        if (!sourceId.HasValue)
            return null;

        using (var update = Command(connection, transaction, """
            UPDATE mailbox_import_run_sources
            SET status='importing',phase='connecting',started_at=COALESCE(started_at,$now)
            WHERE id=$id AND status='queued';
            """, ("$now", Now()), ("$id", sourceId.Value)))
        {
            if (update.ExecuteNonQuery() != 1)
                return null;
        }

        transaction.Commit();
        return FindSource(connection, sourceId.Value);
    }

    public static bool SaveProgress(
        SqliteConnection connection,
        long sourceRunId,
        MailboxImportProgress progress)
    {
        ValidateProgress(progress);
        using var command = Command(connection, null, """
            UPDATE mailbox_import_run_sources
            SET phase=$phase,processed=$processed,total=$total,inserted=$inserted,
                updated=$updated,deleted=$deleted,errors=$errors
            WHERE id=$id AND status='importing';
            """,
            ("$phase", progress.Phase.Trim()),
            ("$processed", progress.Processed),
            ("$total", (object?)progress.Total ?? DBNull.Value),
            ("$inserted", progress.Inserted),
            ("$updated", progress.Updated),
            ("$deleted", progress.Deleted),
            ("$errors", progress.Errors),
            ("$id", sourceRunId));
        return command.ExecuteNonQuery() == 1;
    }

    public static bool MarkSourceImported(
        SqliteConnection connection,
        long sourceRunId,
        MailboxImportProgress finalProgress)
        => FinishSource(connection, sourceRunId, MailboxImportSourceStatus.Imported, finalProgress, null);

    public static bool MarkSourceFailed(
        SqliteConnection connection,
        long sourceRunId,
        MailboxImportProgress finalProgress,
        string errorCode)
        => FinishSource(connection, sourceRunId, MailboxImportSourceStatus.Failed,
            finalProgress, NormalizeErrorCode(errorCode));

    public static bool MarkSourceCancelled(
        SqliteConnection connection,
        long sourceRunId,
        MailboxImportProgress finalProgress)
        => FinishSource(connection, sourceRunId, MailboxImportSourceStatus.Cancelled,
            finalProgress, null);

    public static bool RequestCancellation(SqliteConnection connection, long runId)
    {
        using var command = Command(connection, null, """
            UPDATE mailbox_import_runs
            SET cancel_requested=1
            WHERE id=$id
              AND status IN ('queued','importing','processing')
              AND cancel_requested=0;
            """, ("$id", runId));
        return command.ExecuteNonQuery() == 1;
    }

    public static MailboxImportRunStatus? FinishImportPhase(
        SqliteConnection connection,
        long runId)
    {
        using var transaction = connection.BeginTransaction();
        MailboxImportRunRecord? run = FindRun(connection, transaction, runId);
        if (run is null || run.Status is not (
            MailboxImportRunStatus.Queued or MailboxImportRunStatus.Importing))
            return null;

        string now = Now();
        if (run.CancelRequested)
        {
            using (var cancelSources = Command(connection, transaction, """
                UPDATE mailbox_import_run_sources
                SET status='cancelled',phase='cancelled',completed_at=$now
                WHERE run_id=$run AND status IN ('queued','importing');
                """, ("$now", now), ("$run", runId)))
                cancelSources.ExecuteNonQuery();
            using (var cancelRun = Command(connection, transaction, """
                UPDATE mailbox_import_runs
                SET status='cancelled',import_completed_at=$now,completed_at=$now
                WHERE id=$run;
                """, ("$now", now), ("$run", runId)))
                cancelRun.ExecuteNonQuery();
            transaction.Commit();
            return MailboxImportRunStatus.Cancelled;
        }
        if (run.Status != MailboxImportRunStatus.Importing)
            return null;

        using (var unfinished = Command(connection, transaction, """
            SELECT count(*) FROM mailbox_import_run_sources
            WHERE run_id=$run AND status IN ('queued','importing');
            """, ("$run", runId)))
        {
            if (Convert.ToInt64(unfinished.ExecuteScalar()) != 0)
                return null;
        }

        using (var processing = Command(connection, transaction, """
            UPDATE mailbox_import_runs
            SET status='processing',import_completed_at=$now
            WHERE id=$run;
            """, ("$now", now), ("$run", runId)))
            processing.ExecuteNonQuery();
        transaction.Commit();
        return MailboxImportRunStatus.Processing;
    }

    public static MailboxImportRunStatus? CompleteProcessing(
        SqliteConnection connection,
        long runId,
        bool processingHadErrors = false)
    {
        using var transaction = connection.BeginTransaction();
        MailboxImportRunRecord? run = FindRun(connection, transaction, runId);
        if (run is null || run.Status != MailboxImportRunStatus.Processing)
            return null;

        long failures;
        using (var count = Command(connection, transaction, """
            SELECT count(*) FROM mailbox_import_run_sources
            WHERE run_id=$run
              AND (status IN ('failed','cancelled') OR errors > 0);
            """, ("$run", runId)))
            failures = Convert.ToInt64(count.ExecuteScalar());

        MailboxImportRunStatus status = run.CancelRequested
            ? MailboxImportRunStatus.Cancelled
            : failures == 0 && !processingHadErrors
                ? MailboxImportRunStatus.Completed
                : MailboxImportRunStatus.CompletedWithErrors;
        using (var complete = Command(connection, transaction, """
            UPDATE mailbox_import_runs
            SET status=$status,
                last_error_code=CASE
                    WHEN $processingErrors=1 THEN 'ProcessingErrors'
                    ELSE last_error_code
                END,
                completed_at=$now
            WHERE id=$run;
            """,
            ("$status", RunStatusName(status)),
            ("$processingErrors", processingHadErrors ? 1 : 0),
            ("$now", Now()),
            ("$run", runId)))
            complete.ExecuteNonQuery();
        transaction.Commit();
        return status;
    }

    public static bool FailRun(SqliteConnection connection, long runId, string errorCode)
    {
        using var transaction = connection.BeginTransaction();
        using (var cancelSources = Command(connection, transaction, """
            UPDATE mailbox_import_run_sources
            SET status='cancelled',phase='cancelled',completed_at=$now
            WHERE run_id=$run AND status IN ('queued','importing');
            """, ("$now", Now()), ("$run", runId)))
            cancelSources.ExecuteNonQuery();
        using var failRun = Command(connection, transaction, """
            UPDATE mailbox_import_runs
            SET status='failed',last_error_code=$error,completed_at=$now
            WHERE id=$run AND status IN ('queued','importing','processing');
            """,
            ("$error", NormalizeErrorCode(errorCode)),
            ("$now", Now()),
            ("$run", runId));
        bool changed = failRun.ExecuteNonQuery() == 1;
        transaction.Commit();
        return changed;
    }

    public static bool RecoverInterruptedImport(SqliteConnection connection, long runId)
    {
        using var transaction = connection.BeginTransaction();
        MailboxImportRunRecord? run = FindRun(connection, transaction, runId);
        if (run is null || run.Status != MailboxImportRunStatus.Importing)
            return false;

        string now = Now();
        if (run.CancelRequested)
        {
            using (var cancelSources = Command(connection, transaction, """
                UPDATE mailbox_import_run_sources
                SET status='cancelled',phase='cancelled',completed_at=$now
                WHERE run_id=$run AND status IN ('queued','importing');
                """, ("$now", now), ("$run", runId)))
                cancelSources.ExecuteNonQuery();
            using (var cancelRun = Command(connection, transaction, """
                UPDATE mailbox_import_runs
                SET status='cancelled',import_completed_at=$now,completed_at=$now
                WHERE id=$run;
                """, ("$now", now), ("$run", runId)))
                cancelRun.ExecuteNonQuery();
        }
        else
        {
            using (var queueSources = Command(connection, transaction, """
                UPDATE mailbox_import_run_sources
                SET status='queued',phase='queued',started_at=NULL
                WHERE run_id=$run AND status='importing';
                """, ("$run", runId)))
                queueSources.ExecuteNonQuery();
            using (var queueRun = Command(connection, transaction, """
                UPDATE mailbox_import_runs
                SET status='queued'
                WHERE id=$run;
                """, ("$run", runId)))
                queueRun.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    static MailboxImportSourceRecord? FindSource(SqliteConnection connection, long sourceRunId)
    {
        using var command = Command(connection, null, SourceSelect + "\n" + """
            WHERE id=$id
            LIMIT 1;
            """, ("$id", sourceRunId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSource(reader) : null;
    }

    static bool FinishSource(
        SqliteConnection connection,
        long sourceRunId,
        MailboxImportSourceStatus status,
        MailboxImportProgress progress,
        string? errorCode)
    {
        if (status is not (
            MailboxImportSourceStatus.Imported
            or MailboxImportSourceStatus.Failed
            or MailboxImportSourceStatus.Cancelled))
            throw new ArgumentOutOfRangeException(nameof(status));
        ValidateProgress(progress);

        using var command = Command(connection, null, """
            UPDATE mailbox_import_run_sources
            SET status=$status,phase=$phase,processed=$processed,total=$total,
                inserted=$inserted,updated=$updated,deleted=$deleted,errors=$errors,
                last_error_code=$error,completed_at=$now
            WHERE id=$id AND status='importing';
            """,
            ("$status", SourceStatusName(status)),
            ("$phase", progress.Phase.Trim()),
            ("$processed", progress.Processed),
            ("$total", (object?)progress.Total ?? DBNull.Value),
            ("$inserted", progress.Inserted),
            ("$updated", progress.Updated),
            ("$deleted", progress.Deleted),
            ("$errors", progress.Errors),
            ("$error", (object?)errorCode ?? DBNull.Value),
            ("$now", Now()),
            ("$id", sourceRunId));
        return command.ExecuteNonQuery() == 1;
    }

    static MailboxSourceRecord ReadConfiguredSource(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceId)
    {
        using var command = Command(connection, transaction, """
            SELECT id,provider,external_key,display_name,credential_ref,settings_json,
                   enabled,sort_order,created_at,updated_at
            FROM mailbox_sources WHERE id=$id LIMIT 1;
            """, ("$id", sourceId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new KeyNotFoundException($"Nie istnieje źródło poczty o identyfikatorze {sourceId}.");
        return new MailboxSourceRecord(
            reader.GetInt64(0),
            MailboxSourceRepository.ParseProvider(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6) != 0,
            reader.GetInt32(7),
            reader.GetString(8),
            reader.GetString(9));
    }

    static MailboxImportRunRecord? FindRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId)
    {
        using var command = Command(connection, transaction, """
            SELECT id,status,cancel_requested,force_full,last_error_code,processing_job_baseline_id,created_at,started_at,
                   import_completed_at,completed_at
            FROM mailbox_import_runs
            WHERE id=$id
            LIMIT 1;
            """, ("$id", runId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    static MailboxImportRunRecord ReadRun(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        ParseRunStatus(reader.GetString(1)),
        reader.GetInt64(2) != 0,
        reader.GetInt64(3) != 0,
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt64(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9));

    static MailboxImportSourceRecord ReadSource(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.IsDBNull(2) ? null : reader.GetInt64(2),
        MailboxSourceRepository.ParseProvider(reader.GetString(3)),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7),
        reader.GetInt32(8),
        ParseSourceStatus(reader.GetString(9)),
        reader.GetString(10),
        reader.GetInt64(11),
        reader.IsDBNull(12) ? null : reader.GetInt64(12),
        reader.GetInt64(13),
        reader.GetInt64(14),
        reader.GetInt64(15),
        reader.GetInt64(16),
        reader.IsDBNull(17) ? null : reader.GetString(17),
        reader.IsDBNull(18) ? null : reader.GetString(18),
        reader.IsDBNull(19) ? null : reader.GetString(19));

    static void ValidateProgress(MailboxImportProgress progress)
    {
        if (string.IsNullOrWhiteSpace(progress.Phase))
            throw new ArgumentException("Etap importu nie może być pusty.", nameof(progress));
        if (progress.Processed < 0 || progress.Total < 0 || progress.Inserted < 0
            || progress.Updated < 0 || progress.Deleted < 0 || progress.Errors < 0)
            throw new ArgumentOutOfRangeException(nameof(progress), "Liczniki importu nie mogą być ujemne.");
    }

    static string NormalizeErrorCode(string errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("Kod błędu nie może być pusty.", nameof(errorCode));
        string normalized = errorCode.Trim();
        if (normalized.Length > 80 || normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_' or '.')))
            throw new ArgumentException(
                "Kod błędu może zawierać wyłącznie litery, cyfry, kropki, łączniki i podkreślenia.",
                nameof(errorCode));
        return normalized;
    }

    static string RunStatusName(MailboxImportRunStatus status) => status switch
    {
        MailboxImportRunStatus.Queued => "queued",
        MailboxImportRunStatus.Importing => "importing",
        MailboxImportRunStatus.Processing => "processing",
        MailboxImportRunStatus.Completed => "completed",
        MailboxImportRunStatus.CompletedWithErrors => "completed-with-errors",
        MailboxImportRunStatus.Cancelled => "cancelled",
        MailboxImportRunStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    static MailboxImportRunStatus ParseRunStatus(string status) => status switch
    {
        "queued" => MailboxImportRunStatus.Queued,
        "importing" => MailboxImportRunStatus.Importing,
        "processing" => MailboxImportRunStatus.Processing,
        "completed" => MailboxImportRunStatus.Completed,
        "completed-with-errors" => MailboxImportRunStatus.CompletedWithErrors,
        "cancelled" => MailboxImportRunStatus.Cancelled,
        "failed" => MailboxImportRunStatus.Failed,
        _ => throw new InvalidDataException($"Nieobsługiwany stan importu: {status}."),
    };

    static string SourceStatusName(MailboxImportSourceStatus status) => status switch
    {
        MailboxImportSourceStatus.Queued => "queued",
        MailboxImportSourceStatus.Importing => "importing",
        MailboxImportSourceStatus.Imported => "imported",
        MailboxImportSourceStatus.Failed => "failed",
        MailboxImportSourceStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    static MailboxImportSourceStatus ParseSourceStatus(string status) => status switch
    {
        "queued" => MailboxImportSourceStatus.Queued,
        "importing" => MailboxImportSourceStatus.Importing,
        "imported" => MailboxImportSourceStatus.Imported,
        "failed" => MailboxImportSourceStatus.Failed,
        "cancelled" => MailboxImportSourceStatus.Cancelled,
        _ => throw new InvalidDataException($"Nieobsługiwany stan źródła importu: {status}."),
    };

    static long LastInsertRowId(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT last_insert_rowid();");
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    const string SourceSelect = """
        SELECT id,run_id,mailbox_source_id,source_provider,source_external_key,
               source_display_name,source_credential_ref,source_settings_json,
               queue_position,status,phase,processed,total,inserted,updated,deleted,
               errors,last_error_code,started_at,completed_at
        FROM mailbox_import_run_sources
        """;
}
