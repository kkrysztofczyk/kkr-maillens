using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class MailSourceIdentityTests
{
    [TestMethod]
    public void ImapIdentity_IsScopedToProviderLocator()
    {
        string first = Identity("imap", new ImapMessageLocator("account-a", "INBOX", 10, 7).Encode());
        string repeated = Identity("imap", new ImapMessageLocator("account-a", "INBOX", 10, 7).Encode());
        string otherAccount = Identity("imap", new ImapMessageLocator("account-b", "INBOX", 10, 7).Encode());
        string otherFolder = Identity("imap", new ImapMessageLocator("account-a", "Archive", 10, 7).Encode());

        Assert.AreEqual(first, repeated);
        Assert.AreNotEqual(first, otherAccount);
        Assert.AreNotEqual(first, otherFolder);
        StringAssert.StartsWith(first, "imap:");
    }

    [TestMethod]
    public void CorpusUpsert_DoesNotCollapseSameImapMessageIdAcrossAccounts()
    {
        using var db = new TestDatabase();
        const string legacyMessageId = "<shared@example.invalid>";
        HarvestedMail first = ImapMessage("account-a", legacyMessageId, 7);
        HarvestedMail second = ImapMessage("account-b", legacyMessageId, 7);

        Corpus.Upsert(db.Connection, [first, second], "2026-01-01 00:00:00");

        Assert.AreEqual(2, db.ScalarLong("SELECT count(*) FROM mails;"));
        Assert.AreEqual(2, db.ScalarLong("SELECT count(DISTINCT source_identity) FROM mails;"));
    }

    [TestMethod]
    public void CorpusUpsert_DoesNotCollapseSameOutlookEntryIdAcrossStores()
    {
        using var db = new TestDatabase();
        HarvestedMail first = OutlookMessage("store-a", "entry-shared");
        HarvestedMail second = OutlookMessage("store-b", "entry-shared");

        Corpus.Upsert(db.Connection, [first, second], "2026-01-01 00:00:00");

        Assert.AreEqual(2, db.ScalarLong("SELECT count(*) FROM mails;"));
        Assert.AreEqual(2, db.ScalarLong("SELECT count(DISTINCT source_identity) FROM mails;"));
    }

    [TestMethod]
    public void CorpusUpsert_AdoptsLegacyRowAndKeepsAttachmentForeignKey()
    {
        using var db = new TestDatabase();
        const string legacyMessageId = "<legacy@example.invalid>";
        HarvestedMail legacy = ImapMessage("account-a", legacyMessageId, 7);
        legacy.EntryId = legacyMessageId;
        legacy.SourceIdentity = "";
        legacy.LegacyEntryId = "";
        legacy.Subject = "Legacy record";
        Corpus.Upsert(db.Connection, [legacy], "2026-01-01 00:00:00");

        HarvestedMail current = ImapMessage("account-a", legacyMessageId, 7);
        current.Subject = "Test Record";
        Corpus.Upsert(db.Connection, [current], "2026-01-02 00:00:00");

        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM mails;"));
        Assert.AreEqual(legacyMessageId, db.ScalarText("SELECT entry_id FROM mails;"));
        Assert.AreEqual(current.SourceIdentity, db.ScalarText("SELECT source_identity FROM mails;"));
        Assert.AreEqual("Test Record", db.ScalarText("SELECT subject FROM mails;"));
        Assert.AreEqual(legacyMessageId, db.ScalarText("SELECT mail_entry_id FROM mail_attachments;"));
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM mail_attachments;"));
    }

    static HarvestedMail ImapMessage(string account, string legacyMessageId, uint uid)
    {
        string messageKey = new ImapMessageLocator(account, "INBOX", 10, uid).Encode();
        string sourceIdentity = Identity("imap", messageKey);
        return Message(sourceIdentity, legacyMessageId, $"imap:{account}", $"imap://{account}/INBOX",
            "imap", messageKey);
    }

    static HarvestedMail OutlookMessage(string storeId, string entryId)
    {
        string messageKey = new OutlookMessageLocator(storeId, entryId).Encode();
        string sourceIdentity = Identity("outlook", messageKey);
        return Message(sourceIdentity, entryId, storeId, "Inbox", "outlook", messageKey);
    }

    static HarvestedMail Message(string sourceIdentity, string legacyEntryId, string storeId,
        string folderPath, string provider, string providerMessageKey) => new()
    {
        EntryId = sourceIdentity,
        SourceIdentity = sourceIdentity,
        LegacyEntryId = legacyEntryId,
        StoreId = storeId,
        FolderPath = folderPath,
        FolderLeaf = "Inbox",
        Received = "2026-01-01 00:00:00",
        Sent = "2026-01-01 00:00:00",
        SenderName = "Neutral Sender",
        SenderEmail = "sender@example.invalid",
        ToRecips = "recipient@example.invalid",
        Subject = "Test Record",
        Body = "Neutralny tekst wiadomości używany do testowania indeksu",
        AttachmentProvider = provider,
        ProviderMessageKey = providerMessageKey,
        HasAttachments = true,
        AttachmentNames = "record.txt",
        Attachments = [new HarvestedAttachment("1", "1", "record.txt", "text/plain", 64, "", false)],
    };

    static string Identity(string provider, string locator) => MailSourceIdentity.Create(provider, locator);
}
