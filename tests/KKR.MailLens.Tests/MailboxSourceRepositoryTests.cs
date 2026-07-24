using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class MailboxSourceRepositoryTests
{
    [TestMethod]
    public void Upsert_GmailNormalizesIdentityAndUpdatesExistingSource()
    {
        using var db = new TestDatabase();

        MailboxSourceRecord first = MailboxSourceRepository.Upsert(db.Connection, new(
            MailboxProvider.Gmail,
            " Sender@Example.Invalid ",
            "Primary mailbox",
            "gmail-account:1"));
        MailboxSourceRecord updated = MailboxSourceRepository.Upsert(db.Connection, new(
            MailboxProvider.Gmail,
            "sender@example.invalid",
            "Updated mailbox",
            "gmail-account:1",
            """{"includeSpam":false}"""));

        Assert.AreEqual(first.Id, updated.Id);
        Assert.AreEqual("sender@example.invalid", updated.ExternalKey);
        Assert.AreEqual("Updated mailbox", updated.DisplayName);
        Assert.AreEqual("""{"includeSpam":false}""", updated.SettingsJson);
        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM mailbox_sources;"));
    }

    [TestMethod]
    public void List_UsesExplicitSourceOrder()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord first = MailboxSourceRepository.Upsert(db.Connection,
            new(MailboxProvider.Imap, "host.invalid:993:user-a", "Mailbox A"));
        MailboxSourceRecord second = MailboxSourceRepository.Upsert(db.Connection,
            new(MailboxProvider.Outlook, "store-b", "Mailbox B"));

        Assert.IsTrue(MailboxSourceRepository.SetSortOrder(db.Connection, second.Id, 0));
        Assert.IsTrue(MailboxSourceRepository.SetSortOrder(db.Connection, first.Id, 10));

        IReadOnlyList<MailboxSourceRecord> sources = MailboxSourceRepository.List(db.Connection);

        CollectionAssert.AreEqual(
            new[] { second.Id, first.Id },
            sources.Select(source => source.Id).ToArray());
    }

    [TestMethod]
    public void Delete_DisconnectsSourceWithoutDeletingImportedMail()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = MailboxSourceRepository.Upsert(db.Connection,
            new(MailboxProvider.Imap, "host.invalid:993:user-a", "Mailbox A"));
        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO mails(entry_id,store_id,mailbox_source_id,subject)
                VALUES('imap:test-record','imap:mailbox-a',$source,'Test Record');
                """;
            command.Parameters.AddWithValue("$source", source.Id);
            command.ExecuteNonQuery();
        }

        Assert.IsTrue(MailboxSourceRepository.Delete(db.Connection, source.Id));

        Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM mails;"));
        Assert.AreEqual(1, db.ScalarLong(
            "SELECT count(*) FROM mails WHERE mailbox_source_id IS NULL;"));
    }

    [TestMethod]
    public void Upsert_RejectsSettingsThatAreNotAJsonObject()
    {
        using var db = new TestDatabase();

        Assert.ThrowsExactly<ArgumentException>(() =>
            MailboxSourceRepository.Upsert(db.Connection,
                new(MailboxProvider.Imap, "host.invalid:993:user-a", "Mailbox A", SettingsJson: "[]")));
    }

    [TestMethod]
    public void SetEnabled_ChangesOnlySelectedSource()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = MailboxSourceRepository.Upsert(db.Connection,
            new(MailboxProvider.Outlook, "store-a", "Mailbox A"));

        Assert.IsTrue(MailboxSourceRepository.SetEnabled(db.Connection, source.Id, false));
        Assert.IsFalse(MailboxSourceRepository.Find(db.Connection, source.Id)!.Enabled);
        Assert.IsFalse(MailboxSourceRepository.SetEnabled(db.Connection, source.Id + 1, false));
    }
}
