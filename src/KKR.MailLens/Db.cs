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
        var c = new SqliteConnection(ConnStr(keyHex, create, path));
        c.Open();
        return c;
    }

    /// <summary>Czy klucz otwiera ISTNIEJACY korpus (weryfikacja PIN). False gdy zly klucz/uszkodzony.</summary>
    public static bool VerifyKey(string keyHex)
    {
        try
        {
            using var c = Open(keyHex, create: false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
            cmd.ExecuteScalar();
            return true;
        }
        catch { return false; }
        finally { SqliteConnection.ClearAllPools(); }
    }

    public const int SchemaVersion = 1;

    /// <summary>Tworzy tabele + FTS5 jesli brak (idempotentne). mails_fts trzyma kopie pol do FTS
    /// (nie external-content: upserty z zachowaniem rowid sa prostsze i odporne).</summary>
    public static void EnsureSchema(SqliteConnection c)
    {
        Exec(c, """
            CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);
            CREATE TABLE IF NOT EXISTS mails(
                entry_id TEXT PRIMARY KEY,
                store_id TEXT, folder_path TEXT, folder_leaf TEXT, conversation_id TEXT,
                received TEXT, sent TEXT,
                sender_name TEXT, sender_email TEXT,
                to_recips TEXT, cc_recips TEXT,
                subject TEXT, body TEXT,
                has_attachments INTEGER, attachment_names TEXT,
                size INTEGER, unread INTEGER, categories TEXT,
                kind TEXT,
                harvested_at TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_mails_received ON mails(received);
            CREATE VIRTUAL TABLE IF NOT EXISTS mails_fts USING fts5(subject, body, sender, recips);
            """);
        // migracja starszych baz (bez kolumny kind = mail|alert)
        EnsureColumn(c, "mails", "kind", "TEXT");
        // Indeksy tylko tam gdzie realnie uzywane (koszt przy zapisie/harveście). conversation_id -
        // brak zapytan; kind - 2 wartosci, planer i tak pomija. Usuwamy jesli zostaly ze starszej bazy.
        Exec(c, "DROP INDEX IF EXISTS ix_mails_conv; DROP INDEX IF EXISTS ix_mails_kind;");
        Exec(c, $"INSERT INTO meta(k,v) VALUES('schema_version','{SchemaVersion}') ON CONFLICT(k) DO NOTHING;");
    }

    static void EnsureColumn(SqliteConnection c, string table, string col, string decl)
    {
        using var chk = c.CreateCommand();
        chk.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{col}';";
        if (Convert.ToInt64(chk.ExecuteScalar()) == 0) Exec(c, $"ALTER TABLE {table} ADD COLUMN {col} {decl};");
    }

    static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
}
