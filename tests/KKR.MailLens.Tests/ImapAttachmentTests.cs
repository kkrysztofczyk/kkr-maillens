using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class ImapAttachmentTests
{
    [TestMethod]
    public void Locators_RoundTripAndRejectInvalidValues()
    {
        var message = new ImapMessageLocator("test-account", "INBOX", 42, 81);
        var part = new ImapPartLocator("2.1", "base64");

        Assert.AreEqual(message, ImapMessageLocator.Decode(message.Encode()));
        Assert.AreEqual(part, ImapPartLocator.Decode(part.Encode()));
        Assert.Throws<InvalidDataException>(() => ImapMessageLocator.Decode("{}"));
        Assert.Throws<InvalidDataException>(() => ImapPartLocator.Decode("not-json"));
    }

    [TestMethod]
    public async Task BoundedMemoryStream_RejectsContentBeyondLimitForSyncAndAsyncWrites()
    {
        using var stream = new BoundedMemoryStream(5);
        stream.Write("123"u8);
        await stream.WriteAsync("45"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => stream.WriteByte((byte)'6'));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await stream.WriteAsync("67"u8.ToArray()));
        CollectionAssert.AreEqual("12345"u8.ToArray(), stream.ToArray());
    }

    [TestMethod]
    public void MessageIdCandidate_AcceptsOnlyRealMessageIds()
    {
        Assert.AreEqual("abc@example.invalid",
            ImapAttachmentDownloader.MessageIdCandidate(Item("abc@example.invalid")));
        Assert.AreEqual("abc@example.invalid",
            ImapAttachmentDownloader.MessageIdCandidate(Item(" <abc@example.invalid> ")));
        // syntetyczne entry_id (brak Message-Id) i hasze source-identity nie nadaja sie do SEARCH
        Assert.IsNull(ImapAttachmentDownloader.MessageIdCandidate(Item("imap:test-account:INBOX:81")));
        Assert.IsNull(ImapAttachmentDownloader.MessageIdCandidate(Item("imap:" + new string('a', 64))));
        Assert.IsNull(ImapAttachmentDownloader.MessageIdCandidate(Item("")));

        static MailAttachmentRepository.Item Item(string mailEntryId) => new(1, mailEntryId, "imap",
            new ImapMessageLocator("test-account", "INBOX", 42, 81).Encode(),
            "2", new ImapPartLocator("2", "base64").Encode(), "record.txt", "text/plain", 64, "",
            false, null, null);
    }

    [TestMethod]
    public void CorpusUpsert_QueuesImapAttachmentAndPreservesUnchangedContent()
    {
        using var db = new TestDatabase();
        HarvestedMail first = Message(size: 128, filename: "record.txt");

        Corpus.Upsert(db.Connection, [first], "2026-01-01 00:00:00");

        Assert.AreEqual("imap", db.ScalarText("SELECT provider FROM mail_attachments;"));
        Assert.AreEqual("record.txt", db.ScalarText("SELECT filename FROM mail_attachments;"));
        Assert.AreEqual("metadata-only", db.ScalarText("SELECT download_status FROM mail_attachments;"));
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM processing_jobs WHERE job_type='download';"));

        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE mail_attachments SET download_status='downloaded',processing_status='extracted',blob_id=9;
                UPDATE processing_jobs SET status='completed',completed_at='2026-01-01T00:00:00Z';
                """;
            command.ExecuteNonQuery();
        }
        HarvestedMail unchanged = Message(size: 128, filename: "updated-record.txt");
        Corpus.Upsert(db.Connection, [unchanged], "2026-01-02 00:00:00");

        Assert.AreEqual("updated-record.txt", db.ScalarText("SELECT filename FROM mail_attachments;"));
        Assert.AreEqual("downloaded", db.ScalarText("SELECT download_status FROM mail_attachments;"));
        Assert.AreEqual(9, db.ScalarLong("SELECT blob_id FROM mail_attachments;"));
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM processing_jobs;"));

        HarvestedMail changed = Message(size: 256, filename: "updated-record.txt");
        Corpus.Upsert(db.Connection, [changed], "2026-01-03 00:00:00");

        Assert.AreEqual("metadata-only", db.ScalarText("SELECT download_status FROM mail_attachments;"));
        Assert.AreEqual("pending", db.ScalarText("SELECT processing_status FROM mail_attachments;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM mail_attachments WHERE blob_id IS NOT NULL;"));
        Assert.AreEqual(2, db.ScalarLong("SELECT count(*) FROM processing_jobs WHERE job_type='download';"));
    }

    [TestMethod]
    public void CorpusUpsert_MarksMissingImapAttachmentAsDeleted()
    {
        using var db = new TestDatabase();
        Corpus.Upsert(db.Connection, [Message(128, "record.txt")], "2026-01-01 00:00:00");
        HarvestedMail withoutAttachments = Message(128, "record.txt");
        withoutAttachments.Attachments = [];
        withoutAttachments.HasAttachments = false;
        withoutAttachments.AttachmentNames = "";

        Corpus.Upsert(db.Connection, [withoutAttachments], "2026-01-02 00:00:00");

        Assert.AreEqual(1, db.ScalarLong("SELECT is_deleted FROM mail_attachments;"));
    }

    static HarvestedMail Message(long size, string filename)
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
            Body = Encoding.UTF8.GetString("Neutralny tekst wiadomości"u8),
            AttachmentProvider = "imap",
            ProviderMessageKey = messageKey,
            HasAttachments = true,
            AttachmentNames = filename,
            Attachments =
            [
                new HarvestedAttachment("2", partKey, filename, "text/plain", size, "", false),
            ],
        };
    }
}
