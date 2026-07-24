using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration015ProcessingRunBaseline : IDatabaseMigration
{
    public int Version => 15;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            ALTER TABLE mailbox_import_runs
            ADD COLUMN processing_job_baseline_id INTEGER NOT NULL DEFAULT 0;
            """);
    }
}
