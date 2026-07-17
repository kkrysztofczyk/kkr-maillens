using System.Globalization;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed record ProcessingJob(long Id, string JobType, long? AttachmentId, long? DocumentId,
    string Status, int Priority, int Attempts, int MaxAttempts, DateTimeOffset AvailableAt,
    string? LockedBy, DateTimeOffset? LeaseUntil, string? ErrorCode, string? ErrorMessage);

static class ProcessingJobRepository
{
    public static bool Enqueue(SqliteConnection connection, string jobType, long? attachmentId,
        long? documentId = null, int priority = 100, int maxAttempts = 3, DateTimeOffset? availableAt = null)
    {
        ValidateType(jobType);
        string now = Stamp(DateTimeOffset.UtcNow);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO processing_jobs(job_type,attachment_id,document_id,status,priority,
                attempts,max_attempts,available_at,created_at)
            VALUES($type,$attachment,$document,'pending',$priority,0,$max,$available,$created);
            """;
        command.Parameters.AddWithValue("$type", jobType);
        command.Parameters.AddWithValue("$attachment", attachmentId is null ? DBNull.Value : attachmentId.Value);
        command.Parameters.AddWithValue("$document", documentId is null ? DBNull.Value : documentId.Value);
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$max", Math.Max(1, maxAttempts));
        command.Parameters.AddWithValue("$available", Stamp(availableAt ?? DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$created", now);
        return command.ExecuteNonQuery() == 1;
    }

    public static ProcessingJob? LeaseNext(SqliteConnection connection, string workerId,
        TimeSpan leaseDuration, DateTimeOffset? clock = null)
    {
        if (string.IsNullOrWhiteSpace(workerId)) throw new ArgumentException("Brak identyfikatora workera.", nameof(workerId));
        DateTimeOffset now = clock ?? DateTimeOffset.UtcNow;
        using var transaction = connection.BeginTransaction(deferred: false);
        RecoverExpired(connection, transaction, now);

        long? id = ScalarLong(connection, transaction, """
            SELECT id FROM processing_jobs
            WHERE status='pending' AND attempts<max_attempts AND available_at<=$now
            ORDER BY priority,id LIMIT 1;
            """, ("$now", Stamp(now)));
        if (id is null)
        {
            transaction.Commit();
            return null;
        }

        Execute(connection, transaction, """
            UPDATE processing_jobs SET status='running',attempts=attempts+1,
                locked_by=$worker,locked_at=$now,lease_until=$lease,error_code=NULL,error_message=NULL
            WHERE id=$id;
            """, ("$worker", workerId), ("$now", Stamp(now)),
            ("$lease", Stamp(now.Add(leaseDuration))), ("$id", id.Value));
        ProcessingJob job = Read(connection, transaction, id.Value);
        transaction.Commit();
        return job;
    }

    public static void Complete(SqliteConnection connection, long id)
    {
        Execute(connection, null, """
            UPDATE processing_jobs SET status='completed',completed_at=$now,
                locked_by=NULL,locked_at=NULL,lease_until=NULL WHERE id=$id;
            """, ("$now", Stamp(DateTimeOffset.UtcNow)), ("$id", id));
    }

    public static void Fail(SqliteConnection connection, long id, string code, string message,
        TimeSpan retryDelay, DateTimeOffset? clock = null)
    {
        DateTimeOffset now = clock ?? DateTimeOffset.UtcNow;
        Execute(connection, null, """
            UPDATE processing_jobs SET
                status=CASE WHEN attempts>=max_attempts THEN 'failed' ELSE 'pending' END,
                available_at=$available,error_code=$code,error_message=$message,
                locked_by=NULL,locked_at=NULL,lease_until=NULL,
                completed_at=CASE WHEN attempts>=max_attempts THEN $now ELSE NULL END
            WHERE id=$id;
            """, ("$available", Stamp(now.Add(retryDelay))), ("$code", code),
            ("$message", message), ("$now", Stamp(now)), ("$id", id));
    }

    static void RecoverExpired(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        Execute(connection, transaction, """
            UPDATE processing_jobs SET
                status=CASE WHEN attempts>=max_attempts THEN 'failed' ELSE 'pending' END,
                available_at=$now,locked_by=NULL,locked_at=NULL,lease_until=NULL,
                completed_at=CASE WHEN attempts>=max_attempts THEN $now ELSE NULL END,
                error_code='lease-expired',error_message='Worker utracił lease zadania.'
            WHERE status='running' AND lease_until<=$now;
            """, ("$now", Stamp(now)));
    }

    static ProcessingJob Read(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var command = Command(connection, transaction, "SELECT * FROM processing_jobs WHERE id=$id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Zadanie nie istnieje.");
        return new ProcessingJob(reader.GetInt64(reader.GetOrdinal("id")), reader.GetString(reader.GetOrdinal("job_type")),
            NullableLong(reader, "attachment_id"), NullableLong(reader, "document_id"), reader.GetString(reader.GetOrdinal("status")),
            reader.GetInt32(reader.GetOrdinal("priority")), reader.GetInt32(reader.GetOrdinal("attempts")),
            reader.GetInt32(reader.GetOrdinal("max_attempts")), Parse(reader.GetString(reader.GetOrdinal("available_at"))),
            NullableText(reader, "locked_by"), NullableStamp(reader, "lease_until"), NullableText(reader, "error_code"),
            NullableText(reader, "error_message"));
    }

    static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); command.ExecuteNonQuery(); }

    static long? ScalarLong(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); object? value = command.ExecuteScalar(); return value is null or DBNull ? null : Convert.ToInt64(value); }

    static long? NullableLong(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt64(reader.GetOrdinal(name));
    static string? NullableText(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
    static DateTimeOffset? NullableStamp(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : Parse(reader.GetString(reader.GetOrdinal(name)));
    static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    static void ValidateType(string type) { if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Brak typu zadania.", nameof(type)); }
}
