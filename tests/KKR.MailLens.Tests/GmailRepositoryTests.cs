using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class GmailRepositoryTests
{
    [TestMethod]
    public void Account_SaveAndLookup_SupportsMultipleAccounts()
    {
        using var db = new TestDatabase();
        GmailAccountRecord first = db.AddAccount("sender@example.invalid");
        GmailAccountRecord second = db.AddAccount("recipient@example.invalid");

        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(first.Id, GmailRepository.FindAccount(db.Connection, "sender@example.invalid")?.Id);
        Assert.AreEqual(second.Id, GmailRepository.FindAccount(db.Connection, second.Id)?.Id);
        Assert.HasCount(2, GmailRepository.ListAccounts(db.Connection));
    }

    [TestMethod]
    public void Message_ReimportUpdatesWithoutDuplicates_AndReplacesLabels()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        GmailRepository.UpsertLabels(db.Connection, account.Id,
        [
            new GmailApiLabel("INBOX", "INBOX", "system"),
            new GmailApiLabel("UNREAD", "UNREAD", "system"),
            new GmailApiLabel("Label_Test", "Test Label", "user"),
        ]);

        GmailStoredMessage first = GmailMessageMapper.Map(
            GmailTestMessage.Create("m1", labels: ["INBOX", "UNREAD"]), account.Id);
        GmailSaveBatchResult inserted = GmailRepository.SaveMessages(db.Connection, 1, [first]);

        GmailStoredMessage changed = GmailMessageMapper.Map(
            GmailTestMessage.Create("m1", subject: "Updated Record", labels: ["Label_Test"]), account.Id);
        GmailSaveBatchResult updated = GmailRepository.SaveMessages(db.Connection, 1, [changed]);

        Assert.AreEqual(1, inserted.Inserted);
        Assert.AreEqual(1, updated.Updated);
        Assert.AreEqual(1, GmailRepository.MessageCount(db.Connection, account.Id));
        Assert.AreEqual("Updated Record", db.ScalarText("SELECT subject FROM messages;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT is_unread FROM messages;"));
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM message_labels;"));
        Assert.AreEqual("Label_Test", db.ScalarText("SELECT gmail_label_id FROM labels JOIN message_labels ON labels.id=message_labels.label_id;"));
    }

    [TestMethod]
    public void AttachmentMetadata_IsSavedWithoutBinaryContent()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        var attachment = new GmailApiPart
        {
            MimeType = "application/octet-stream",
            Filename = "record.bin",
            AttachmentId = "attachment-1",
            Size = 321,
        };
        GmailStoredMessage message = GmailMessageMapper.Map(GmailTestMessage.Create("m1", extraParts: [attachment]), account.Id);
        GmailRepository.SaveMessages(db.Connection, 1, [message]);

        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM attachments;"));
        Assert.AreEqual("metadata-only", db.ScalarText("SELECT download_status FROM attachments;"));
        Assert.AreEqual("not-indexed", db.ScalarText("SELECT index_status FROM attachments;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM attachments WHERE local_path IS NOT NULL OR extracted_text IS NOT NULL;"));
    }

    [TestMethod]
    public void Attachment_ReimportPreservesProcessingState_AndMarksMissing()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        var originalPart = new GmailApiPart
        {
            PartId = "2",
            MimeType = "application/pdf",
            Filename = "record.pdf",
            AttachmentId = "attachment-1",
            Size = 321,
        };
        GmailStoredMessage original = GmailMessageMapper.Map(
            GmailTestMessage.Create("m1", extraParts: [originalPart]), account.Id);
        GmailRepository.SaveMessages(db.Connection, 1, [original]);

        using (var processed = db.Connection.CreateCommand())
        {
            processed.CommandText = """
                UPDATE attachments
                SET download_status='downloaded', local_path='encrypted/blob-1',
                    extracted_text='Neutralny tekst', index_status='indexed', error_message='retained';
                """;
            processed.ExecuteNonQuery();
        }

        var updatedPart = new GmailApiPart
        {
            PartId = "2",
            MimeType = "application/pdf",
            Filename = "updated-record.pdf",
            AttachmentId = "attachment-1",
            Size = 654,
        };
        GmailStoredMessage updated = GmailMessageMapper.Map(
            GmailTestMessage.Create("m1", extraParts: [updatedPart]), account.Id);
        GmailRepository.SaveMessages(db.Connection, 2, [updated]);

        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM attachments;"));
        Assert.AreEqual("updated-record.pdf", db.ScalarText("SELECT filename FROM attachments;"));
        Assert.AreEqual(654, db.ScalarLong("SELECT size_bytes FROM attachments;"));
        Assert.AreEqual("downloaded", db.ScalarText("SELECT download_status FROM attachments;"));
        Assert.AreEqual("encrypted/blob-1", db.ScalarText("SELECT local_path FROM attachments;"));
        Assert.AreEqual("Neutralny tekst", db.ScalarText("SELECT extracted_text FROM attachments;"));
        Assert.AreEqual("indexed", db.ScalarText("SELECT index_status FROM attachments;"));
        Assert.AreEqual("retained", db.ScalarText("SELECT error_message FROM attachments;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT is_deleted FROM attachments;"));
        Assert.AreEqual(2, db.ScalarLong("SELECT last_seen_generation FROM attachments;"));

        GmailStoredMessage withoutAttachment = GmailMessageMapper.Map(GmailTestMessage.Create("m1"), account.Id);
        GmailRepository.SaveMessages(db.Connection, 3, [withoutAttachment]);

        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM attachments;"));
        Assert.AreEqual(1, db.ScalarLong("SELECT is_deleted FROM attachments;"));
        Assert.AreEqual("Neutralny tekst", db.ScalarText("SELECT extracted_text FROM attachments;"));
    }

    [TestMethod]
    public void DeleteMessage_RemovesNormalizedRowAndFtsEntry()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        GmailStoredMessage message = GmailMessageMapper.Map(GmailTestMessage.Create("m1"), account.Id);
        GmailRepository.SaveMessages(db.Connection, 1, [message]);
        Corpus.Upsert(db.Connection, [GmailMessageMapper.ToHarvested(message)], "2026-01-01 00:00:00");

        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM mails_fts WHERE mails_fts MATCH 'neutralny';"));
        Assert.AreEqual(1, GmailRepository.DeleteMessages(db.Connection, account.Id, ["m1"]));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM messages;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM mails;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM mails_fts;"));
    }

    [TestMethod]
    public void SameGmailId_InDifferentAccounts_DoesNotCollide()
    {
        using var db = new TestDatabase();
        GmailAccountRecord first = db.AddAccount("sender@example.invalid");
        GmailAccountRecord second = db.AddAccount("recipient@example.invalid");
        GmailRepository.SaveMessages(db.Connection, 1, [GmailMessageMapper.Map(GmailTestMessage.Create("same"), first.Id)]);
        GmailRepository.SaveMessages(db.Connection, 1, [GmailMessageMapper.Map(GmailTestMessage.Create("same"), second.Id)]);
        Assert.AreEqual(2, db.ScalarLong("SELECT count(*) FROM messages;"));
    }
}
