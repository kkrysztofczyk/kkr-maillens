using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration014MailboxImportRuns : IDatabaseMigration
{
    public int Version => 14;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            CREATE TABLE mailbox_import_runs(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                status TEXT NOT NULL DEFAULT 'queued'
                    CHECK(status IN (
                        'queued','importing','processing','completed',
                        'completed-with-errors','cancelled','failed')),
                cancel_requested INTEGER NOT NULL DEFAULT 0 CHECK(cancel_requested IN (0,1)),
                last_error_code TEXT,
                created_at TEXT NOT NULL,
                started_at TEXT,
                import_completed_at TEXT,
                completed_at TEXT
            );
            CREATE INDEX ix_mailbox_import_runs_status
                ON mailbox_import_runs(status,id);
            CREATE UNIQUE INDEX ux_mailbox_import_runs_single_active
                ON mailbox_import_runs((1))
                WHERE status IN ('queued','importing','processing');

            CREATE TABLE mailbox_import_run_sources(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL
                    REFERENCES mailbox_import_runs(id) ON DELETE CASCADE,
                mailbox_source_id INTEGER
                    REFERENCES mailbox_sources(id) ON DELETE SET NULL,
                source_provider TEXT NOT NULL
                    CHECK(source_provider IN ('gmail','imap','outlook')),
                source_external_key TEXT NOT NULL,
                source_display_name TEXT NOT NULL,
                source_credential_ref TEXT,
                source_settings_json TEXT NOT NULL DEFAULT '{}',
                queue_position INTEGER NOT NULL CHECK(queue_position >= 0),
                status TEXT NOT NULL DEFAULT 'queued'
                    CHECK(status IN ('queued','importing','imported','failed','cancelled')),
                phase TEXT NOT NULL DEFAULT 'queued',
                processed INTEGER NOT NULL DEFAULT 0 CHECK(processed >= 0),
                total INTEGER CHECK(total IS NULL OR total >= 0),
                inserted INTEGER NOT NULL DEFAULT 0 CHECK(inserted >= 0),
                updated INTEGER NOT NULL DEFAULT 0 CHECK(updated >= 0),
                deleted INTEGER NOT NULL DEFAULT 0 CHECK(deleted >= 0),
                errors INTEGER NOT NULL DEFAULT 0 CHECK(errors >= 0),
                last_error_code TEXT,
                started_at TEXT,
                completed_at TEXT,
                UNIQUE(run_id,queue_position),
                UNIQUE(run_id,mailbox_source_id)
            );
            CREATE INDEX ix_mailbox_import_sources_queue
                ON mailbox_import_run_sources(run_id,status,queue_position);
            CREATE INDEX ix_mailbox_import_sources_mailbox
                ON mailbox_import_run_sources(mailbox_source_id,run_id);
            """);
    }
}
