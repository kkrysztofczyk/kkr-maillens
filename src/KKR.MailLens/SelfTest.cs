using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

/// <summary>Dowod fundamentu: SQLCipher szyfruje + zly klucz odrzucony + FTS5 dziala (na bazie tymczasowej).</summary>
static class SelfTest
{
    static string DeriveKeyHex(string pin, byte[] salt)
    {
        using var kdf = new Rfc2898DeriveBytes(pin, salt, 200_000, HashAlgorithmName.SHA256);
        return Convert.ToHexString(kdf.GetBytes(32));
    }

    static string ConnStr(string path, string keyHex) => new SqliteConnectionStringBuilder
    { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Password = $"x'{keyHex}'", Pooling = false }.ToString();

    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kkr-maillens-selftest");
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "test.db");
        if (File.Exists(db)) File.Delete(db);

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        string goodKey = DeriveKeyHex("1234-PIN", salt);
        string wrongKey = DeriveKeyHex("zlyPIN", salt);

        using (var c = new SqliteConnection(ConnStr(db, goodKey)))
        {
            c.Open();
            Exec(c, "CREATE TABLE mails(entryId TEXT PRIMARY KEY, subject TEXT, body TEXT);");
            Exec(c, "CREATE VIRTUAL TABLE mails_fts USING fts5(subject, body, content='mails', content_rowid='rowid');");
            Exec(c, "INSERT INTO mails(entryId,subject,body) VALUES('E1','Test Record','Neutralny tekst wiadomości używany do testowania indeksu');");
            Exec(c, "INSERT INTO mails_fts(rowid,subject,body) SELECT rowid,subject,body FROM mails;");
        }
        SqliteConnection.ClearAllPools();

        var all = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(db));
        bool looksPlain = all.StartsWith("SQLite format 3");
        bool bodyLeaks = all.Contains("Neutralny tekst");
        Console.WriteLine($"[1] szyfrogram? naglowek-plaintext={looksPlain} tresc-wycieka={bodyLeaks} -> {(!looksPlain && !bodyLeaks ? "OK" : "BLAD")}");

        bool wrongRejected;
        try { using var c = new SqliteConnection(ConnStr(db, wrongKey)); c.Open(); Exec(c, "SELECT count(*) FROM mails;"); wrongRejected = false; }
        catch { wrongRejected = true; }
        Console.WriteLine($"[2] zly klucz odrzucony? -> {(wrongRejected ? "OK" : "BLAD")}");
        SqliteConnection.ClearAllPools();

        int cnt; string ftsHit;
        using (var c = new SqliteConnection(ConnStr(db, goodKey)))
        {
            c.Open();
            cnt = Convert.ToInt32(Scalar(c, "SELECT count(*) FROM mails;"));
            ftsHit = Scalar(c, "SELECT subject FROM mails_fts WHERE mails_fts MATCH 'neutralny';")?.ToString() ?? "(brak)";
        }
        Console.WriteLine($"[3] dobry klucz: wierszy={cnt}, FTS 'neutralny' -> '{ftsHit}' -> {(cnt == 1 && ftsHit == "Test Record" ? "OK" : "BLAD")}");
        SqliteConnection.ClearAllPools();
        try { File.Delete(db); } catch { }

        bool ok = !looksPlain && !bodyLeaks && wrongRejected && cnt == 1 && ftsHit == "Test Record";
        Console.WriteLine(ok ? "SELFTEST OK" : "SELFTEST FAIL");
        return ok ? 0 : 1;
    }

    static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    static object? Scalar(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
}
