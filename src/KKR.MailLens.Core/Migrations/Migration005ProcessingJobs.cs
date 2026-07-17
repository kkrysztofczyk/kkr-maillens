using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration005ProcessingJobs : IDatabaseMigration
{
    public int Version => 5;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            CREATE TABLE processing_jobs(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_type TEXT NOT NULL,
                attachment_id INTEGER REFERENCES mail_attachments(id) ON DELETE CASCADE,
                document_id INTEGER,
                status TEXT NOT NULL DEFAULT 'pending',
                priority INTEGER NOT NULL DEFAULT 100,
                attempts INTEGER NOT NULL DEFAULT 0,
                max_attempts INTEGER NOT NULL DEFAULT 3,
                available_at TEXT NOT NULL,
                locked_by TEXT,
                locked_at TEXT,
                lease_until TEXT,
                progress_current INTEGER,
                progress_total INTEGER,
                error_code TEXT,
                error_message TEXT,
                created_at TEXT NOT NULL,
                completed_at TEXT
            );
            CREATE INDEX ix_processing_jobs_ready
                ON processing_jobs(status,available_at,priority,id);
            CREATE INDEX ix_processing_jobs_lease ON processing_jobs(status,lease_until);
            CREATE UNIQUE INDEX ux_processing_jobs_active_attachment
                ON processing_jobs(job_type,attachment_id)
                WHERE attachment_id IS NOT NULL AND status IN ('pending','running');
            """);
    }
}
