using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration011MailSourceIdentity : IDatabaseMigration
{
    public int Version => 11;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            ALTER TABLE mails ADD COLUMN source_identity TEXT;
            UPDATE mails SET source_identity=entry_id WHERE store_id LIKE 'gmail:%';
            CREATE UNIQUE INDEX ux_mails_source_identity
                ON mails(source_identity) WHERE source_identity IS NOT NULL;
            """);
    }
}
