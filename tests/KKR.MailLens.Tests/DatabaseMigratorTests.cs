using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class DatabaseMigratorTests
{
    static DatabaseMigratorTests() => SQLitePCL.Batteries_V2.Init();

    [TestMethod]
    public void NewDatabase_AppliesAllMigrationsAndConnectionPragmas()
    {
        using var db = new TestDatabase();

        Assert.AreEqual(Db.SchemaVersion.ToString(), db.ScalarText("SELECT v FROM meta WHERE k='schema_version';"));
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='mails';"));
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='attachments';"));
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM pragma_table_info('attachments') WHERE name='part_id';"));
        Assert.AreEqual(1, db.ScalarLong("PRAGMA foreign_keys;"));
        Assert.AreEqual(5000, db.ScalarLong("PRAGMA busy_timeout;"));
    }

    [TestMethod]
    public void VersionTwoMigration_PreservesAttachmentProcessingData()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "legacy.db");
        try
        {
            using (SqliteConnection connection = Db.Open(new string('B', 64), create: true, path: path))
            {
                using (var legacy = connection.CreateCommand())
                {
                    legacy.CommandText = """
                        CREATE TABLE meta(k TEXT PRIMARY KEY, v TEXT);
                        INSERT INTO meta(k,v) VALUES('schema_version','2');
                        CREATE TABLE attachments(
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            message_id INTEGER NOT NULL,
                            gmail_attachment_id TEXT,
                            filename TEXT NOT NULL DEFAULT '',
                            mime_type TEXT NOT NULL DEFAULT 'application/octet-stream',
                            size_bytes INTEGER NOT NULL DEFAULT 0,
                            download_status TEXT NOT NULL DEFAULT 'metadata-only',
                            local_path TEXT,
                            extracted_text TEXT,
                            index_status TEXT NOT NULL DEFAULT 'not-indexed',
                            error_message TEXT
                        );
                        INSERT INTO attachments(message_id,gmail_attachment_id,filename,download_status,
                            local_path,extracted_text,index_status,error_message)
                        VALUES(7,'attachment-1','record.pdf','downloaded','encrypted/blob-1',
                            'Neutralny tekst','indexed','retained');
                        """;
                    legacy.ExecuteNonQuery();
                }

                Db.EnsureSchema(connection);

                Assert.AreEqual(Db.SchemaVersion.ToString(), ScalarText(connection, "SELECT v FROM meta WHERE k='schema_version';"));
                Assert.AreEqual("downloaded", ScalarText(connection, "SELECT download_status FROM attachments;"));
                Assert.AreEqual("encrypted/blob-1", ScalarText(connection, "SELECT local_path FROM attachments;"));
                Assert.AreEqual("Neutralny tekst", ScalarText(connection, "SELECT extracted_text FROM attachments;"));
                Assert.AreEqual("indexed", ScalarText(connection, "SELECT index_status FROM attachments;"));
                Assert.AreEqual("retained", ScalarText(connection, "SELECT error_message FROM attachments;"));
                Assert.AreEqual(3, ScalarLong(connection,
                    "SELECT count(*) FROM pragma_table_info('attachments') WHERE name IN ('part_id','is_deleted','last_seen_generation');"));
                Assert.AreEqual(1, ScalarLong(connection,
                    "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='mail_attachments';"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }
}
