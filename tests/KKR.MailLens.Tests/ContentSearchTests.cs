using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class ContentSearchTests
{
    [TestMethod]
    public void Rebuild_RestoresIndexFromPersistedSegmentsIdempotently()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
            db.Connection, attachmentId, new string('d', 64), "text/plain");
        ExtractionResult extraction = new ContentExtractionDispatcher().Extract(
            "record.txt", "text/plain", "Neutralny tekst wiadomości używany do testowania indeksu"u8.ToArray());
        ContentDocumentRepository.SaveExtraction(db.Connection, documentId, extraction, "plain-text", "1");
        long segmentCount = db.ScalarLong("SELECT count(*) FROM content_segments;");

        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM content_fts;";
            command.ExecuteNonQuery();
        }
        Assert.AreEqual(0, ContentSearch.Search(db.Connection, "neutralny tekst").Count);

        int rebuilt = ContentSearch.Rebuild(db.Connection);
        int rebuiltAgain = ContentSearch.Rebuild(db.Connection);

        Assert.AreEqual(segmentCount, rebuilt);
        Assert.AreEqual(segmentCount, rebuiltAgain);
        Assert.AreEqual(segmentCount, db.ScalarLong("SELECT count(*) FROM content_fts;"));
        IReadOnlyList<ContentSearchHit> hits = ContentSearch.Search(db.Connection, "neutralny tekst");
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual("record.txt", hits[0].Filename);
    }

    static long AddAttachment(TestDatabase db)
    {
        GmailAccountRecord account = db.AddAccount();
        var part = new GmailApiPart
        {
            PartId = "2",
            MimeType = "text/plain",
            Filename = "record.txt",
            AttachmentId = "attachment-search",
            Size = 64,
            Headers = [new GmailHeader("Content-Disposition", "attachment")],
        };
        GmailStoredMessage message = GmailMessageMapper.Map(
            GmailTestMessage.Create("message-search", extraParts: [part]), account.Id);
        Corpus.Upsert(db.Connection, [GmailMessageMapper.ToHarvested(message)], "2026-01-01 00:00:00");
        MailAttachmentRepository.UpsertGmail(db.Connection, 1, [message]);
        return db.ScalarLong("SELECT id FROM mail_attachments;");
    }
}
