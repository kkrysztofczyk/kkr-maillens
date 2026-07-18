using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

/// <summary>
/// Jawna inicjacja korpusu: ustala PIN + (opcjonalnie) YubiKey i TWORZY zaszyfrowana pusta baze,
/// wiazac klucz z konkretnym PIN/kluczem. Wczesniej init byl niejawny (pierwszy unlock/harvest) -
/// literowka w PINie tworzyla baze na zlym kluczu. Teraz to osobny, potwierdzany krok.
/// </summary>
static class Setup
{
    public static bool IsInitialized => File.Exists(Paths.CorpusDb) && Mode.Read().Length > 0;

    public sealed record Result(string? Error, string? KeyHex);

    /// <summary>Tworzy zaszyfrowana pusta baze zwiazana z PIN(+YubiKey) i USTAWIA tryb. Zwraca klucz
    /// (bez zarzadzania sesja - wolajacy decyduje: GUI trzyma w RAM, CLI na dysku).</summary>
    public static Result Init(string pin, bool wantYubi, bool force)
    {
        if (string.IsNullOrEmpty(pin)) return new("Pusty PIN.", null);
        // Chron ISTNIEJACY plik bazy, nie tylko "w pelni zainicjowany" (mode.txt moze zniknac, a dane sa).
        if ((IsInitialized || File.Exists(Paths.CorpusDb)) && !force)
            return new("Korpus juz istnieje. Uzyj force, ale to SKASUJE dotychczasowe dane.", null);

        // 1) Walidacja ZANIM cokolwiek zniszczymy. Sol trzymana w pamieci - na dysk trafi dopiero
        //    po udanym zbudowaniu nowej bazy (nieudany init nie moze osierocic istniejacego korpusu).
        byte[] salt = Crypto.NewSaltBytes(); // swieza sol = swieze zwiazanie (i wyzwanie YubiKey)
        byte[]? resp = null;
        if (wantYubi)
        {
            if (!YubiKey.TryInfo(out var info)) return new($"YubiKey niewykryty ({info}). Wepnij klucz i powtorz.", null);
            try { resp = YubiKey.ChallengeResponse(salt); }
            catch (Exception ex) { return new("Blad YubiKey: " + ex.Message, null); }
        }
        string keyHex = Crypto.DeriveKeyHex(pin, salt, resp);

        // 2) Zbuduj nowa baze w pliku tymczasowym (stary korpus jeszcze nietkniety).
        string tmp = Paths.CorpusDb + ".new";
        try
        {
            Directory.CreateDirectory(Paths.Base);
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var c = Db.Open(keyHex, create: true, path: tmp))
                Db.EnsureSchema(c);
            SqliteConnection.ClearAllPools(); // zwolnij uchwyt do pliku tmp przed przenosinami
        }
        catch (Exception ex)
        {
            try { SqliteConnection.ClearAllPools(); if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return new("Nie moge utworzyc zaszyfrowanej bazy: " + ex.Message, null);
        }

        // 3) Commit: dopiero teraz utrwalamy sol, podmieniamy baze i zapisujemy tryb.
        try
        {
            Session.Lock(); // sprzataj ewentualna stara sesje dyskowa
            if (force) PurgeDataBoundToPreviousCorpus();
            Crypto.WriteSalt(salt);
            if (File.Exists(Paths.CorpusDb)) File.Delete(Paths.CorpusDb);
            File.Move(tmp, Paths.CorpusDb);
            Mode.Write(wantYubi ? "pin+yubi" : "pin");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return new("Nie moge zapisac nowego korpusu: " + ex.Message, null);
        }
        return new(null, keyHex);
    }

    static void PurgeDataBoundToPreviousCorpus()
    {
        DeleteFileIfExists(Paths.CorpusDb + "-wal");
        DeleteFileIfExists(Paths.CorpusDb + "-shm");
        DeleteFileIfExists(Paths.ImapAccountsFile);
        DeleteFileIfExists(Paths.GmailCancelFile);
        DeleteDirectoryIfExists(Paths.GmailTokensDir);
        DeleteDirectoryIfExists(Paths.BlobsDir);
        DeleteDirectoryIfExists(Paths.TempDir);
    }

    static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
