using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed record ContentDocumentRecord(
    long Id,
    string MailEntryId,
    long AttachmentId,
    string SourceSha256,
    string DetectedMimeType,
    int PipelineVersion,
    string Status);

static class ContentDocumentRepository
{
    public const int CurrentPipelineVersion = 1;

    public static long EnsureAttachmentDocument(SqliteConnection connection, long attachmentId,
        string sourceSha256, string detectedMimeType, int pipelineVersion = CurrentPipelineVersion)
    {
        if (string.IsNullOrWhiteSpace(sourceSha256)) throw new ArgumentException("Brak skrótu źródła.", nameof(sourceSha256));
        if (pipelineVersion <= 0) throw new ArgumentOutOfRangeException(nameof(pipelineVersion));
        string now = DateTimeOffset.UtcNow.ToString("O");
        using var transaction = connection.BeginTransaction();
        (long Id, string Source)? existing = Find(connection, transaction, attachmentId, pipelineVersion);
        if (existing is null)
        {
            long id = ScalarLong(connection, transaction, """
                INSERT INTO content_documents(mail_entry_id,attachment_id,document_kind,source_sha256,
                    detected_mime_type,pipeline_version,status,created_at)
                SELECT mail_entry_id,id,'attachment',$hash,$mime,$pipeline,'pending',$now
                FROM mail_attachments WHERE id=$attachment AND is_deleted=0
                RETURNING id;
                """, ("$hash", sourceSha256), ("$mime", detectedMimeType), ("$pipeline", pipelineVersion),
                ("$now", now), ("$attachment", attachmentId))
                ?? throw new InvalidOperationException("Załącznik nie istnieje.");
            transaction.Commit();
            return id;
        }

        if (!existing.Value.Source.Equals(sourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            Execute(connection, transaction, "DELETE FROM content_segments WHERE document_id=$id;", ("$id", existing.Value.Id));
            Execute(connection, transaction, """
                UPDATE content_documents SET source_sha256=$hash,detected_mime_type=$mime,status='pending',
                    extractor_name=NULL,extractor_version=NULL,model_name=NULL,confidence=NULL,
                    error_code=NULL,error_message=NULL,processed_at=NULL WHERE id=$id;
                """, ("$hash", sourceSha256), ("$mime", detectedMimeType), ("$id", existing.Value.Id));
            Execute(connection, transaction, """
                UPDATE mail_attachments SET processing_status='pending',error_code=NULL,error_message=NULL,updated_at=$now
                WHERE id=$attachment;
                """, ("$now", now), ("$attachment", attachmentId));
        }
        else
        {
            Execute(connection, transaction,
                "UPDATE content_documents SET detected_mime_type=$mime WHERE id=$id;",
                ("$mime", detectedMimeType), ("$id", existing.Value.Id));
        }
        transaction.Commit();
        return existing.Value.Id;
    }

    public static ContentDocumentRecord Get(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,mail_entry_id,attachment_id,source_sha256,detected_mime_type,pipeline_version,status
            FROM content_documents WHERE id=$id AND attachment_id IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Dokument nie istnieje.");
        return new ContentDocumentRecord(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6));
    }

    public static string SaveExtraction(SqliteConnection connection, long documentId, ExtractionResult result,
        string extractorName, string extractorVersion, string documentKind = "attachment", string? modelName = null,
        bool ocrCompleted = false)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        bool canUseOcr = result.DetectedMimeType == "application/pdf"
            || result.DetectedMimeType.StartsWith("image/", StringComparison.Ordinal);
        bool hasMissingPdfPages = result.DetectedMimeType == "application/pdf"
            && result.OcrPageNumbers.Count > 0;
        string status = !ocrCompleted && canUseOcr && (result.CleanText.Length == 0 || hasMissingPdfPages)
            ? "needs-ocr"
            : "completed";
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM content_segments WHERE document_id=$id;", ("$id", documentId));
        foreach (ExtractedSegment segment in result.Segments)
        {
            Execute(connection, transaction, """
                INSERT INTO content_segments(document_id,ordinal,page_number,slide_number,sheet_name,heading,
                    raw_text,clean_text,confidence,metadata_json)
                VALUES($document,$ordinal,$page,$slide,$sheet,$heading,$raw,$clean,$confidence,$metadata);
                """, ("$document", documentId), ("$ordinal", segment.Ordinal), ("$page", segment.PageNumber),
                ("$slide", segment.SlideNumber), ("$sheet", segment.SheetName), ("$heading", segment.Heading),
                ("$raw", segment.RawText), ("$clean", segment.CleanText), ("$confidence", segment.Confidence),
                ("$metadata", segment.MetadataJson));
        }
        Execute(connection, transaction, """
            UPDATE content_documents SET document_kind=$kind,detected_mime_type=$mime,extractor_name=$extractor,
                extractor_version=$version,model_name=$model,status=$status,
                error_code=NULL,error_message=NULL,processed_at=$now
            WHERE id=$id;
            """, ("$kind", documentKind), ("$mime", result.DetectedMimeType), ("$extractor", extractorName),
            ("$version", extractorVersion), ("$model", modelName), ("$status", status), ("$now", now), ("$id", documentId));
        Execute(connection, transaction, """
            UPDATE mail_attachments SET processing_status=$status,error_code=NULL,error_message=NULL,updated_at=$now
            WHERE id=(SELECT attachment_id FROM content_documents WHERE id=$id);
            """, ("$status", status == "completed" ? "extracted" : status), ("$now", now), ("$id", documentId));
        ContentSearch.IndexSavedDocument(connection, transaction, documentId);
        transaction.Commit();
        return status;
    }

    public static void MarkFailed(SqliteConnection connection, long documentId, string code, string message)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        Execute(connection, null, """
            UPDATE content_documents SET status='failed',error_code=$code,error_message=$message,processed_at=$now
            WHERE id=$id;
            UPDATE mail_attachments SET processing_status='failed',error_code=$code,error_message=$message,updated_at=$now
            WHERE id=(SELECT attachment_id FROM content_documents WHERE id=$id);
            """, ("$code", code), ("$message", message), ("$now", now), ("$id", documentId));
    }

    public static void MarkSkipped(SqliteConnection connection, long documentId, string code, string message)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        Execute(connection, null, """
            DELETE FROM content_segments WHERE document_id=$id;
            UPDATE content_documents SET status='skipped',error_code=$code,error_message=$message,processed_at=$now
            WHERE id=$id;
            UPDATE mail_attachments SET processing_status='skipped',error_code=$code,error_message=$message,updated_at=$now
            WHERE id=(SELECT attachment_id FROM content_documents WHERE id=$id);
            """, ("$code", code), ("$message", message), ("$now", now), ("$id", documentId));
    }

    static (long Id, string Source)? Find(SqliteConnection connection, SqliteTransaction transaction,
        long attachmentId, int pipelineVersion)
    {
        using var command = Command(connection, transaction, """
            SELECT id,source_sha256 FROM content_documents
            WHERE attachment_id=$attachment AND pipeline_version=$pipeline;
            """, ("$attachment", attachmentId), ("$pipeline", pipelineVersion));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetString(1)) : null;
    }

    static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); command.ExecuteNonQuery(); }

    static long? ScalarLong(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); object? value = command.ExecuteScalar(); return value is null or DBNull ? null : Convert.ToInt64(value); }
}
