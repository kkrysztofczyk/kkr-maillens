using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class GmailMailboxImporterTests
{
    const string SessionKey =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task Import_UsesExistingSynchronizerAndAssignsMessagesToMailboxSource()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        MailboxSourceRecord source = MailboxSourceRepository.Upsert(db.Connection, new(
            MailboxProvider.Gmail,
            account.Email,
            "Test mailbox",
            $"gmail-account:{account.Id}"));
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [source.Id]);
        MailboxImportSourceRecord queued =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id).Single();

        var api = new FakeGmailApiClient();
        api.Messages["message-1"] = GmailTestMessage.Create("message-1");
        api.MessagePages[""] = () => new GmailMessagePage(["message-1"], null);
        var progress = new List<MailboxImportProgress>();
        var importer = new GmailMailboxImporter((selected, key, cancellationToken) =>
        {
            Assert.AreEqual(account.Id, selected.Id);
            Assert.AreEqual(SessionKey, key);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IGmailApiClient>(api);
        });

        MailboxImportResult result = await importer.ImportAsync(
            new MailboxImportRequest(
                db.Connection,
                SessionKey,
                queued,
                forceFull: false,
                new CallbackProgress<MailboxImportProgress>(progress.Add)),
            CancellationToken.None);

        Assert.AreEqual(1, result.Processed);
        Assert.AreEqual(1, result.Inserted);
        Assert.IsTrue(result.WasFullImport);
        Assert.AreEqual(source.Id, db.ScalarLong(
            "SELECT mailbox_source_id FROM mails WHERE subject='Test Record';"));
        Assert.AreEqual("imported", progress[^1].Phase);
        Assert.AreEqual(1, progress[^1].Inserted);
    }

    [TestMethod]
    public async Task Import_RejectsCredentialReferenceForDifferentAccount()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        MailboxSourceRecord source = MailboxSourceRepository.Upsert(db.Connection, new(
            MailboxProvider.Gmail,
            "recipient@example.invalid",
            "Test mailbox",
            $"gmail-account:{account.Id}"));
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [source.Id]);
        MailboxImportSourceRecord queued =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id).Single();
        var importer = new GmailMailboxImporter((_, _, _) =>
            throw new AssertFailedException("Klient API nie powinien zostać utworzony."));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            importer.ImportAsync(
                new MailboxImportRequest(db.Connection, SessionKey, queued, forceFull: false),
                CancellationToken.None));
    }

    [TestMethod]
    public void ImportRequest_ToStringDoesNotExposeSessionKey()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        MailboxSourceRecord source = MailboxSourceRepository.Upsert(db.Connection, new(
            MailboxProvider.Gmail,
            account.Email,
            "Test mailbox",
            $"gmail-account:{account.Id}"));
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [source.Id]);
        MailboxImportSourceRecord queued =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id).Single();

        var request = new MailboxImportRequest(
            db.Connection,
            SessionKey,
            queued,
            forceFull: false);

        Assert.IsFalse(request.ToString().Contains(SessionKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OAuthPersistence_RegistersSourceAndDisconnectRemovesIt()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "kkr-maillens-gmail-importer-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new TestDatabase();
            using var store = new GmailTokenStore(directory, SessionKey);
            var token = new TokenResponse
            {
                RefreshToken = "neutral-refresh-token",
                AccessToken = "neutral-access-token",
            };

            GmailAccountRecord account = await GmailOAuth.PersistAuthorizedAccountAsync(
                db.Connection,
                store,
                "sender@example.invalid",
                token);

            MailboxSourceRecord source = MailboxSourceRepository.Find(
                db.Connection,
                MailboxProvider.Gmail,
                "sender@example.invalid")!;
            Assert.IsNotNull(source);
            Assert.AreEqual($"gmail-account:{account.Id}", source.CredentialReference);

            GmailRepository.DeleteAccount(db.Connection, account.Id);

            Assert.IsNull(MailboxSourceRepository.Find(
                db.Connection,
                MailboxProvider.Gmail,
                "sender@example.invalid"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
