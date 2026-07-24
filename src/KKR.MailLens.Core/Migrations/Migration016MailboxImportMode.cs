using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration016MailboxImportMode : IDatabaseMigration
{
    public int Version => 16;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            ALTER TABLE mailbox_import_runs
            ADD COLUMN force_full INTEGER NOT NULL DEFAULT 0
                CHECK(force_full IN (0,1));
            """);
    }
}
