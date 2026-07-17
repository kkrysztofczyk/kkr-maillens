using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

interface IDatabaseMigration
{
    int Version { get; }
    void Apply(SqliteConnection connection, SqliteTransaction transaction);
}

static class MigrationSql
{
    public static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static void EnsureColumn(SqliteConnection connection, SqliteTransaction transaction,
        string table, string column, string declaration)
    {
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$column;";
        check.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt64(check.ExecuteScalar()) == 0)
            Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {declaration};");
    }
}
