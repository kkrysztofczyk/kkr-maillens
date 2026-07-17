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
        Exec(c, "PRAGMA foreign_keys=ON;");
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

    public const int SchemaVersion = 3;

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

            CREATE TABLE IF NOT EXISTS accounts(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                email TEXT NOT NULL COLLATE NOCASE,
                provider TEXT NOT NULL,
                display_name TEXT NOT NULL DEFAULT '',
                token_key TEXT NOT NULL,
                last_history_id TEXT,
                initial_page_token TEXT,
                initial_sync_completed INTEGER NOT NULL DEFAULT 0,
                last_sync_at TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                sync_generation INTEGER NOT NULL DEFAULT 0,
                last_error_count INTEGER NOT NULL DEFAULT 0,
                current_operation TEXT,
                operation_started_at TEXT,
                UNIQUE(provider, email)
            );
            CREATE INDEX IF NOT EXISTS ix_accounts_provider ON accounts(provider);

            CREATE TABLE IF NOT EXISTS messages(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                gmail_message_id TEXT NOT NULL,
                gmail_thread_id TEXT,
                rfc_message_id TEXT,
                internal_date TEXT,
                sent_at TEXT,
                sender TEXT,
                recipients TEXT,
                cc TEXT,
                bcc TEXT,
                subject TEXT,
                body_text TEXT,
                body_html TEXT,
                is_unread INTEGER NOT NULL DEFAULT 0,
                is_trashed INTEGER NOT NULL DEFAULT 0,
                is_spam INTEGER NOT NULL DEFAULT 0,
                has_attachments INTEGER NOT NULL DEFAULT 0,
                size_bytes INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_seen_generation INTEGER NOT NULL DEFAULT 0,
                UNIQUE(account_id, gmail_message_id)
            );
            CREATE INDEX IF NOT EXISTS ix_messages_account_date ON messages(account_id, internal_date);
            CREATE INDEX IF NOT EXISTS ix_messages_rfc_id ON messages(rfc_message_id);

            CREATE TABLE IF NOT EXISTS labels(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                gmail_label_id TEXT NOT NULL,
                name TEXT NOT NULL,
                label_type TEXT NOT NULL,
                UNIQUE(account_id, gmail_label_id)
            );

            CREATE TABLE IF NOT EXISTS message_labels(
                message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                label_id INTEGER NOT NULL REFERENCES labels(id) ON DELETE CASCADE,
                PRIMARY KEY(message_id, label_id)
            );
            CREATE INDEX IF NOT EXISTS ix_message_labels_label ON message_labels(label_id);

            CREATE TABLE IF NOT EXISTS attachments(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                gmail_attachment_id TEXT NOT NULL DEFAULT '',
                part_id TEXT NOT NULL DEFAULT '',
                filename TEXT NOT NULL DEFAULT '',
                mime_type TEXT NOT NULL DEFAULT 'application/octet-stream',
                size_bytes INTEGER NOT NULL DEFAULT 0,
                download_status TEXT NOT NULL DEFAULT 'metadata-only',
                local_path TEXT,
                extracted_text TEXT,
                index_status TEXT NOT NULL DEFAULT 'not-indexed',
                error_message TEXT,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                last_seen_generation INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_attachments_message ON attachments(message_id);

            CREATE TABLE IF NOT EXISTS sync_errors(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                gmail_message_id TEXT,
                stage TEXT NOT NULL,
                error_code TEXT NOT NULL,
                occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sync_errors_account ON sync_errors(account_id, occurred_at);
            """);
        // migracja starszych baz (bez kolumny kind = mail|alert)
        EnsureColumn(c, "mails", "kind", "TEXT");
        EnsureColumn(c, "attachments", "part_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(c, "attachments", "is_deleted", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(c, "attachments", "last_seen_generation", "INTEGER NOT NULL DEFAULT 0");
        Exec(c, "UPDATE attachments SET gmail_attachment_id='' WHERE gmail_attachment_id IS NULL;");
        Exec(c, "CREATE UNIQUE INDEX IF NOT EXISTS ux_attachments_message_key ON attachments(message_id,gmail_attachment_id,part_id);");
        // Indeksy tylko tam gdzie realnie uzywane (koszt przy zapisie/harveście). conversation_id -
        // brak zapytan; kind - 2 wartosci, planer i tak pomija. Usuwamy jesli zostaly ze starszej bazy.
        Exec(c, "DROP INDEX IF EXISTS ix_mails_conv; DROP INDEX IF EXISTS ix_mails_kind;");
        Exec(c, $"INSERT INTO meta(k,v) VALUES('schema_version','{SchemaVersion}') ON CONFLICT(k) DO UPDATE SET v=excluded.v;");
    }

    static void EnsureColumn(SqliteConnection c, string table, string col, string decl)
    {
        using var chk = c.CreateCommand();
        chk.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{col}';";
        if (Convert.ToInt64(chk.ExecuteScalar()) == 0) Exec(c, $"ALTER TABLE {table} ADD COLUMN {col} {decl};");
    }

    static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
}
