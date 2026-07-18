using System.Text.Json;

namespace KKR.MailLens;

sealed record CorpusInstallFiles(string Database, string Salt, string Mode, string Journal);

enum CorpusInstallStep
{
    BackupsCreated,
    DatabaseInstalled,
    SaltInstalled,
    ModeInstalled,
}

/// <summary>Odwracalna podmiana plików definiujących korpus. Usunięcie dziennika jest
/// punktem commit; do tego momentu każdy błąd lub następne uruchomienie odtwarza stary zestaw.</summary>
static class CorpusInstallation
{
    sealed record JournalState(string Id, bool HadDatabase, bool HadSalt, bool HadMode,
        bool HadWal, bool HadShm);

    public static void Commit(CorpusInstallFiles files, string newDatabase, ReadOnlySpan<byte> salt,
        string mode, Action<CorpusInstallStep>? checkpoint = null)
    {
        ValidateFiles(files, newDatabase);
        if (!File.Exists(newDatabase)) throw new FileNotFoundException("Brak nowej bazy korpusu.", newDatabase);
        if (salt.IsEmpty) throw new ArgumentException("Sól korpusu jest pusta.", nameof(salt));
        if (mode is not ("pin" or "pin+yubi")) throw new ArgumentException("Nieprawidłowy tryb korpusu.", nameof(mode));

        using FileStream installLock = AcquireLock(files);
        RecoverUnlocked(files);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(files.Database))!);
        string id = Guid.NewGuid().ToString("N");
        string stagedSalt = Stage(files.Salt, id);
        string stagedMode = Stage(files.Mode, id);
        string journalWrite = files.Journal + ".write";
        string committedJournal = CommittedJournal(files);
        var state = new JournalState(id, File.Exists(files.Database), File.Exists(files.Salt),
            File.Exists(files.Mode), File.Exists(Wal(files)), File.Exists(Shm(files)));

        try
        {
            DeleteBestEffort(journalWrite);
            WriteBytes(stagedSalt, salt);
            WriteText(stagedMode, mode);
            WriteText(journalWrite, JsonSerializer.Serialize(state));
            File.Move(journalWrite, files.Journal, overwrite: true);

            BackupExisting(files.Database, BackupPath(files.Database, id));
            BackupExisting(files.Salt, BackupPath(files.Salt, id));
            BackupExisting(files.Mode, BackupPath(files.Mode, id));
            BackupExisting(Wal(files), BackupPath(Wal(files), id));
            BackupExisting(Shm(files), BackupPath(Shm(files), id));
            checkpoint?.Invoke(CorpusInstallStep.BackupsCreated);

            File.Move(newDatabase, files.Database);
            checkpoint?.Invoke(CorpusInstallStep.DatabaseInstalled);
            File.Move(stagedSalt, files.Salt);
            checkpoint?.Invoke(CorpusInstallStep.SaltInstalled);
            File.Move(stagedMode, files.Mode);
            checkpoint?.Invoke(CorpusInstallStep.ModeInstalled);

            // Atomowa zmiana nazwy jest punktem commit. Marker pozostaje do czasu usunięcia
            // wszystkich kopii, więc awaria procesu nie zostawia starego korpusu jako artefaktu.
            File.Move(files.Journal, committedJournal, overwrite: true);
            FinalizeCommitted(files);
        }
        catch (Exception installError)
        {
            try { RecoverUnlocked(files); }
            catch (Exception recoveryError)
            {
                throw new AggregateException("Nie udało się podmienić ani odtworzyć korpusu.",
                    installError, recoveryError);
            }
            throw;
        }
        finally
        {
            DeleteBestEffort(journalWrite);
            DeleteBestEffort(stagedSalt);
            DeleteBestEffort(stagedMode);
            DeleteBestEffort(newDatabase);
        }
    }

    public static void Recover(CorpusInstallFiles files)
    {
        using FileStream installLock = AcquireLock(files);
        RecoverUnlocked(files);
    }

    static void RecoverUnlocked(CorpusInstallFiles files)
    {
        FinalizeCommitted(files);
        if (!File.Exists(files.Journal)) return;
        JournalState state = ReadJournal(files.Journal);

        Restore(files.Database, BackupPath(files.Database, state.Id), state.HadDatabase);
        Restore(files.Salt, BackupPath(files.Salt, state.Id), state.HadSalt);
        Restore(files.Mode, BackupPath(files.Mode, state.Id), state.HadMode);
        Restore(Wal(files), BackupPath(Wal(files), state.Id), state.HadWal);
        Restore(Shm(files), BackupPath(Shm(files), state.Id), state.HadShm);
        DeleteBestEffort(Stage(files.Salt, state.Id));
        DeleteBestEffort(Stage(files.Mode, state.Id));
        File.Delete(files.Journal);
    }

    static void FinalizeCommitted(CorpusInstallFiles files)
    {
        string marker = CommittedJournal(files);
        if (!File.Exists(marker)) return;
        JournalState state = ReadJournal(marker);
        DeleteBackupBestEffort(files.Database, state.Id);
        DeleteBackupBestEffort(files.Salt, state.Id);
        DeleteBackupBestEffort(files.Mode, state.Id);
        DeleteBackupBestEffort(Wal(files), state.Id);
        DeleteBackupBestEffort(Shm(files), state.Id);
        DeleteBestEffort(Stage(files.Salt, state.Id));
        DeleteBestEffort(Stage(files.Mode, state.Id));
        if (!BackupExists(files, state.Id)) DeleteBestEffort(marker);
    }

    static JournalState ReadJournal(string path)
    {
        JournalState state;
        try
        {
            state = JsonSerializer.Deserialize<JournalState>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Pusty dziennik instalacji korpusu.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Uszkodzony dziennik instalacji korpusu.", ex);
        }
        if (state.Id.Length != 32 || state.Id.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Nieprawidłowy identyfikator dziennika instalacji korpusu.");
        return state;
    }

    static bool BackupExists(CorpusInstallFiles files, string id) =>
        File.Exists(BackupPath(files.Database, id)) || File.Exists(BackupPath(files.Salt, id))
        || File.Exists(BackupPath(files.Mode, id)) || File.Exists(BackupPath(Wal(files), id))
        || File.Exists(BackupPath(Shm(files), id));

    static FileStream AcquireLock(CorpusInstallFiles files)
    {
        string path = files.Journal + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.WriteThrough);
    }

    static void Restore(string target, string backup, bool existed)
    {
        if (File.Exists(backup))
        {
            if (File.Exists(target)) File.Delete(target);
            File.Move(backup, target);
        }
        else if (!existed && File.Exists(target)) File.Delete(target);
    }

    static void BackupExisting(string source, string destination)
    {
        if (File.Exists(source)) File.Move(source, destination);
    }

    static void WriteBytes(string path, ReadOnlySpan<byte> value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        stream.Write(value);
        stream.Flush(flushToDisk: true);
    }

    static void WriteText(string path, string value)
        => WriteBytes(path, System.Text.Encoding.UTF8.GetBytes(value));

    static void ValidateFiles(CorpusInstallFiles files, string newDatabase)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(files.Database))!;
        foreach (string path in new[] { files.Salt, files.Mode, files.Journal, newDatabase })
            if (!string.Equals(directory, Path.GetDirectoryName(Path.GetFullPath(path)),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pliki instalacji korpusu muszą leżeć w jednym katalogu.");
    }

    static string Wal(CorpusInstallFiles files) => files.Database + "-wal";
    static string Shm(CorpusInstallFiles files) => files.Database + "-shm";
    static string CommittedJournal(CorpusInstallFiles files) => files.Journal + ".committed";
    static string BackupPath(string path, string id) => path + ".backup-" + id;
    static string Stage(string path, string id) => path + ".new-" + id;
    static void DeleteBackupBestEffort(string path, string id) => DeleteBestEffort(BackupPath(path, id));
    static void DeleteBestEffort(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
