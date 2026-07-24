using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class ImapMailboxImporterTests
{
    const string SessionKey =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task Import_UsesQueuedSnapshotAndAssignsMessagesToMailboxSource()
    {
        using var db = new TestDatabase();
        ImapAccount configured = Account();
        MailboxSourceRecord source = ImapMailboxRegistration.Register(db.Connection, configured, maxPerFolder: 250);
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [source.Id]);
        MailboxImportSourceRecord queued =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id).Single();

        ImapAccount changedAfterQueueCreation = Account();
        changedAfterQueueCreation.Host = "changed.example.invalid";
        var progress = new List<MailboxImportProgress>();
        var importer = new ImapMailboxImporter(
            name =>
            {
                Assert.AreEqual(configured.Name, name);
                return changedAfterQueueCreation;
            },
            (account, key, from, maxPerFolder, onFolder, onProgress, flush, batchSize, cancellationToken) =>
            {
                Assert.AreEqual("imap.example.invalid", account.Host);
                Assert.AreEqual(993, account.Port);
                Assert.AreEqual("neutral-user", account.User);
                Assert.AreEqual(SessionKey, key);
                Assert.AreEqual(250, maxPerFolder);
                Assert.IsNull(from);
                Assert.AreEqual(500, batchSize);
                cancellationToken.ThrowIfCancellationRequested();
                onFolder("private folder name");
                onProgress?.Invoke(1, 1);
                flush([Message(account.Name)]);
                return new ImapHarvestResult(1, 2);
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
        Assert.AreEqual(2, result.Errors);
        Assert.AreEqual(source.Id, db.ScalarLong("SELECT mailbox_source_id FROM mails;"));
        Assert.AreEqual("listing-folders", progress[0].Phase);
        Assert.AreEqual("imported", progress[^1].Phase);
        Assert.AreEqual(2, progress[^1].Errors);
        Assert.IsFalse(progress.Any(item =>
            item.Phase.Contains("private", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Registration_HashesCanonicalIdentityAndNeverStoresPassword()
    {
        using var db = new TestDatabase();
        ImapAccount first = Account();
        first.SetPassword("neutral-password", SessionKey);

        MailboxSourceRecord source = ImapMailboxRegistration.Register(db.Connection, first);
        var renamed = new ImapAccount
        {
            Name = "renamed-account",
            Host = "IMAP.EXAMPLE.INVALID",
            Port = first.Port,
            UseSsl = first.UseSsl,
            User = first.User,
        };
        var otherUser = new ImapAccount
        {
            Name = first.Name,
            Host = first.Host,
            Port = first.Port,
            UseSsl = first.UseSsl,
            User = "other-user",
        };

        Assert.AreEqual(source.ExternalKey, ImapMailboxRegistration.ExternalKey(renamed));
        Assert.AreNotEqual(source.ExternalKey, ImapMailboxRegistration.ExternalKey(otherUser));
        Assert.IsFalse(source.SettingsJson.Contains("neutral-password", StringComparison.Ordinal));
        Assert.IsFalse(source.SettingsJson.Contains(first.PasswordProtected, StringComparison.Ordinal));
        StringAssert.Contains(source.CredentialReference!, first.Name);
    }

    [TestMethod]
    public async Task Import_HonorsCancellationBeforeOpeningConnection()
    {
        using var db = new TestDatabase();
        ImapAccount account = Account();
        MailboxSourceRecord source = ImapMailboxRegistration.Register(db.Connection, account);
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [source.Id]);
        MailboxImportSourceRecord queued =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id).Single();
        bool harvested = false;
        var importer = new ImapMailboxImporter(
            _ => account,
            (_, _, _, _, _, _, _, _, _) =>
            {
                harvested = true;
                return new ImapHarvestResult(0, 0);
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            importer.ImportAsync(
                new MailboxImportRequest(db.Connection, SessionKey, queued, forceFull: false),
                cancellation.Token));

        Assert.IsFalse(harvested);
    }

    [TestMethod]
    public void Registration_ZeroLimitMeansUnlimited()
    {
        using var db = new TestDatabase();

        MailboxSourceRecord source = ImapMailboxRegistration.Register(
            db.Connection,
            Account(),
            maxPerFolder: 0);

        Assert.AreEqual(0, ImapMailboxRegistration.ReadSettings(source.SettingsJson).MaxPerFolder);
    }

    static ImapAccount Account() => new()
    {
        Name = "test-account",
        Host = "imap.example.invalid",
        Port = 993,
        UseSsl = true,
        User = "neutral-user",
        PasswordProtected = "protected-placeholder",
    };

    static HarvestedMail Message(string accountName)
    {
        string key = new ImapMessageLocator(accountName, "INBOX", 42, 81).Encode();
        string identity = MailSourceIdentity.Create("imap", key);
        return new HarvestedMail
        {
            EntryId = identity,
            SourceIdentity = identity,
            LegacyEntryId = "<test-record@example.invalid>",
            StoreId = "imap:" + accountName,
            FolderPath = $"imap://{accountName}/INBOX",
            FolderLeaf = "INBOX",
            Received = "2026-01-01 00:00:00",
            Sent = "2026-01-01 00:00:00",
            SenderName = "Neutral Sender",
            SenderEmail = "sender@example.invalid",
            ToRecips = "recipient@example.invalid",
            Subject = "Test Record",
            Body = "Neutralny tekst wiadomości używany do testowania indeksu",
            AttachmentProvider = "imap",
            ProviderMessageKey = key,
        };
    }
}
