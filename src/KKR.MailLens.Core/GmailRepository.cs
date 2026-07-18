using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

static class GmailRepository
{
    const string Provider = "gmail";

    public static GmailAccountRecord UpsertAccount(SqliteConnection c, string email, string displayName, string proposedTokenKey)
    {
        string now = Now();
        using var cmd = Command(c, null, """
            INSERT INTO accounts(email,provider,display_name,token_key,created_at,updated_at)
            VALUES($email,$provider,$display,$token,$now,$now)
            ON CONFLICT(provider,email) DO UPDATE SET
                display_name=excluded.display_name,
                updated_at=excluded.updated_at;
            """,
            ("$email", email.Trim()), ("$provider", Provider), ("$display", displayName.Trim()),
            ("$token", proposedTokenKey), ("$now", now));
        cmd.ExecuteNonQuery();
        return FindAccount(c, email) ?? throw new InvalidOperationException("Nie zapisano konta Gmail.");
    }

    public static IReadOnlyList<GmailAccountRecord> ListAccounts(SqliteConnection c)
    {
        var result = new List<GmailAccountRecord>();
        using var cmd = Command(c, null, """
            SELECT id,email,display_name,token_key,last_history_id,initial_page_token,
                   initial_sync_completed,last_sync_at,sync_generation,last_error_count,
                   current_operation,operation_started_at
            FROM accounts WHERE provider=$provider ORDER BY email COLLATE NOCASE;
            """, ("$provider", Provider));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadAccount(reader));
        return result;
    }

    public static GmailAccountRecord? FindAccount(SqliteConnection c, string selector)
    {
        using var cmd = Command(c, null, """
            SELECT id,email,display_name,token_key,last_history_id,initial_page_token,
                   initial_sync_completed,last_sync_at,sync_generation,last_error_count,
                   current_operation,operation_started_at
            FROM accounts
            WHERE provider=$provider AND (email=$selector COLLATE NOCASE OR CAST(id AS TEXT)=$selector)
            LIMIT 1;
            """, ("$provider", Provider), ("$selector", selector.Trim()));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAccount(reader) : null;
    }

    public static GmailAccountRecord? FindAccount(SqliteConnection c, long accountId) => FindAccount(c, accountId.ToString());

    public static string? DeleteAccount(SqliteConnection c, long accountId)
    {
        var account = FindAccount(c, accountId);
        if (account is null) return null;
        Corpus.DeleteByStoreId(c, $"gmail:{accountId}");
        using var cmd = Command(c, null, "DELETE FROM accounts WHERE id=$id AND provider=$provider;",
            ("$id", accountId), ("$provider", Provider));
        cmd.ExecuteNonQuery();
        return account.TokenKey;
    }

    public static void UpsertLabels(SqliteConnection c, long accountId, IEnumerable<GmailApiLabel> labels)
    {
        using var tx = c.BeginTransaction();
        foreach (var label in labels)
        {
            if (string.IsNullOrWhiteSpace(label.Id)) continue;
            using var cmd = Command(c, tx, """
                INSERT INTO labels(account_id,gmail_label_id,name,label_type)
                VALUES($account,$id,$name,$type)
                ON CONFLICT(account_id,gmail_label_id) DO UPDATE SET name=excluded.name,label_type=excluded.label_type;
                """, ("$account", accountId), ("$id", label.Id), ("$name", label.Name ?? label.Id),
                ("$type", string.IsNullOrWhiteSpace(label.Type) ? "unknown" : label.Type));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static IReadOnlyDictionary<string, string> LabelNames(SqliteConnection c, long accountId)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = Command(c, null, "SELECT gmail_label_id,name FROM labels WHERE account_id=$account;", ("$account", accountId));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public static GmailSaveBatchResult SaveMessages(SqliteConnection c, long generation, IReadOnlyList<GmailStoredMessage> messages)
    {
        using var transaction = c.BeginTransaction();
        GmailSaveBatchResult result = SaveMessages(c, transaction, generation, messages);
        transaction.Commit();
        return result;
    }

    internal static GmailSaveBatchResult SaveMessages(SqliteConnection c, SqliteTransaction transaction,
        long generation, IReadOnlyList<GmailStoredMessage> messages)
    {
        long inserted = 0, updated = 0;
        var saved = new List<GmailStoredMessage>();
        var failed = new List<string>();
        string now = Now();

        foreach (var message in messages)
        {
            Exec(c, transaction, "SAVEPOINT gmail_message;");
            try
            {
                long? existingId = ScalarLong(c, transaction,
                    "SELECT id FROM messages WHERE account_id=$account AND gmail_message_id=$gmail;",
                    ("$account", message.AccountId), ("$gmail", message.GmailMessageId));

                using (var upsert = Command(c, transaction, """
                    INSERT INTO messages(account_id,gmail_message_id,gmail_thread_id,rfc_message_id,internal_date,sent_at,
                        sender,recipients,cc,bcc,subject,body_text,body_html,is_unread,is_trashed,is_spam,
                        has_attachments,size_bytes,created_at,updated_at,last_seen_generation)
                    VALUES($account,$gmail,$thread,$rfc,$internal,$sent,$sender,$recipients,$cc,$bcc,$subject,$text,$html,
                        $unread,$trashed,$spam,$attachments,$size,$now,$now,$generation)
                    ON CONFLICT(account_id,gmail_message_id) DO UPDATE SET
                        gmail_thread_id=excluded.gmail_thread_id,rfc_message_id=excluded.rfc_message_id,
                        internal_date=excluded.internal_date,sent_at=excluded.sent_at,sender=excluded.sender,
                        recipients=excluded.recipients,cc=excluded.cc,bcc=excluded.bcc,subject=excluded.subject,
                        body_text=excluded.body_text,body_html=excluded.body_html,is_unread=excluded.is_unread,
                        is_trashed=excluded.is_trashed,is_spam=excluded.is_spam,
                        has_attachments=excluded.has_attachments,size_bytes=excluded.size_bytes,
                        updated_at=excluded.updated_at,last_seen_generation=excluded.last_seen_generation;
                    """,
                    ("$account", message.AccountId), ("$gmail", message.GmailMessageId), ("$thread", message.GmailThreadId),
                    ("$rfc", message.RfcMessageId), ("$internal", message.InternalDate), ("$sent", message.SentAt),
                    ("$sender", message.Sender), ("$recipients", message.Recipients), ("$cc", message.Cc),
                    ("$bcc", message.Bcc), ("$subject", message.Subject), ("$text", message.BodyText),
                    ("$html", message.BodyHtml), ("$unread", message.IsUnread ? 1 : 0),
                    ("$trashed", message.IsTrashed ? 1 : 0), ("$spam", message.IsSpam ? 1 : 0),
                    ("$attachments", message.Attachments.Count > 0 ? 1 : 0), ("$size", message.SizeBytes),
                    ("$now", now), ("$generation", generation)))
                    upsert.ExecuteNonQuery();

                long messageId = existingId ?? ScalarLong(c, transaction,
                    "SELECT id FROM messages WHERE account_id=$account AND gmail_message_id=$gmail;",
                    ("$account", message.AccountId), ("$gmail", message.GmailMessageId))
                    ?? throw new InvalidOperationException("Brak lokalnego identyfikatora wiadomosci.");

                Exec(c, transaction, "DELETE FROM message_labels WHERE message_id=$message;", ("$message", messageId));
                foreach (string labelId in message.LabelIds.Distinct(StringComparer.Ordinal))
                {
                    using (var ensure = Command(c, transaction, """
                        INSERT INTO labels(account_id,gmail_label_id,name,label_type)
                        VALUES($account,$label,$label,'unknown')
                        ON CONFLICT(account_id,gmail_label_id) DO NOTHING;
                        """, ("$account", message.AccountId), ("$label", labelId)))
                        ensure.ExecuteNonQuery();
                    long localLabelId = ScalarLong(c, transaction,
                        "SELECT id FROM labels WHERE account_id=$account AND gmail_label_id=$label;",
                        ("$account", message.AccountId), ("$label", labelId))!.Value;
                    Exec(c, transaction, "INSERT OR IGNORE INTO message_labels(message_id,label_id) VALUES($message,$label);",
                        ("$message", messageId), ("$label", localLabelId));
                }

                Exec(c, transaction, "UPDATE attachments SET is_deleted=1 WHERE message_id=$message;", ("$message", messageId));
                foreach (var attachment in message.Attachments)
                {
                    using var insertAttachment = Command(c, transaction, """
                        INSERT INTO attachments(message_id,gmail_attachment_id,part_id,filename,mime_type,size_bytes,
                            download_status,index_status,is_deleted,last_seen_generation)
                        VALUES($message,$gmail,$part,$filename,$mime,$size,'metadata-only','not-indexed',0,$generation)
                        ON CONFLICT(message_id,gmail_attachment_id,part_id) DO UPDATE SET
                            filename=excluded.filename,
                            mime_type=excluded.mime_type,
                            size_bytes=excluded.size_bytes,
                            is_deleted=0,
                            last_seen_generation=excluded.last_seen_generation;
                        """, ("$message", messageId), ("$gmail", attachment.GmailAttachmentId), ("$part", attachment.PartId),
                        ("$filename", attachment.Filename), ("$mime", attachment.MimeType), ("$size", attachment.SizeBytes));
                    insertAttachment.Parameters.AddWithValue("$generation", generation);
                    insertAttachment.ExecuteNonQuery();
                }

                Exec(c, transaction, "RELEASE SAVEPOINT gmail_message;");
                if (existingId.HasValue) updated++; else inserted++;
                saved.Add(message);
            }
            catch (Exception ex)
            {
                Exec(c, transaction, "ROLLBACK TO SAVEPOINT gmail_message;");
                Exec(c, transaction, "RELEASE SAVEPOINT gmail_message;");
                failed.Add(message.GmailMessageId);
                InsertError(c, transaction, message.AccountId, message.GmailMessageId, "database", ex.GetType().Name);
            }
        }
        return new GmailSaveBatchResult(inserted, updated, saved, failed);
    }

    public static int DeleteMessages(SqliteConnection c, long accountId, IEnumerable<string> gmailMessageIds)
    {
        string[] ids = gmailMessageIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return 0;
        int deleted = 0;
        using (var tx = c.BeginTransaction())
        {
            foreach (string id in ids)
            {
                using var cmd = Command(c, tx, "DELETE FROM messages WHERE account_id=$account AND gmail_message_id=$gmail;",
                    ("$account", accountId), ("$gmail", id));
                deleted += cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        Corpus.DeleteByEntryIds(c, ids.Select(id => $"gmail:{accountId}:{id}"));
        return deleted;
    }

    public static int PruneMissingMessages(SqliteConnection c, long accountId, long generation)
    {
        var ids = new List<string>();
        using (var cmd = Command(c, null,
            "SELECT gmail_message_id FROM messages WHERE account_id=$account AND last_seen_generation<>$generation;",
            ("$account", accountId), ("$generation", generation)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetString(0));
        return DeleteMessages(c, accountId, ids);
    }

    public static IReadOnlyList<string> RetryMessageIds(SqliteConnection c, long accountId, int limit = 1000)
    {
        using var cmd = Command(c, null, """
            SELECT gmail_message_id FROM gmail_sync_retries
            WHERE account_id=$account ORDER BY last_failed_at,gmail_message_id LIMIT $limit;
            """, ("$account", accountId), ("$limit", Math.Clamp(limit, 1, 10_000)));
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public static long RetryCount(SqliteConnection c, long accountId) =>
        ScalarLong(c, null, "SELECT count(*) FROM gmail_sync_retries WHERE account_id=$account;",
            ("$account", accountId)) ?? 0;

    public static void QueueRetries(SqliteConnection c, long accountId,
        IEnumerable<(string MessageId, string Stage, string Code)> failures)
    {
        var rows = failures.Where(failure => !string.IsNullOrWhiteSpace(failure.MessageId))
            .GroupBy(failure => failure.MessageId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        if (rows.Length == 0) return;
        string now = Now();
        using var transaction = c.BeginTransaction();
        foreach (var failure in rows)
        {
            using var command = Command(c, transaction, """
                INSERT INTO gmail_sync_retries(account_id,gmail_message_id,failure_stage,error_code,
                    attempts,first_failed_at,last_failed_at)
                VALUES($account,$message,$stage,$code,1,$now,$now)
                ON CONFLICT(account_id,gmail_message_id) DO UPDATE SET
                    failure_stage=excluded.failure_stage,error_code=excluded.error_code,
                    attempts=gmail_sync_retries.attempts+1,last_failed_at=excluded.last_failed_at;
                """, ("$account", accountId), ("$message", failure.MessageId),
                ("$stage", failure.Stage), ("$code", failure.Code), ("$now", now));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static void ResolveRetries(SqliteConnection c, long accountId, IEnumerable<string> messageIds)
    {
        string[] ids = messageIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;
        using var transaction = c.BeginTransaction();
        foreach (string id in ids)
        {
            using var command = Command(c, transaction,
                "DELETE FROM gmail_sync_retries WHERE account_id=$account AND gmail_message_id=$message;",
                ("$account", accountId), ("$message", id));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static void MarkMessagesSeen(SqliteConnection c, long accountId, long generation,
        IEnumerable<string> messageIds)
    {
        string[] ids = messageIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;
        using var transaction = c.BeginTransaction();
        foreach (string id in ids)
        {
            using var command = Command(c, transaction, """
                UPDATE messages SET last_seen_generation=$generation
                WHERE account_id=$account AND gmail_message_id=$message;
                """, ("$generation", generation), ("$account", accountId), ("$message", id));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static void BeginFullSync(SqliteConnection c, long accountId, string historyId, bool reset)
    {
        string sql = reset
            ? """
              UPDATE accounts SET last_history_id=$history,initial_page_token=NULL,initial_sync_completed=0,
                  sync_generation=sync_generation+1,updated_at=$now WHERE id=$id;
              """
            : """
              UPDATE accounts SET last_history_id=COALESCE(last_history_id,$history),
                  sync_generation=CASE WHEN sync_generation=0 THEN 1 ELSE sync_generation END,
                  updated_at=$now WHERE id=$id;
              """;
        using var cmd = Command(c, null, sql, ("$history", historyId), ("$now", Now()), ("$id", accountId));
        cmd.ExecuteNonQuery();
    }

    public static void CheckpointFullPage(SqliteConnection c, long accountId, string? nextPageToken)
    {
        using var cmd = Command(c, null,
            "UPDATE accounts SET initial_page_token=$token,updated_at=$now WHERE id=$id;",
            ("$token", nextPageToken), ("$now", Now()), ("$id", accountId));
        cmd.ExecuteNonQuery();
    }

    public static void UpdateHistory(SqliteConnection c, long accountId, string historyId)
    {
        using var cmd = Command(c, null,
            "UPDATE accounts SET last_history_id=$history,updated_at=$now WHERE id=$id;",
            ("$history", historyId), ("$now", Now()), ("$id", accountId));
        cmd.ExecuteNonQuery();
    }

    public static void CompleteFullSync(SqliteConnection c, long accountId, string historyId, int errors)
    {
        string now = Now();
        using var cmd = Command(c, null, """
            UPDATE accounts SET last_history_id=$history,initial_page_token=NULL,initial_sync_completed=1,
                last_sync_at=$now,updated_at=$now,last_error_count=$errors WHERE id=$id;
            """, ("$history", historyId), ("$now", now), ("$errors", errors), ("$id", accountId));
        cmd.ExecuteNonQuery();
    }

    public static void SetOperation(SqliteConnection c, long accountId, string? operation, int? errors = null, bool completed = false)
    {
        string now = Now();
        using var cmd = Command(c, null, """
            UPDATE accounts SET current_operation=$operation,
                operation_started_at=CASE WHEN $operation IS NULL THEN NULL ELSE $now END,
                last_error_count=COALESCE($errors,last_error_count),
                last_sync_at=CASE WHEN $completed=1 THEN $now ELSE last_sync_at END,
                updated_at=$now WHERE id=$id;
            """, ("$operation", operation), ("$now", now), ("$errors", errors),
            ("$completed", completed ? 1 : 0), ("$id", accountId));
        cmd.ExecuteNonQuery();
    }

    public static void RecordError(SqliteConnection c, long accountId, string? gmailMessageId, string stage, string code)
    {
        using var tx = c.BeginTransaction();
        InsertError(c, tx, accountId, gmailMessageId, stage, code);
        tx.Commit();
    }

    public static long MessageCount(SqliteConnection c, long accountId) =>
        ScalarLong(c, null, "SELECT count(*) FROM messages WHERE account_id=$account;", ("$account", accountId)) ?? 0;

    public static long ErrorCount(SqliteConnection c, long accountId) =>
        ScalarLong(c, null, "SELECT count(*) FROM sync_errors WHERE account_id=$account;", ("$account", accountId)) ?? 0;

    static void InsertError(SqliteConnection c, SqliteTransaction tx, long accountId, string? gmailMessageId, string stage, string code)
    {
        using var cmd = Command(c, tx, """
            INSERT INTO sync_errors(account_id,gmail_message_id,stage,error_code,occurred_at)
            VALUES($account,$message,$stage,$code,$now);
            """, ("$account", accountId), ("$message", gmailMessageId), ("$stage", stage),
            ("$code", code), ("$now", Now()));
        cmd.ExecuteNonQuery();
    }

    static GmailAccountRecord ReadAccount(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        Text(reader, 4), Text(reader, 5), reader.GetInt64(6) != 0, Text(reader, 7), reader.GetInt64(8),
        reader.GetInt32(9), Text(reader, 10), Text(reader, 11));

    static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    static long? ScalarLong(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    {
        using var cmd = Command(c, tx, sql, values);
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] values)
    { using var cmd = Command(c, tx, sql, values); cmd.ExecuteNonQuery(); }

    static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}
