using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class AttachmentExtractionProcessorTests
{
    [TestMethod]
    public void Process_DecryptsExtractsAndPersistsWithoutPlaintextFile()
    {
        using var db = new TestDatabase();
        string storeDirectory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            long attachmentId = AddAttachment(db);
            string key = new('D', 64);
            var store = new EncryptedBlobStore(storeDirectory, key);
            byte[] content = Encoding.UTF8.GetBytes("Neutralny tekst wiadomości używany do testowania indeksu");
            StoredBlob blob = store.Put(db.Connection, content);
            MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "text/plain");
            long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
                db.Connection, attachmentId, blob.Sha256, "text/plain");

            AttachmentExtractionProcessor.Process(db.Connection, store, attachmentId, documentId);

            Assert.AreEqual("completed", db.ScalarText("SELECT status FROM content_documents;"));
            Assert.AreEqual("extracted", db.ScalarText("SELECT processing_status FROM mail_attachments;"));
            Assert.AreEqual("Neutralny tekst wiadomości używany do testowania indeksu",
                db.ScalarText("SELECT clean_text FROM content_segments;"));
            IReadOnlyList<ContentSearchHit> hits = ContentSearch.Search(db.Connection, "neutralny tekst");
            Assert.HasCount(1, hits);
            Assert.AreEqual("record.txt", hits[0].Filename);
            Assert.AreEqual("Test Record", hits[0].Subject);
            Assert.AreEqual(1, ContentSearch.Rebuild(db.Connection));
            Assert.HasCount(1, ContentSearch.Search(db.Connection, "neutralny tekst"));
            byte[] encrypted = File.ReadAllBytes(Path.Combine(storeDirectory, blob.EncryptedPath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.IsFalse(Encoding.UTF8.GetString(encrypted).Contains("Neutralny tekst", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(storeDirectory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Process_UnsupportedBinaryIsSkippedWithoutRetryFailure()
    {
        using var db = new TestDatabase();
        string storeDirectory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            long attachmentId = AddAttachment(db);
            var store = new EncryptedBlobStore(storeDirectory, new string('D', 64));
            StoredBlob blob = store.Put(db.Connection, [0, 1, 2, 3]);
            MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "application/octet-stream");
            using (var rename = db.Connection.CreateCommand())
            {
                rename.CommandText = "UPDATE mail_attachments SET filename='record.bin' WHERE id=$id;";
                rename.Parameters.AddWithValue("$id", attachmentId);
                rename.ExecuteNonQuery();
            }
            long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
                db.Connection, attachmentId, blob.Sha256, "application/octet-stream");

            AttachmentExtractionOutcome outcome = AttachmentExtractionProcessor.Process(
                db.Connection, store, attachmentId, documentId);

            Assert.AreEqual("skipped", outcome.Status);
            Assert.AreEqual("skipped", db.ScalarText("SELECT status FROM content_documents;"));
            Assert.AreEqual("skipped", db.ScalarText("SELECT processing_status FROM mail_attachments;"));
            Assert.AreEqual("unsupported-type", db.ScalarText("SELECT error_code FROM content_documents;"));
        }
        finally
        {
            try { Directory.Delete(storeDirectory, recursive: true); } catch { }
        }
    }

    static long AddAttachment(TestDatabase db)
    {
        GmailAccountRecord account = db.AddAccount();
        var part = new GmailApiPart
        {
            PartId = "2",
            MimeType = "text/plain",
            Filename = "record.txt",
            AttachmentId = "attachment-processor",
            Size = 64,
            Headers = [new GmailHeader("Content-Disposition", "attachment")],
        };
        GmailStoredMessage message = GmailMessageMapper.Map(
            GmailTestMessage.Create("m-processor", extraParts: [part]), account.Id);
        Corpus.Upsert(db.Connection, [GmailMessageMapper.ToHarvested(message)], "2026-01-01 00:00:00");
        MailAttachmentRepository.UpsertGmail(db.Connection, 1, [message]);
        return db.ScalarLong("SELECT id FROM mail_attachments;");
    }
}
