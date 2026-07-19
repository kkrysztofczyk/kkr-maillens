using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

/// <summary>Testy integracyjne procesu Workera (Worker/Program.cs) uruchamianego jako osobny proces.</summary>
[TestClass]
[DoNotParallelize]
public sealed class WorkerProgramTests
{
    const string SessionKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    static string WorkerExecutable => Path.Combine(AppContext.BaseDirectory, "KKR.MailLens.Worker.exe");

    [TestMethod]
    public void DirectLaunch_WithoutRestrictedTokenIsRejectedWithExitCode4()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Test wymaga Windows.");
        if (RestrictedWorkerProcess.IsCurrentProcessRestricted())
            Assert.Inconclusive("Proces testowy sam działa z ograniczonym tokenem.");
        Assert.IsTrue(File.Exists(WorkerExecutable), $"Brak zbudowanego Workera: {WorkerExecutable}");

        string dataDir = TemporaryDirectory();
        try
        {
            var start = new ProcessStartInfo(WorkerExecutable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.Environment["KKR_MAILLENS_DIR"] = dataDir;
            using Process process = Process.Start(start)!;
            string stderr = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Assert.Fail("Worker uruchomiony bezpośrednio nie zakończył się w limicie czasu.");
            }

            Assert.AreEqual(4, process.ExitCode,
                $"Worker uruchomiony bez ograniczonego tokenu musi odmówić pracy (stderr: {stderr}).");
            StringAssert.Contains(stderr, "ograniczony launcher");
            Assert.IsFalse(File.Exists(Path.Combine(dataDir, "corpus.db")),
                "Odrzucony Worker nie może dotykać katalogu danych.");
        }
        finally { try { Directory.Delete(dataDir, recursive: true); } catch { } }
    }

    [TestMethod]
    public void SessionLock_CancelsRunningJobAbandonsItWithoutAttemptAndExitsWithCode2()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Test wymaga Windows.");
        if (!CanLaunchRestrictedProcess())
            Assert.Inconclusive("Środowisko nie pozwala uruchomić procesu z ograniczonym tokenem " +
                "(brak dostępu do stacji okien/pulpitu dla ograniczonego SID).");
        Assert.IsTrue(File.Exists(WorkerExecutable), $"Brak zbudowanego Workera: {WorkerExecutable}");
        if (Ipc.Request("STATUS", 250) is not null)
            Assert.Inconclusive("Pipe agenta sesji jest już zajęty (uruchomione GUI KKR MailLens?).");

        string dataDir = TemporaryDirectory();
        bool binaryAclGranted = false;
        try
        {
            GrantUsers(dataDir, FileSystemRights.Modify);
            GrantUsers(AppContext.BaseDirectory, FileSystemRights.ReadAndExecute);
            binaryAclGranted = true;

            using var imapBlackHole = new TcpBlackHole();
            WriteImapAccounts(dataDir, imapBlackHole.Port);
            string databasePath = Path.Combine(dataDir, "corpus.db");
            using (SqliteConnection connection = Db.Open(SessionKey, create: true, path: databasePath))
            {
                Db.EnsureSchema(connection);
                Corpus.Upsert(connection, [ImapMessage(imapBlackHole.Port)], "2026-01-01 00:00:00");
                Assert.AreEqual(1, ScalarLong(connection,
                    "SELECT count(*) FROM processing_jobs WHERE job_type='download' AND status='pending';"));
            }
            SqliteConnection.ClearAllPools();

            using var agent = new FakeSessionAgent(SessionKey);
            Assert.AreEqual("UNLOCKED 600 pin", Ipc.Request("STATUS", 5_000),
                "Testowy agent sesji nie odpowiada przez pipe.");

            Environment.SetEnvironmentVariable("KKR_MAILLENS_DIR", dataDir);
            // Ograniczony token nie odczyta SDK z profilu użytkownika (np. %USERPROFILE%\.dotnet);
            // czyścimy DOTNET_ROOT*, żeby apphost Workera użył instalacji z Program Files.
            var dotnetRoots = new Dictionary<string, string?>();
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string name = (string)entry.Key;
                if (name.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase))
                    dotnetRoots[name] = (string?)entry.Value;
            }
            foreach (string name in dotnetRoots.Keys) Environment.SetEnvironmentVariable(name, null);
            try
            {
                using RestrictedWorkerProcess worker = RestrictedWorkerProcess.Start(
                    WorkerExecutable, "", 1024L * 1024 * 1024);
                try
                {
                    bool running = WaitFor(() => Query(databasePath,
                        "SELECT count(*) FROM processing_jobs WHERE status='running';") == 1,
                        TimeSpan.FromSeconds(60));
                    Assert.IsTrue(running,
                        $"Worker nie podjął zadania pobierania: {DescribeJobs(databasePath)}, " +
                        $"exited={worker.Process.HasExited}" +
                        (worker.Process.HasExited ? $", exit={worker.Process.ExitCode}" : ""));

                    agent.Lock();
                    Assert.IsTrue(worker.Process.WaitForExit(60_000),
                        "Worker nie zakończył się po zablokowaniu sesji.");
                    Assert.AreEqual(2, worker.Process.ExitCode,
                        $"Zablokowana sesja musi kończyć Workera kodem 2: {DescribeJobs(databasePath)}");
                }
                finally
                {
                    try { if (!worker.Process.HasExited) worker.Process.Kill(entireProcessTree: true); }
                    catch { }
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("KKR_MAILLENS_DIR", null);
                foreach ((string name, string? value) in dotnetRoots)
                    Environment.SetEnvironmentVariable(name, value);
            }

            SqliteConnection.ClearAllPools();
            using (SqliteConnection connection = Db.Open(SessionKey, create: false, path: databasePath))
            {
                Assert.AreEqual("pending", ScalarText(connection, "SELECT status FROM processing_jobs;"),
                    "Anulowane zadanie musi wrócić do kolejki.");
                Assert.AreEqual(0, ScalarLong(connection, "SELECT attempts FROM processing_jobs;"),
                    "Anulowanie nie może konsumować próby zadania.");
                Assert.AreEqual(0, ScalarLong(connection,
                    "SELECT count(*) FROM processing_jobs WHERE locked_by IS NOT NULL OR lease_until IS NOT NULL;"));
                Assert.AreEqual(0, ScalarLong(connection,
                    "SELECT count(*) FROM processing_jobs WHERE error_code IS NOT NULL;"));
            }
        }
        finally
        {
            if (binaryAclGranted) { try { RevokeUsers(AppContext.BaseDirectory); } catch { } }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }

    /// <summary>Sprawdza, czy środowisko w ogóle potrafi uruchomić proces z ograniczonym tokenem.
    /// Część sandboxów/CI odmawia (STATUS_ACCESS_DENIED) z powodu DACL stacji okien; wtedy testy
    /// integracyjne oparte o realny launcher są pomijane zamiast fałszywie czerwone.</summary>
    static bool CanLaunchRestrictedProcess()
    {
        if (!OperatingSystem.IsWindows() || !RestrictedWorkerProcess.CanCreateRestrictedToken())
            return false;
        try
        {
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            using RestrictedWorkerProcess probe = RestrictedWorkerProcess.Start(
                cmd, "/d /c exit 0", 128L * 1024 * 1024);
            if (!probe.Process.WaitForExit(10_000))
            {
                try { probe.Process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return probe.Process.ExitCode == 0;
        }
        catch { return false; }
    }

    static HarvestedMail ImapMessage(int port)
    {
        string messageKey = new ImapMessageLocator("test-account", "INBOX", 42, 81).Encode();
        string partKey = new ImapPartLocator("2", "base64").Encode();
        return new HarvestedMail
        {
            EntryId = "imap:test-account:INBOX:81",
            StoreId = "imap:test-account",
            FolderPath = "imap://test-account/INBOX",
            FolderLeaf = "INBOX",
            Received = "2026-01-01 00:00:00",
            Sent = "2026-01-01 00:00:00",
            SenderName = "Neutral Sender",
            SenderEmail = "sender@example.invalid",
            ToRecips = "recipient@example.invalid",
            Subject = "Test Record",
            Body = "Neutralny tekst wiadomości",
            AttachmentProvider = "imap",
            ProviderMessageKey = messageKey,
            HasAttachments = true,
            AttachmentNames = "record.txt",
            Attachments =
            [
                new HarvestedAttachment("2", partKey, "record.txt", "text/plain", 64, "", false),
            ],
        };
    }

    static void WriteImapAccounts(string dataDir, int port)
    {
        var accounts = new ImapAccounts();
        accounts.Accounts.Add(new ImapAccount
        {
            Name = "test-account",
            Host = "127.0.0.1",
            Port = port,
            UseSsl = true,
            User = "neutral-user",
        });
        File.WriteAllText(Path.Combine(dataDir, "imap-accounts.json"), JsonSerializer.Serialize(accounts));
    }

    static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(200);
        }
        return condition();
    }

    static long Query(string databasePath, string sql)
    {
        using SqliteConnection connection = Db.Open(SessionKey, create: false, path: databasePath);
        return ScalarLong(connection, sql);
    }

    static string DescribeJobs(string databasePath)
    {
        try
        {
            using SqliteConnection connection = Db.Open(SessionKey, create: false, path: databasePath);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT id||':'||job_type||':'||status||':attempts='||attempts||':'||COALESCE(error_code,'-') FROM processing_jobs;";
            using SqliteDataReader reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read()) rows.Add(reader.GetString(0));
            return rows.Count == 0 ? "(brak zadań)" : string.Join(" | ", rows);
        }
        catch (Exception ex) { return $"(diagnostyka niedostępna: {ex.GetType().Name})"; }
    }

    static long ScalarLong(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static string ScalarText(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }

    static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    static void GrantUsers(string path, FileSystemRights rights)
    {
        var directory = new DirectoryInfo(path);
        DirectorySecurity security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(UsersSid, rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    static void RevokeUsers(string path)
    {
        var directory = new DirectoryInfo(path);
        DirectorySecurity security = directory.GetAccessControl();
        security.PurgeAccessRules(UsersSid);
        directory.SetAccessControl(security);
    }

    /// <summary>Nasłuchuje na loopbacku i przyjmuje połączenia bez odpowiedzi — pobieranie IMAP
    /// wisi na handshake'u TLS aż do anulowania, co daje deterministycznie długie zadanie.</summary>
    sealed class TcpBlackHole : IDisposable
    {
        readonly TcpListener listener;
        readonly List<TcpClient> clients = new();
        readonly Task loop;

        public int Port { get; }

        public TcpBlackHole()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(AcceptAsync);
        }

        async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    lock (clients) clients.Add(client);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            listener.Stop();
            lock (clients) { foreach (TcpClient client in clients) client.Dispose(); }
            try { loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    /// <summary>Podstawiony agent sesji GUI: serwer named-pipe zgodny z protokołem Ipc.
    /// ACL dopuszcza BUILTIN\Users, żeby ograniczony token Workera mógł otworzyć pipe.</summary>
    sealed class FakeSessionAgent : IDisposable
    {
        readonly Thread thread;
        readonly string key;
        volatile bool stop;
        volatile bool locked;

        public FakeSessionAgent(string key)
        {
            this.key = key;
            thread = new Thread(Loop) { IsBackground = true, Name = "kkr-maillens-test-agent" };
            thread.Start();
        }

        public void Lock() => locked = true;

        void Loop()
        {
            while (!stop)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    var security = new PipeSecurity();
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
                        PipeAccessRights.FullControl, AccessControlType.Allow));
                    security.AddAccessRule(new PipeAccessRule(UsersSid,
                        PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    server = NamedPipeServerStreamAcl.Create(Ipc.PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security);
                    server.WaitForConnection();
                    if (stop) break;
                    var reader = new StreamReader(server);
                    var writer = new StreamWriter(server) { AutoFlush = true };
                    string? verb = reader.ReadLine();
                    writer.WriteLine(verb switch
                    {
                        "GETKEY" => locked ? "LOCKED" : key,
                        "STATUS" => locked ? "LOCKED pin" : "UNLOCKED 600 pin",
                        _ => "ERR",
                    });
                    try { server.WaitForPipeDrain(); } catch { }
                }
                catch { Thread.Sleep(100); }
                finally { try { server?.Dispose(); } catch { } }
            }
        }

        public void Dispose()
        {
            stop = true;
            try
            {
                using var client = new NamedPipeClientStream(".", Ipc.PipeName, PipeDirection.InOut);
                client.Connect(200);
            }
            catch { }
            thread.Join(2_000);
        }
    }
}
