using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

static class MailAttachmentRepository
{
    public static void UpsertGmail(SqliteConnection connection, long generation,
        IReadOnlyList<GmailStoredMessage> messages)
    {
        if (messages.Count == 0) return;
        string now = DateTimeOffset.UtcNow.ToString("O");
        using var transaction = connection.BeginTransaction();
        foreach (GmailStoredMessage message in messages)
        {
            Execute(connection, transaction, """
                UPDATE mail_attachments SET is_deleted=1, updated_at=$now
                WHERE mail_entry_id=$mail AND provider='gmail';
                """, ("$mail", message.EntryId), ("$now", now));

            foreach (GmailAttachmentRecord attachment in message.Attachments)
            {
                if (string.IsNullOrWhiteSpace(attachment.ProviderKey)) continue;
                using var command = Command(connection, transaction, """
                    INSERT INTO mail_attachments(mail_entry_id,provider,provider_message_key,
                        provider_attachment_key,part_id,filename,mime_type,size_bytes,content_id,is_inline,
                        inline_base64_data,download_status,processing_status,is_deleted,last_seen_generation,
                        created_at,updated_at)
                    VALUES($mail,'gmail',$message,$key,$part,$filename,$mime,$size,$content,$inline,$data,
                        'metadata-only','pending',0,$generation,$now,$now)
                    ON CONFLICT(mail_entry_id,provider,provider_attachment_key) DO UPDATE SET
                        provider_message_key=excluded.provider_message_key,
                        part_id=excluded.part_id,
                        filename=excluded.filename,
                        mime_type=excluded.mime_type,
                        content_id=excluded.content_id,
                        is_inline=excluded.is_inline,
                        inline_base64_data=excluded.inline_base64_data,
                        download_status=CASE WHEN mail_attachments.size_bytes<>excluded.size_bytes
                            THEN 'metadata-only' ELSE mail_attachments.download_status END,
                        processing_status=CASE WHEN mail_attachments.size_bytes<>excluded.size_bytes
                            THEN 'pending' ELSE mail_attachments.processing_status END,
                        blob_id=CASE WHEN mail_attachments.size_bytes<>excluded.size_bytes
                            THEN NULL ELSE mail_attachments.blob_id END,
                        error_code=CASE WHEN mail_attachments.size_bytes<>excluded.size_bytes
                            THEN NULL ELSE mail_attachments.error_code END,
                        error_message=CASE WHEN mail_attachments.size_bytes<>excluded.size_bytes
                            THEN NULL ELSE mail_attachments.error_message END,
                        size_bytes=excluded.size_bytes,
                        is_deleted=0,
                        last_seen_generation=excluded.last_seen_generation,
                        updated_at=excluded.updated_at;
                    """,
                    ("$mail", message.EntryId), ("$message", message.GmailMessageId),
                    ("$key", attachment.ProviderKey), ("$part", attachment.PartId),
                    ("$filename", attachment.Filename), ("$mime", attachment.MimeType),
                    ("$size", attachment.SizeBytes), ("$content", attachment.ContentId),
                    ("$inline", attachment.IsInline ? 1 : 0), ("$data", attachment.InlineBase64Data),
                    ("$generation", generation), ("$now", now));
                command.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }
}
