using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration004MailAttachments : IDatabaseMigration
{
    public int Version => 4;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            CREATE TABLE mail_attachments(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                mail_entry_id TEXT NOT NULL REFERENCES mails(entry_id) ON DELETE CASCADE,
                provider TEXT NOT NULL,
                provider_message_key TEXT NOT NULL,
                provider_attachment_key TEXT NOT NULL,
                part_id TEXT NOT NULL DEFAULT '',
                filename TEXT NOT NULL DEFAULT '',
                mime_type TEXT NOT NULL DEFAULT 'application/octet-stream',
                size_bytes INTEGER NOT NULL DEFAULT 0,
                content_id TEXT NOT NULL DEFAULT '',
                is_inline INTEGER NOT NULL DEFAULT 0,
                inline_base64_data TEXT,
                download_status TEXT NOT NULL DEFAULT 'metadata-only',
                processing_status TEXT NOT NULL DEFAULT 'pending',
                blob_id INTEGER,
                error_code TEXT,
                error_message TEXT,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                last_seen_generation INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(mail_entry_id,provider,provider_attachment_key)
            );
            CREATE INDEX ix_mail_attachments_status
                ON mail_attachments(provider,download_status,processing_status,is_deleted);
            CREATE INDEX ix_mail_attachments_blob ON mail_attachments(blob_id);
            """);
    }
}
