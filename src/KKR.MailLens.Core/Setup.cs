using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

/// <summary>
/// Jawna inicjacja korpusu: ustala PIN + (opcjonalnie) YubiKey i TWORZY zaszyfrowana pusta baze,
/// wiazac klucz z konkretnym PIN/kluczem. Wczesniej init byl niejawny (pierwszy unlock/harvest) -
/// literowka w PINie tworzyla baze na zlym kluczu. Teraz to osobny, potwierdzany krok.
/// </summary>
static class Setup
{
    static CorpusInstallFiles InstallFiles => new(
        Paths.CorpusDb, Paths.SaltFile, Paths.ModeFile, Paths.InstallJournalFile);

    public static bool IsInitialized
    {
        get
        {
            try { CorpusInstallation.Recover(InstallFiles); }
            catch { return false; }
            return File.Exists(Paths.CorpusDb) && File.Exists(Paths.SaltFile) && Mode.Read().Length > 0;
        }
    }

    public sealed record Result(string? Error, string? KeyHex);

    /// <summary>Tworzy zaszyfrowana pusta baze zwiazana z PIN(+YubiKey) i USTAWIA tryb. Zwraca klucz
    /// (bez zarzadzania sesja - wolajacy decyduje: GUI trzyma w RAM, CLI na dysku).</summary>
    public static Result Init(string pin, bool wantYubi, bool force)
    {
        if (string.IsNullOrEmpty(pin)) return new("Pusty PIN.", null);
        try { CorpusInstallation.Recover(InstallFiles); }
        catch (Exception ex) { return new("Nie mogę odtworzyć przerwanej inicjalizacji: " + ex.Message, null); }
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

        // 3) Commit: baza, sól i tryb są podmieniane odwracalnie; dziennik pozwala
        //    przywrócić stary zestaw także po przerwaniu procesu.
        try
        {
            Session.Lock(); // sprzataj ewentualna stara sesje dyskowa
            SqliteConnection.ClearAllPools();
            CorpusInstallation.Commit(InstallFiles, tmp, salt, wantYubi ? "pin+yubi" : "pin");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return new("Nie moge zapisac nowego korpusu: " + ex.Message, null);
        }
        if (force) PurgeDataBoundToPreviousCorpus();
        return new(null, keyHex);
    }

    static void PurgeDataBoundToPreviousCorpus()
    {
        DeleteFileIfExists(Paths.ImapAccountsFile);
        DeleteFileIfExists(Paths.GmailCancelFile);
        DeleteDirectoryIfExists(Paths.GmailTokensDir);
        DeleteDirectoryIfExists(Paths.BlobsDir);
        DeleteDirectoryIfExists(Paths.TempDir);
    }

    static void DeleteFileIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static void DeleteDirectoryIfExists(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
