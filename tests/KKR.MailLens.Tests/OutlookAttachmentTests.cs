using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class OutlookAttachmentTests
{
    [TestMethod]
    public void Locator_RoundTripsAndRejectsMissingIdentifiers()
    {
        var locator = new OutlookMessageLocator("store-test", "entry-test");

        Assert.AreEqual(locator, OutlookMessageLocator.Decode(locator.Encode()));
        Assert.Throws<InvalidDataException>(() => OutlookMessageLocator.Decode("{}"));
        Assert.Throws<InvalidDataException>(() => new OutlookMessageLocator("", "entry-test").Encode());
    }

    [TestMethod]
    public void Workspace_RemovesPlaintextDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        string directory;
        try
        {
            var workspace = OutlookAttachmentWorkspace.Create(root);
            directory = workspace.DirectoryPath;
            File.WriteAllText(Path.Combine(directory, "attachment.txt"), "Neutralny tekst");

            workspace.Dispose();

            Assert.IsFalse(Directory.Exists(directory));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [TestMethod]
    public void Broker_RejectsOversizedMetadataBeforeStartingCom()
    {
        var item = new MailAttachmentRepository.Item(1, "entry-test", "outlook",
            new OutlookMessageLocator("store-test", "entry-test").Encode(), "1", "1", "record.txt",
            "text/plain", 11, "", false, null, null);
        using var broker = new OutlookAttachmentBroker();

        Assert.Throws<InvalidDataException>(() => broker.Download(item, maximumBytes: 10));
    }

    [TestMethod]
    public void Harvest_RejectsCancellationBeforeAccessingOutlook()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var outlook = new Outlook();

        Assert.Throws<OperationCanceledException>(() => outlook.HarvestMail(
            null, null, 1, _ => { }, null, _ => { }, cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public void StoreEnumeration_RejectsCancellationBeforeAccessingOutlook()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var outlook = new Outlook();

        Assert.Throws<OperationCanceledException>(() => outlook.ListStores(cancellation.Token));
    }

    [TestMethod]
    public void CorpusUpsert_QueuesOutlookAttachment()
    {
        using var db = new TestDatabase();
        var message = new HarvestedMail
        {
            EntryId = "entry-test",
            StoreId = "store-test",
            FolderPath = "folder-test",
            FolderLeaf = "Inbox",
            Received = "2026-01-01 00:00:00",
            Subject = "Test Record",
            Body = "Neutralny tekst wiadomości",
            SenderEmail = "sender@example.invalid",
            ToRecips = "recipient@example.invalid",
            AttachmentProvider = "outlook",
            ProviderMessageKey = new OutlookMessageLocator("store-test", "entry-test").Encode(),
            HasAttachments = true,
            AttachmentNames = "record.txt",
            Attachments = [new HarvestedAttachment("1", "1", "record.txt", "text/plain", 64, "", false)],
        };

        Corpus.Upsert(db.Connection, [message], "2026-01-01 00:00:00");

        Assert.AreEqual("outlook", db.ScalarText("SELECT provider FROM mail_attachments;"));
        Assert.AreEqual("1", db.ScalarText("SELECT part_id FROM mail_attachments;"));
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM processing_jobs WHERE job_type='download';"));
    }
}
