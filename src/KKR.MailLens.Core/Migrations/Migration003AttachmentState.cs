using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration003AttachmentState : IDatabaseMigration
{
    public int Version => 3;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.EnsureColumn(connection, transaction, "attachments", "part_id", "TEXT NOT NULL DEFAULT ''");
        MigrationSql.EnsureColumn(connection, transaction, "attachments", "is_deleted", "INTEGER NOT NULL DEFAULT 0");
        MigrationSql.EnsureColumn(connection, transaction, "attachments", "last_seen_generation", "INTEGER NOT NULL DEFAULT 0");
        MigrationSql.Execute(connection, transaction, """
            UPDATE attachments SET gmail_attachment_id='' WHERE gmail_attachment_id IS NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_attachments_message_key
                ON attachments(message_id,gmail_attachment_id,part_id);
            """);
    }
}
