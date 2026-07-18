using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

static class MailAttachmentRepository
{
    internal sealed record Item(long Id, string MailEntryId, string Provider, string ProviderMessageKey,
        string ProviderAttachmentKey, string PartId, string Filename, string MimeType, long SizeBytes,
        string ContentId, bool IsInline, long? BlobId, string? InlineBase64Data);

    public static void UpsertGmail(SqliteConnection connection, long generation,
        IReadOnlyList<GmailStoredMessage> messages)
    {
        if (messages.Count == 0) return;
        using var transaction = connection.BeginTransaction();
        UpsertGmail(connection, transaction, generation, messages);
        transaction.Commit();
    }

    internal static void UpsertGmail(SqliteConnection connection, SqliteTransaction transaction,
        long generation, IReadOnlyList<GmailStoredMessage> messages)
    {
        if (messages.Count == 0) return;
        string now = DateTimeOffset.UtcNow.ToString("O");
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
                        updated_at=excluded.updated_at
                    RETURNING id,download_status;
                    """,
                    ("$mail", message.EntryId), ("$message", message.GmailMessageId),
                    ("$key", attachment.ProviderKey), ("$part", attachment.PartId),
                    ("$filename", attachment.Filename), ("$mime", attachment.MimeType),
                    ("$size", attachment.SizeBytes), ("$content", attachment.ContentId),
                    ("$inline", attachment.IsInline ? 1 : 0), ("$data", attachment.InlineBase64Data),
                    ("$generation", generation), ("$now", now));
                long attachmentId;
                string downloadStatus;
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) throw new InvalidOperationException("Nie zapisano metadanych załącznika.");
                    attachmentId = reader.GetInt64(0);
                    downloadStatus = reader.GetString(1);
                }
                if (downloadStatus == "metadata-only")
                {
                    ResetContent(connection, transaction, attachmentId);
                    ProcessingJobRepository.Enqueue(connection, transaction, "download", attachmentId);
                }
            }
        }
    }

    internal static void UpsertHarvested(SqliteConnection connection, SqliteTransaction transaction,
        HarvestedMail message)
    {
        string provider = message.AttachmentProvider.Trim().ToLowerInvariant();
        if (provider.Length == 0) return;
        if (provider is not ("imap" or "outlook"))
            throw new InvalidDataException("Nieobsługiwany provider załącznika.");
        if (string.IsNullOrWhiteSpace(message.ProviderMessageKey))
            throw new InvalidDataException("Brak identyfikatora wiadomości providera.");

        string now = DateTimeOffset.UtcNow.ToString("O");
        Execute(connection, transaction, """
            UPDATE mail_attachments SET is_deleted=1,updated_at=$now
            WHERE mail_entry_id=$mail AND provider=$provider;
            """, ("$mail", message.EntryId), ("$provider", provider), ("$now", now));

        foreach (HarvestedAttachment attachment in message.Attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.ProviderAttachmentKey)) continue;
            using SqliteCommand command = Command(connection, transaction, """
                INSERT INTO mail_attachments(mail_entry_id,provider,provider_message_key,
                    provider_attachment_key,part_id,filename,mime_type,size_bytes,content_id,is_inline,
                    download_status,processing_status,is_deleted,last_seen_generation,created_at,updated_at)
                VALUES($mail,$provider,$message,$key,$part,$filename,$mime,$size,$content,$inline,
                    'metadata-only','pending',0,0,$now,$now)
                ON CONFLICT(mail_entry_id,provider,provider_attachment_key) DO UPDATE SET
                    provider_message_key=excluded.provider_message_key,
                    part_id=excluded.part_id,
                    filename=excluded.filename,
                    mime_type=excluded.mime_type,
                    content_id=excluded.content_id,
                    is_inline=excluded.is_inline,
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
                    updated_at=excluded.updated_at
                RETURNING id,download_status;
                """, ("$mail", message.EntryId), ("$provider", provider),
                ("$message", message.ProviderMessageKey), ("$key", attachment.ProviderAttachmentKey),
                ("$part", attachment.PartId), ("$filename", attachment.Filename),
                ("$mime", attachment.MimeType), ("$size", attachment.SizeBytes),
                ("$content", attachment.ContentId), ("$inline", attachment.IsInline ? 1 : 0), ("$now", now));
            long attachmentId;
            string downloadStatus;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidOperationException("Nie zapisano metadanych załącznika.");
                attachmentId = reader.GetInt64(0);
                downloadStatus = reader.GetString(1);
            }
            if (downloadStatus == "metadata-only")
            {
                ResetContent(connection, transaction, attachmentId);
                ProcessingJobRepository.Enqueue(connection, transaction, "download", attachmentId);
            }
        }
    }

    public static Item Get(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,mail_entry_id,provider,provider_message_key,provider_attachment_key,part_id,
                filename,mime_type,size_bytes,content_id,is_inline,blob_id,inline_base64_data
            FROM mail_attachments WHERE id=$id AND is_deleted=0;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Załącznik nie istnieje.");
        return new Item(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt64(8),
            reader.GetString(9), reader.GetInt64(10) != 0, reader.IsDBNull(11) ? null : reader.GetInt64(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    public static void MarkDownloaded(SqliteConnection connection, long id, StoredBlob blob, string detectedMimeType)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_attachments SET blob_id=$blob,mime_type=$mime,download_status='downloaded',
                processing_status='pending',inline_base64_data=NULL,error_code=NULL,error_message=NULL,
                updated_at=$now WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$blob", blob.Id);
        command.Parameters.AddWithValue("$mime", detectedMimeType);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Nie udało się zaktualizować załącznika.");
    }

    static void ResetContent(SqliteConnection connection, SqliteTransaction transaction, long attachmentId)
    {
        Execute(connection, transaction, """
            DELETE FROM processing_jobs
            WHERE document_id IN(SELECT id FROM content_documents WHERE attachment_id=$attachment);
            DELETE FROM content_documents WHERE attachment_id=$attachment;
            """, ("$attachment", attachmentId));
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
