using System.Globalization;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

static class DatabaseMigrator
{
    static readonly IDatabaseMigration[] Migrations =
    [
        new Migration001InitialCorpus(),
        new Migration002Gmail(),
        new Migration003AttachmentState(),
        new Migration004MailAttachments(),
        new Migration005ProcessingJobs(),
        new Migration006StoredBlobs(),
        new Migration007ContentDocuments(),
        new Migration008ContentSearch(),
        new Migration009SemanticSearch(),
        new Migration010GmailSyncRetries(),
        new Migration011MailSourceIdentity(),
    ];

    public const int LatestVersion = 11;

    public static void Migrate(SqliteConnection connection)
    {
        EnsureMetadataTable(connection);
        int currentVersion = ReadVersion(connection);
        if (currentVersion > LatestVersion)
            throw new InvalidOperationException($"Baza ma nowszy schemat ({currentVersion}) niż aplikacja ({LatestVersion}).");

        foreach (IDatabaseMigration migration in Migrations.Where(x => x.Version > currentVersion).OrderBy(x => x.Version))
        {
            if (migration.Version != currentVersion + 1)
                throw new InvalidOperationException($"Brak migracji schematu {currentVersion + 1}.");

            using var transaction = connection.BeginTransaction();
            migration.Apply(connection, transaction);
            SetVersion(connection, transaction, migration.Version);
            transaction.Commit();
            currentVersion = migration.Version;
        }
    }

    static void EnsureMetadataTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);";
        command.ExecuteNonQuery();
    }

    static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT v FROM meta WHERE k='schema_version';";
        string? value = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version < 0)
            throw new InvalidDataException("Nieprawidłowa wersja schematu bazy.");
        return version;
    }

    static void SetVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO meta(k,v) VALUES('schema_version',$version)
            ON CONFLICT(k) DO UPDATE SET v=excluded.v;
            """;
        command.Parameters.AddWithValue("$version", version.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}
