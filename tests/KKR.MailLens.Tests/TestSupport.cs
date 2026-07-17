using System.Collections.Concurrent;
using System.Text;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens.Tests;

sealed class TestDatabase : IDisposable
{
    readonly string _directory;
    public SqliteConnection Connection { get; }

    static TestDatabase() => SQLitePCL.Batteries_V2.Init();

    public TestDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "test.db");
        Connection = Db.Open(new string('A', 64), create: true, path: path);
        Db.EnsureSchema(Connection);
    }

    public GmailAccountRecord AddAccount(string email = "sender@example.invalid") =>
        GmailRepository.UpsertAccount(Connection, email, email, "token-" + Guid.NewGuid().ToString("N"));

    public long ScalarLong(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public string ScalarText(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }

    public void Dispose()
    {
        Connection.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}

sealed class FakeGmailApiClient : IGmailApiClient
{
    public GmailProfile Profile { get; set; } = new("sender@example.invalid", "100");
    public IReadOnlyList<GmailApiLabel> Labels { get; set; } =
    [
        new("INBOX", "INBOX", "system"),
        new("UNREAD", "UNREAD", "system"),
        new("Label_Test", "Test Label", "user"),
    ];
    public ConcurrentDictionary<string, GmailApiMessage> Messages { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, byte[]> AttachmentBytes { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, Exception> MessageErrors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Func<GmailMessagePage>> MessagePages { get; } = new(StringComparer.Ordinal);
    public Queue<Func<GmailHistoryPage>> HistoryPages { get; } = new();
    public List<string?> RequestedPageTokens { get; } = new();

    public Task<GmailProfile> GetProfileAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Profile); }

    public Task<IReadOnlyList<GmailApiLabel>> GetLabelsAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Labels); }

    public Task<GmailMessagePage> ListMessageIdsAsync(string? pageToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestedPageTokens.Add(pageToken);
        string key = pageToken ?? "";
        if (!MessagePages.TryGetValue(key, out var page)) return Task.FromResult(new GmailMessagePage(Array.Empty<string>(), null));
        return Task.FromResult(page());
    }

    public Task<GmailApiMessage> GetMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MessageErrors.TryGetValue(messageId, out var error)) throw error;
        return Task.FromResult(Messages[messageId]);
    }

    public Task<byte[]> GetAttachmentBytesAsync(string messageId, string attachmentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AttachmentBytes[$"{messageId}\n{attachmentId}"]);
    }

    public Task<GmailHistoryPage> ListHistoryAsync(string startHistoryId, string? pageToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HistoryPages.Count == 0) return Task.FromResult(new GmailHistoryPage(Array.Empty<string>(), Array.Empty<string>(), startHistoryId, null));
        return Task.FromResult(HistoryPages.Dequeue()());
    }

    public void Dispose() { }
}

static class GmailTestMessage
{
    public static GmailApiMessage Create(string id, string subject = "Test Record", string body = "Neutralny tekst",
        IReadOnlyList<string>? labels = null, bool html = false, IReadOnlyList<GmailApiPart>? extraParts = null)
    {
        var parts = new List<GmailApiPart>
        {
            new()
            {
                PartId = "0",
                MimeType = html ? "text/html" : "text/plain",
                Data = Base64Url(body),
                Headers = [new("Content-Type", html ? "text/html; charset=utf-8" : "text/plain; charset=utf-8")],
            },
        };
        if (extraParts != null) parts.AddRange(extraParts);
        return new GmailApiMessage
        {
            Id = id,
            ThreadId = "thread-" + id,
            HistoryId = "100",
            InternalDateUnixMs = 1_700_000_000_000,
            SizeEstimate = 512,
            LabelIds = labels ?? ["INBOX"],
            Payload = new GmailApiPart
            {
                MimeType = "multipart/mixed",
                Headers =
                [
                    new("Message-ID", $"<{id}@example.invalid>"),
                    new("Date", "Tue, 14 Nov 2023 22:13:20 +0000"),
                    new("From", "Neutral Sender <sender@example.invalid>"),
                    new("To", "recipient@example.invalid"),
                    new("Cc", "copy@example.invalid"),
                    new("Subject", subject),
                ],
                Parts = parts,
            },
        };
    }

    public static string Base64Url(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
