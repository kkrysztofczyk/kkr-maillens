using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration001InitialCorpus : IDatabaseMigration
{
    public int Version => 1;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS mails(
                entry_id TEXT PRIMARY KEY,
                store_id TEXT, folder_path TEXT, folder_leaf TEXT, conversation_id TEXT,
                received TEXT, sent TEXT,
                sender_name TEXT, sender_email TEXT,
                to_recips TEXT, cc_recips TEXT,
                subject TEXT, body TEXT,
                has_attachments INTEGER, attachment_names TEXT,
                size INTEGER, unread INTEGER, categories TEXT,
                kind TEXT,
                harvested_at TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_mails_received ON mails(received);
            CREATE VIRTUAL TABLE IF NOT EXISTS mails_fts USING fts5(subject, body, sender, recips);
            """);

        MigrationSql.EnsureColumn(connection, transaction, "mails", "kind", "TEXT");
        MigrationSql.Execute(connection, transaction, "DROP INDEX IF EXISTS ix_mails_conv; DROP INDEX IF EXISTS ix_mails_kind;");
    }
}
