using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration010GmailSyncRetries : IDatabaseMigration
{
    public int Version => 10;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            CREATE TABLE gmail_sync_retries(
                account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                gmail_message_id TEXT NOT NULL,
                failure_stage TEXT NOT NULL,
                error_code TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 1,
                first_failed_at TEXT NOT NULL,
                last_failed_at TEXT NOT NULL,
                PRIMARY KEY(account_id,gmail_message_id)
            );
            CREATE INDEX ix_gmail_sync_retries_next
                ON gmail_sync_retries(account_id,last_failed_at,gmail_message_id);
            """);
    }
}
