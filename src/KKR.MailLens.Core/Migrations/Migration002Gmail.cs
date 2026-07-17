using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration002Gmail : IDatabaseMigration
{
    public int Version => 2;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS accounts(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                email TEXT NOT NULL COLLATE NOCASE,
                provider TEXT NOT NULL,
                display_name TEXT NOT NULL DEFAULT '',
                token_key TEXT NOT NULL,
                last_history_id TEXT,
                initial_page_token TEXT,
                initial_sync_completed INTEGER NOT NULL DEFAULT 0,
                last_sync_at TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                sync_generation INTEGER NOT NULL DEFAULT 0,
                last_error_count INTEGER NOT NULL DEFAULT 0,
                current_operation TEXT,
                operation_started_at TEXT,
                UNIQUE(provider, email)
            );
            CREATE INDEX IF NOT EXISTS ix_accounts_provider ON accounts(provider);

            CREATE TABLE IF NOT EXISTS messages(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                gmail_message_id TEXT NOT NULL,
                gmail_thread_id TEXT,
                rfc_message_id TEXT,
                internal_date TEXT,
                sent_at TEXT,
                sender TEXT,
                recipients TEXT,
                cc TEXT,
                bcc TEXT,
                subject TEXT,
                body_text TEXT,
                body_html TEXT,
                is_unread INTEGER NOT NULL DEFAULT 0,
                is_trashed INTEGER NOT NULL DEFAULT 0,
                is_spam INTEGER NOT NULL DEFAULT 0,
                has_attachments INTEGER NOT NULL DEFAULT 0,
                size_bytes INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_seen_generation INTEGER NOT NULL DEFAULT 0,
                UNIQUE(account_id, gmail_message_id)
            );
            CREATE INDEX IF NOT EXISTS ix_messages_account_date ON messages(account_id, internal_date);
            CREATE INDEX IF NOT EXISTS ix_messages_rfc_id ON messages(rfc_message_id);

            CREATE TABLE IF NOT EXISTS labels(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                gmail_label_id TEXT NOT NULL,
                name TEXT NOT NULL,
                label_type TEXT NOT NULL,
                UNIQUE(account_id, gmail_label_id)
            );

            CREATE TABLE IF NOT EXISTS message_labels(
                message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                label_id INTEGER NOT NULL REFERENCES labels(id) ON DELETE CASCADE,
                PRIMARY KEY(message_id, label_id)
            );
            CREATE INDEX IF NOT EXISTS ix_message_labels_label ON message_labels(label_id);

            CREATE TABLE IF NOT EXISTS attachments(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                gmail_attachment_id TEXT,
                filename TEXT NOT NULL DEFAULT '',
                mime_type TEXT NOT NULL DEFAULT 'application/octet-stream',
                size_bytes INTEGER NOT NULL DEFAULT 0,
                download_status TEXT NOT NULL DEFAULT 'metadata-only',
                local_path TEXT,
                extracted_text TEXT,
                index_status TEXT NOT NULL DEFAULT 'not-indexed',
                error_message TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_attachments_message ON attachments(message_id);

            CREATE TABLE IF NOT EXISTS sync_errors(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                gmail_message_id TEXT,
                stage TEXT NOT NULL,
                error_code TEXT NOT NULL,
                occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sync_errors_account ON sync_errors(account_id, occurred_at);
            """);
    }
}
