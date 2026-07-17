using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration006StoredBlobs : IDatabaseMigration
{
    public int Version => 6;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            CREATE TABLE stored_blobs(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sha256 TEXT NOT NULL UNIQUE,
                encrypted_path TEXT NOT NULL,
                original_size INTEGER NOT NULL,
                encryption_version INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );
            """);
    }
}
