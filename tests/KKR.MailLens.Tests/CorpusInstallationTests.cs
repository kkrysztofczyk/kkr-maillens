using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class CorpusInstallationTests
{
    [TestMethod]
    public void Commit_ReplacesCompleteCorpusSetAndRemovesOldSidecars()
    {
        using var workspace = new InstallWorkspace();
        workspace.WriteOldSet();
        workspace.WriteNewDatabase();

        CorpusInstallation.Commit(workspace.Files, workspace.NewDatabase, "new-salt"u8,
            "pin+yubi");

        Assert.AreEqual("new-database", File.ReadAllText(workspace.Files.Database));
        Assert.AreEqual("new-salt", File.ReadAllText(workspace.Files.Salt));
        Assert.AreEqual("pin+yubi", File.ReadAllText(workspace.Files.Mode));
        Assert.IsFalse(File.Exists(workspace.Files.Database + "-wal"));
        Assert.IsFalse(File.Exists(workspace.Files.Database + "-shm"));
        Assert.IsFalse(File.Exists(workspace.Files.Journal));
        Assert.AreEqual(0, Directory.GetFiles(workspace.Directory, "*.backup-*").Length);
    }

    [TestMethod]
    public void Commit_FailureAfterDatabaseMoveRestoresEntireOldSet()
    {
        using var workspace = new InstallWorkspace();
        workspace.WriteOldSet();
        workspace.WriteNewDatabase();

        Assert.Throws<IOException>(() => CorpusInstallation.Commit(
            workspace.Files, workspace.NewDatabase, "new-salt"u8, "pin",
            step =>
            {
                if (step == CorpusInstallStep.DatabaseInstalled)
                    throw new IOException("Neutralny błąd podmiany");
            }));

        Assert.AreEqual("old-database", File.ReadAllText(workspace.Files.Database));
        Assert.AreEqual("old-salt", File.ReadAllText(workspace.Files.Salt));
        Assert.AreEqual("pin+yubi", File.ReadAllText(workspace.Files.Mode));
        Assert.AreEqual("old-wal", File.ReadAllText(workspace.Files.Database + "-wal"));
        Assert.AreEqual("old-shm", File.ReadAllText(workspace.Files.Database + "-shm"));
        Assert.IsFalse(File.Exists(workspace.Files.Journal));
        Assert.AreEqual(0, Directory.GetFiles(workspace.Directory, "*.backup-*").Length);
    }

    [TestMethod]
    public void Commit_FailedFreshInstallLeavesNoPartialCorpus()
    {
        using var workspace = new InstallWorkspace();
        workspace.WriteNewDatabase();

        Assert.Throws<IOException>(() => CorpusInstallation.Commit(
            workspace.Files, workspace.NewDatabase, "new-salt"u8, "pin",
            step =>
            {
                if (step == CorpusInstallStep.SaltInstalled)
                    throw new IOException("Neutralny błąd podmiany");
            }));

        Assert.IsFalse(File.Exists(workspace.Files.Database));
        Assert.IsFalse(File.Exists(workspace.Files.Salt));
        Assert.IsFalse(File.Exists(workspace.Files.Mode));
        Assert.IsFalse(File.Exists(workspace.Files.Journal));
    }

    [TestMethod]
    public void Recover_CommittedMarkerKeepsNewSetAndRemovesOldBackup()
    {
        using var workspace = new InstallWorkspace();
        const string id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        File.WriteAllText(workspace.Files.Database, "new-database", Encoding.UTF8);
        File.WriteAllText(workspace.Files.Salt, "new-salt", Encoding.UTF8);
        File.WriteAllText(workspace.Files.Mode, "pin", Encoding.UTF8);
        File.WriteAllText(workspace.Files.Database + ".backup-" + id, "old-database", Encoding.UTF8);
        File.WriteAllText(workspace.Files.Journal + ".committed",
            "{\"Id\":\"" + id + "\",\"HadDatabase\":true,\"HadSalt\":true,\"HadMode\":true," +
            "\"HadWal\":false,\"HadShm\":false}", Encoding.UTF8);

        CorpusInstallation.Recover(workspace.Files);

        Assert.AreEqual("new-database", File.ReadAllText(workspace.Files.Database));
        Assert.AreEqual("new-salt", File.ReadAllText(workspace.Files.Salt));
        Assert.AreEqual("pin", File.ReadAllText(workspace.Files.Mode));
        Assert.IsFalse(File.Exists(workspace.Files.Database + ".backup-" + id));
        Assert.IsFalse(File.Exists(workspace.Files.Journal + ".committed"));
    }

    sealed class InstallWorkspace : IDisposable
    {
        public string Directory { get; } = Path.Combine(
            Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        public CorpusInstallFiles Files { get; }
        public string NewDatabase { get; }

        public InstallWorkspace()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Files = new CorpusInstallFiles(Path.Combine(Directory, "corpus.db"),
                Path.Combine(Directory, "salt.bin"), Path.Combine(Directory, "mode.txt"),
                Path.Combine(Directory, "corpus-install.pending"));
            NewDatabase = Files.Database + ".new";
        }

        public void WriteOldSet()
        {
            File.WriteAllText(Files.Database, "old-database", Encoding.UTF8);
            File.WriteAllText(Files.Salt, "old-salt", Encoding.UTF8);
            File.WriteAllText(Files.Mode, "pin+yubi", Encoding.UTF8);
            File.WriteAllText(Files.Database + "-wal", "old-wal", Encoding.UTF8);
            File.WriteAllText(Files.Database + "-shm", "old-shm", Encoding.UTF8);
        }

        public void WriteNewDatabase() => File.WriteAllText(NewDatabase, "new-database", Encoding.UTF8);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { }
        }
    }
}
