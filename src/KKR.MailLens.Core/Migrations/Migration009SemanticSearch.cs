using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration009SemanticSearch : IDatabaseMigration
{
    public int Version => 9;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            CREATE TABLE content_embeddings(
                segment_id INTEGER NOT NULL REFERENCES content_segments(id) ON DELETE CASCADE,
                model TEXT NOT NULL,
                dimensions INTEGER NOT NULL,
                vector BLOB NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY(segment_id,model),
                CHECK(dimensions BETWEEN 1 AND 16384),
                CHECK(length(vector)=dimensions*4)
            );
            CREATE INDEX ix_content_embeddings_model
                ON content_embeddings(model,dimensions,segment_id);
            """);
    }
}
