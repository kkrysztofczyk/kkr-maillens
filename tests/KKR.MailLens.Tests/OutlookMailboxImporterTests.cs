using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class OutlookMailboxImporterTests
{
    const string SessionKey =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task Import_UsesExactMountedStoreAndAssignsMessagesToSource()
    {
        using var db = new TestDatabase();
        var store = new OutlookStoreInfo(
            "store-a1b2",
            "Test archive",
            @"C:\Data\test-archive.pst");
        MailboxSourceRecord source = OutlookMailboxRegistration.Register(
            db.Connection,
            store,
            maxPerFolder: 300,
            includeFolders: [" Inbox ", "Inbox", "Archive"],
            sinceUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [source.Id]);
        MailboxImportSourceRecord queued =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id).Single();
        var progress = new List<MailboxImportProgress>();
        var importer = new OutlookMailboxImporter(
            (storeId, from, maxPerFolder, onFolder, onProgress, flush, includeFolders, batchSize, cancellationToken) =>
            {
                Assert.AreEqual(store.StoreId, storeId);
                Assert.AreEqual(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), from);
                Assert.AreEqual(300, maxPerFolder);
                CollectionAssert.AreEqual(new[] { "Inbox", "Archive" }, includeFolders);
                Assert.AreEqual(500, batchSize);
                cancellationToken.ThrowIfCancellationRequested();
                onFolder("private folder name");
                onProgress?.Invoke(1, 1);
                flush([Message(store.StoreId)]);
                return 1;
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
        Assert.IsFalse(result.WasFullImport);
        Assert.AreEqual(source.Id, db.ScalarLong("SELECT mailbox_source_id FROM mails;"));
        Assert.AreEqual("listing-folders", progress[0].Phase);
        Assert.AreEqual("imported", progress[^1].Phase);
        Assert.IsFalse(progress.Any(item =>
            item.Phase.Contains("private", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void StoreInfo_RecognizesMountedDataFilesWithoutExposingPathInDisplayText()
    {
        var pst = new OutlookStoreInfo("store-a", "Test archive", @"C:\Data\archive.pst");
        var ost = new OutlookStoreInfo("store-b", "Test mailbox", @"C:\Data\mailbox.ost");
        var mailbox = new OutlookStoreInfo("store-c", "Test mailbox", null);

        Assert.AreEqual(OutlookStoreKind.Pst, pst.Kind);
        Assert.IsTrue(pst.IsPst);
        Assert.AreEqual(OutlookStoreKind.Ost, ost.Kind);
        Assert.AreEqual(OutlookStoreKind.Mailbox, mailbox.Kind);
        Assert.IsFalse(pst.ToString().Contains(@"C:\Data", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(pst.ToString().Contains(pst.StoreId, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Registration_UsesCaseInsensitiveStoreIdentityAndStringStoreKind()
    {
        using var db = new TestDatabase();
        var store = new OutlookStoreInfo("a1b2", "Test archive", @"C:\Data\archive.pst");

        MailboxSourceRecord source = OutlookMailboxRegistration.Register(db.Connection, store);

        Assert.AreEqual(source.ExternalKey, OutlookMailboxRegistration.ExternalKey("A1B2"));
        StringAssert.Contains(source.SettingsJson, @"""storeKind"":""pst""");
        Assert.AreEqual(
            OutlookStoreKind.Pst,
            OutlookMailboxRegistration.ReadSettings(source.SettingsJson).StoreKind);
    }

    [TestMethod]
    public async Task Import_HonorsCancellationBeforeOpeningOutlook()
    {
        using var db = new TestDatabase();
        var store = new OutlookStoreInfo("store-a", "Test mailbox", null);
        MailboxSourceRecord source = OutlookMailboxRegistration.Register(db.Connection, store);
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [source.Id]);
        MailboxImportSourceRecord queued =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id).Single();
        bool harvested = false;
        var importer = new OutlookMailboxImporter((_, _, _, _, _, _, _, _, _) =>
        {
            harvested = true;
            return 0;
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            importer.ImportAsync(
                new MailboxImportRequest(db.Connection, SessionKey, queued, forceFull: false),
                cancellation.Token));

        Assert.IsFalse(harvested);
    }

    static HarvestedMail Message(string storeId)
    {
        string entryId = "entry-test";
        string key = new OutlookMessageLocator(storeId, entryId).Encode();
        string identity = MailSourceIdentity.Create("outlook", key);
        return new HarvestedMail
        {
            EntryId = identity,
            SourceIdentity = identity,
            LegacyEntryId = entryId,
            StoreId = storeId,
            FolderPath = "Inbox",
            FolderLeaf = "Inbox",
            Received = "2026-01-01 00:00:00",
            Sent = "2026-01-01 00:00:00",
            SenderName = "Neutral Sender",
            SenderEmail = "sender@example.invalid",
            ToRecips = "recipient@example.invalid",
            Subject = "Test Record",
            Body = "Neutralny tekst wiadomości używany do testowania indeksu",
            AttachmentProvider = "outlook",
            ProviderMessageKey = key,
        };
    }
}
