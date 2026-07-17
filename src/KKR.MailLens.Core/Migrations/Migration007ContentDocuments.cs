using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration007ContentDocuments : IDatabaseMigration
{
    public int Version => 7;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            CREATE TABLE content_documents(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                mail_entry_id TEXT NOT NULL REFERENCES mails(entry_id) ON DELETE CASCADE,
                attachment_id INTEGER REFERENCES mail_attachments(id) ON DELETE CASCADE,
                document_kind TEXT NOT NULL,
                source_sha256 TEXT,
                detected_mime_type TEXT,
                detected_language TEXT,
                extractor_name TEXT,
                extractor_version TEXT,
                model_name TEXT,
                pipeline_version INTEGER NOT NULL,
                status TEXT NOT NULL,
                confidence REAL,
                error_code TEXT,
                error_message TEXT,
                created_at TEXT NOT NULL,
                processed_at TEXT
            );
            CREATE UNIQUE INDEX ux_content_documents_attachment_pipeline
                ON content_documents(attachment_id,pipeline_version)
                WHERE attachment_id IS NOT NULL;
            CREATE INDEX ix_content_documents_mail ON content_documents(mail_entry_id,status);
            CREATE INDEX ix_content_documents_source ON content_documents(source_sha256,pipeline_version);

            CREATE TABLE content_segments(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                document_id INTEGER NOT NULL REFERENCES content_documents(id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL,
                page_number INTEGER,
                slide_number INTEGER,
                sheet_name TEXT,
                start_ms INTEGER,
                end_ms INTEGER,
                heading TEXT,
                raw_text TEXT NOT NULL,
                clean_text TEXT NOT NULL,
                confidence REAL,
                metadata_json TEXT,
                UNIQUE(document_id,ordinal)
            );
            CREATE INDEX ix_content_segments_document ON content_segments(document_id,ordinal);

            CREATE UNIQUE INDEX ux_processing_jobs_active_document
                ON processing_jobs(job_type,document_id)
                WHERE document_id IS NOT NULL AND status IN ('pending','running');
            """);
    }
}
