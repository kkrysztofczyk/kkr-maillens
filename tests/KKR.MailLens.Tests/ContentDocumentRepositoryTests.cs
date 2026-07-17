using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class ContentDocumentRepositoryTests
{
    [TestMethod]
    public void SaveExtraction_PersistsSegmentsAndInvalidatesChangedSource()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        string firstHash = new('a', 64);
        long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
            db.Connection, attachmentId, firstHash, "text/plain");
        ExtractionResult extraction = new ContentExtractionDispatcher().Extract(
            "record.txt", "text/plain", Encoding.UTF8.GetBytes("Neutralny tekst wiadomości"));

        ContentDocumentRepository.SaveExtraction(db.Connection, documentId, extraction, "plain-text", "1");

        Assert.AreEqual("completed", db.ScalarText("SELECT status FROM content_documents;"));
        Assert.AreEqual("extracted", db.ScalarText("SELECT processing_status FROM mail_attachments;"));
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM content_segments;"));
        Assert.AreEqual("Neutralny tekst wiadomości", db.ScalarText("SELECT clean_text FROM content_segments;"));

        long unchangedId = ContentDocumentRepository.EnsureAttachmentDocument(
            db.Connection, attachmentId, firstHash, "text/plain");
        Assert.AreEqual(documentId, unchangedId);
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM content_segments;"));

        long changedId = ContentDocumentRepository.EnsureAttachmentDocument(
            db.Connection, attachmentId, new string('b', 64), "text/plain");
        Assert.AreEqual(documentId, changedId);
        Assert.AreEqual("pending", db.ScalarText("SELECT status FROM content_documents;"));
        Assert.AreEqual("pending", db.ScalarText("SELECT processing_status FROM mail_attachments;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM content_segments;"));
    }

    [TestMethod]
    public void SaveExtraction_EmptyPdfMarksDocumentForOcr()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
            db.Connection, attachmentId, new string('c', 64), "application/pdf");
        var result = new ExtractionResult("application/pdf", "", "", false, []);

        ContentDocumentRepository.SaveExtraction(db.Connection, documentId, result, "pdf-text", "1");

        Assert.AreEqual("needs-ocr", db.ScalarText("SELECT status FROM content_documents;"));
        Assert.AreEqual("needs-ocr", db.ScalarText("SELECT processing_status FROM mail_attachments;"));
    }

    static long AddAttachment(TestDatabase db)
    {
        GmailAccountRecord account = db.AddAccount();
        var part = new GmailApiPart
        {
            PartId = "2",
            MimeType = "text/plain",
            Filename = "record.txt",
            AttachmentId = "attachment-1",
            Size = 64,
            Headers = [new GmailHeader("Content-Disposition", "attachment")],
        };
        GmailStoredMessage message = GmailMessageMapper.Map(
            GmailTestMessage.Create("m-content", extraParts: [part]), account.Id);
        Corpus.Upsert(db.Connection, [GmailMessageMapper.ToHarvested(message)], "2026-01-01 00:00:00");
        MailAttachmentRepository.UpsertGmail(db.Connection, 1, [message]);
        return db.ScalarLong("SELECT id FROM mail_attachments;");
    }
}
