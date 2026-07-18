using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class MailAttachmentRepositoryTests
{
    [TestMethod]
    public void GmailUpsert_PreservesStateAndInvalidatesOnlyChangedContent()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        GmailStoredMessage first = Message(account.Id, 321);
        Corpus.Upsert(db.Connection, [GmailMessageMapper.ToHarvested(first)], "2026-01-01 00:00:00");
        MailAttachmentRepository.UpsertGmail(db.Connection, 1, [first]);
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM processing_jobs WHERE job_type='download';"));

        using (var processed = db.Connection.CreateCommand())
        {
            processed.CommandText = """
                UPDATE mail_attachments SET download_status='downloaded', processing_status='indexed',
                    blob_id=9, error_code='retained', error_message='retained';
                UPDATE processing_jobs SET status='completed',completed_at='2026-01-01T00:00:00Z';
                """;
            processed.ExecuteNonQuery();
        }

        GmailStoredMessage unchanged = Message(account.Id, 321, "updated-record.pdf");
        MailAttachmentRepository.UpsertGmail(db.Connection, 2, [unchanged]);
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM mail_attachments;"));
        Assert.AreEqual("updated-record.pdf", db.ScalarText("SELECT filename FROM mail_attachments;"));
        Assert.AreEqual("downloaded", db.ScalarText("SELECT download_status FROM mail_attachments;"));
        Assert.AreEqual("indexed", db.ScalarText("SELECT processing_status FROM mail_attachments;"));
        Assert.AreEqual(9, db.ScalarLong("SELECT blob_id FROM mail_attachments;"));
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM processing_jobs WHERE job_type='download';"));

        GmailStoredMessage changed = Message(account.Id, 654, "updated-record.pdf");
        MailAttachmentRepository.UpsertGmail(db.Connection, 3, [changed]);
        Assert.AreEqual("metadata-only", db.ScalarText("SELECT download_status FROM mail_attachments;"));
        Assert.AreEqual("pending", db.ScalarText("SELECT processing_status FROM mail_attachments;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM mail_attachments WHERE blob_id IS NOT NULL;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM mail_attachments WHERE error_code IS NOT NULL OR error_message IS NOT NULL;"));
        Assert.AreEqual(2, db.ScalarLong("SELECT count(*) FROM processing_jobs WHERE job_type='download';"));

        GmailStoredMessage missing = GmailMessageMapper.Map(GmailTestMessage.Create("m1"), account.Id);
        MailAttachmentRepository.UpsertGmail(db.Connection, 4, [missing]);
        Assert.AreEqual(1, db.ScalarLong("SELECT is_deleted FROM mail_attachments;"));
    }

    static GmailStoredMessage Message(long accountId, long size, string filename = "record.pdf")
    {
        var part = new GmailApiPart
        {
            PartId = "2",
            MimeType = "application/pdf",
            Filename = filename,
            AttachmentId = "attachment-1",
            Size = size,
            Headers = [new GmailHeader("Content-Disposition", "attachment")],
        };
        return GmailMessageMapper.Map(GmailTestMessage.Create("m1", extraParts: [part]), accountId);
    }
}
