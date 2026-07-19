using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit.Net.Imap;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ImapAttachmentDownloaderTests
{
    const string SessionKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestCleanup]
    public void Cleanup() => ImapAttachmentDownloader.ClientFactory = null;

    [TestMethod]
    public async Task Download_RejectsAttachmentBoundToDifferentAccountBeforeConnecting()
    {
        var account = new ImapAccount
        {
            Name = "other-account",
            Host = "imap.example.invalid",
            Port = 993,
            UseSsl = true,
            User = "neutral-user",
        };

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ImapAttachmentDownloader.DownloadAsync(account, SessionKey,
                Attachment(uidValidity: 81)));

        StringAssert.Contains(error.Message, "Konto IMAP");
    }

    [TestMethod]
    public async Task Download_RejectsFolderWhoseUidValidityDrifted()
    {
        using var server = new ScriptedImapServer(uidValidity: 999);
        ImapAttachmentDownloader.ClientFactory = () => new ImapClient
        {
            ServerCertificateValidationCallback = (_, _, _, _) => true,
        };
        var account = new ImapAccount
        {
            Name = "test-account",
            Host = "127.0.0.1",
            Port = server.Port,
            UseSsl = true,
            User = "neutral-user",
        };
        account.SetPassword("neutral-password", SessionKey);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ImapAttachmentDownloader.DownloadAsync(account, SessionKey,
                Attachment(uidValidity: 81)));

        StringAssert.Contains(error.Message, "UIDVALIDITY");
        IReadOnlyList<string> commands = server.Commands;
        Assert.IsTrue(commands.Any(command =>
            command.Contains("EXAMINE", StringComparison.OrdinalIgnoreCase)
            || command.Contains("SELECT", StringComparison.OrdinalIgnoreCase)),
            $"Klient nie otworzył folderu: {string.Join(" | ", commands)}");
        Assert.IsFalse(commands.Any(command =>
            command.Contains("FETCH", StringComparison.OrdinalIgnoreCase)),
            $"Treść wiadomości nie może być pobrana po dryfie UIDVALIDITY: {string.Join(" | ", commands)}");
    }

    static MailAttachmentRepository.Item Attachment(uint uidValidity)
    {
        string messageKey = new ImapMessageLocator("test-account", "INBOX", uidValidity, 42).Encode();
        string partKey = new ImapPartLocator("2", "base64").Encode();
        return new MailAttachmentRepository.Item(1, "imap:test-account:INBOX:42", "imap", messageKey,
            "2", partKey, "record.txt", "text/plain", 64, "", false, null, null);
    }

    /// <summary>Minimalny skryptowany serwer IMAP po TLS (certyfikat self-signed). Wystarcza,
    /// by prawdziwy MailKit przeszedł connect → login → EXAMINE i odsłonił strażnika UIDVALIDITY.</summary>
    sealed class ScriptedImapServer : IDisposable
    {
        readonly TcpListener listener;
        readonly X509Certificate2 certificate;
        readonly Task loop;
        readonly uint uidValidity;
        readonly List<string> commands = new();

        public int Port { get; }
        public IReadOnlyList<string> Commands { get { lock (commands) return commands.ToArray(); } }

        public ScriptedImapServer(uint uidValidity)
        {
            this.uidValidity = uidValidity;
            certificate = CreateSelfSignedCertificate();
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(ServeAsync);
        }

        async Task ServeAsync()
        {
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(certificate);
                using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(tls, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("* OK [CAPABILITY IMAP4rev1] neutral test server ready");
                while (true)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line is null) return;
                    lock (commands) commands.Add(line);
                    string[] parts = line.Split(' ', 3);
                    if (parts.Length < 2) continue;
                    string tag = parts[0];
                    switch (parts[1].ToUpperInvariant())
                    {
                        case "CAPABILITY":
                            await writer.WriteLineAsync("* CAPABILITY IMAP4rev1");
                            await writer.WriteLineAsync($"{tag} OK CAPABILITY completed");
                            break;
                        case "LOGIN":
                            await writer.WriteLineAsync($"{tag} OK LOGIN completed");
                            break;
                        case "LIST":
                            await writer.WriteLineAsync("* LIST (\\Noselect) \"/\" \"\"");
                            await writer.WriteLineAsync($"{tag} OK LIST completed");
                            break;
                        case "EXAMINE" or "SELECT":
                            await writer.WriteLineAsync("* 0 EXISTS");
                            await writer.WriteLineAsync("* 0 RECENT");
                            await writer.WriteLineAsync("* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)");
                            await writer.WriteLineAsync("* OK [PERMANENTFLAGS ()] limited");
                            await writer.WriteLineAsync($"* OK [UIDVALIDITY {uidValidity}] UIDs valid");
                            await writer.WriteLineAsync("* OK [UIDNEXT 1] predicted next UID");
                            await writer.WriteLineAsync($"{tag} OK [READ-ONLY] open completed");
                            break;
                        case "LOGOUT":
                            await writer.WriteLineAsync("* BYE neutral test server closing");
                            await writer.WriteLineAsync($"{tag} OK LOGOUT completed");
                            return;
                        default:
                            await writer.WriteLineAsync($"{tag} OK completed");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
                or System.Security.Authentication.AuthenticationException)
            {
            }
        }

        static X509Certificate2 CreateSelfSignedCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 ephemeral = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
        }

        public void Dispose()
        {
            listener.Stop();
            try { loop.Wait(TimeSpan.FromSeconds(5)); } catch { }
            certificate.Dispose();
        }
    }
}
