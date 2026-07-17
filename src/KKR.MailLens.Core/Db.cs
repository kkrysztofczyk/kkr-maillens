using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

/// <summary>Otwieranie zaszyfrowanego korpusu (SQLCipher). Klucz jako hex => PRAGMA key = "x'...'".</summary>
static class Db
{
    public static string ConnStr(string keyHex, bool create, string? path = null) => new SqliteConnectionStringBuilder
    {
        DataSource = path ?? Paths.CorpusDb,
        Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
        Password = $"x'{keyHex}'",
        Pooling = false,
    }.ToString();

    public static SqliteConnection Open(string keyHex, bool create = false, string? path = null)
    {
        var connection = new SqliteConnection(ConnStr(keyHex, create, path));
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON;");
        Execute(connection, "PRAGMA busy_timeout=5000;");
        return connection;
    }

    /// <summary>Czy klucz otwiera istniejący korpus. False oznacza zły klucz albo uszkodzoną bazę.</summary>
    public static bool VerifyKey(string keyHex)
    {
        try
        {
            using var connection = Open(keyHex, create: false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            command.ExecuteScalar();
            return true;
        }
        catch { return false; }
        finally { SqliteConnection.ClearAllPools(); }
    }

    public const int SchemaVersion = DatabaseMigrator.LatestVersion;

    /// <summary>Uruchamia brakujące migracje schematu, każdą w osobnej transakcji.</summary>
    public static void EnsureSchema(SqliteConnection connection) => DatabaseMigrator.Migrate(connection);

    static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
