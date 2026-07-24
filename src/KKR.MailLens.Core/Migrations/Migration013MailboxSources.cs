using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration013MailboxSources : IDatabaseMigration
{
    public int Version => 13;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            CREATE TABLE mailbox_sources(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                provider TEXT NOT NULL CHECK(provider IN ('gmail','imap','outlook')),
                external_key TEXT NOT NULL,
                display_name TEXT NOT NULL DEFAULT '',
                credential_ref TEXT,
                settings_json TEXT NOT NULL DEFAULT '{}',
                enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
                sort_order INTEGER NOT NULL DEFAULT 0 CHECK(sort_order >= 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(provider,external_key)
            );
            CREATE INDEX ix_mailbox_sources_order ON mailbox_sources(sort_order,id);
            CREATE INDEX ix_mailbox_sources_provider ON mailbox_sources(provider);

            ALTER TABLE mails ADD COLUMN mailbox_source_id INTEGER
                REFERENCES mailbox_sources(id) ON DELETE SET NULL;
            CREATE INDEX ix_mails_mailbox_source ON mails(mailbox_source_id);
            """);

        if (!TableExists(connection, transaction, "accounts"))
            return;

        MigrationSql.Execute(connection, transaction, """
            INSERT INTO mailbox_sources(
                provider,external_key,display_name,credential_ref,settings_json,
                enabled,sort_order,created_at,updated_at)
            SELECT
                'gmail',
                lower(trim(email)),
                CASE WHEN trim(display_name)='' THEN trim(email) ELSE trim(display_name) END,
                'gmail-account:' || id,
                '{}',
                1,
                id,
                created_at,
                updated_at
            FROM accounts
            WHERE provider='gmail'
            ON CONFLICT(provider,external_key) DO NOTHING;

            UPDATE mails
            SET mailbox_source_id=(
                SELECT source.id
                FROM accounts AS account
                JOIN mailbox_sources AS source
                  ON source.provider='gmail'
                 AND source.credential_ref='gmail-account:' || account.id
                WHERE mails.store_id='gmail:' || account.id
                LIMIT 1
            )
            WHERE mailbox_source_id IS NULL
              AND store_id LIKE 'gmail:%';
            """);
    }

    static bool TableExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*) FROM sqlite_master
            WHERE type='table' AND name=$tableName;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }
}
