using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class MailboxImportRunRepositoryTests
{
    [TestMethod]
    public void Create_SnapshotsSourcesInRequestedOrder()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord first = Source(db, MailboxProvider.Imap, "imap-a", "Mailbox A");
        MailboxSourceRecord second = Source(db, MailboxProvider.Gmail, "sender@example.invalid", "Mailbox B");

        MailboxImportRunRecord run = MailboxImportRunRepository.Create(
            db.Connection, [second.Id, first.Id], forceFull: true);
        MailboxSourceRepository.Upsert(db.Connection, new(
            MailboxProvider.Gmail,
            "sender@example.invalid",
            "Changed after queue creation",
            "gmail-account:9",
            """{"label":"changed"}"""));

        IReadOnlyList<MailboxImportSourceRecord> queued =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id);

        Assert.AreEqual(MailboxImportRunStatus.Queued, run.Status);
        Assert.IsTrue(run.ForceFull);
        Assert.IsTrue(MailboxImportRunRepository.Find(
            db.Connection,
            run.Id)!.ForceFull);
        CollectionAssert.AreEqual(
            new[] { second.Id, first.Id },
            queued.Select(item => item.MailboxSourceId!.Value).ToArray());
        Assert.AreEqual("Mailbox B", queued[0].DisplayName);
        Assert.AreEqual("{}", queued[0].SettingsJson);
    }

    [TestMethod]
    public void StartNextSource_EnforcesSequentialExecution()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord first = Source(db, MailboxProvider.Imap, "imap-a", "Mailbox A");
        MailboxSourceRecord second = Source(db, MailboxProvider.Outlook, "store-b", "Mailbox B");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(
            db.Connection, [first.Id, second.Id]);

        Assert.IsTrue(MailboxImportRunRepository.Start(db.Connection, run.Id));
        MailboxImportSourceRecord startedFirst =
            MailboxImportRunRepository.StartNextSource(db.Connection, run.Id)!;
        Assert.IsNull(MailboxImportRunRepository.StartNextSource(db.Connection, run.Id));

        Assert.IsTrue(MailboxImportRunRepository.SaveProgress(
            db.Connection,
            startedFirst.Id,
            new("messages", 12, 30, Inserted: 10, Updated: 2)));
        Assert.IsTrue(MailboxImportRunRepository.MarkSourceImported(
            db.Connection,
            startedFirst.Id,
            new("imported", 30, 30, Inserted: 28, Updated: 2)));

        MailboxImportSourceRecord startedSecond =
            MailboxImportRunRepository.StartNextSource(db.Connection, run.Id)!;
        Assert.AreEqual(second.Id, startedSecond.MailboxSourceId);
    }

    [TestMethod]
    public void FailedSource_DoesNotBlockNextSourceAndProducesCompletedWithErrors()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord first = Source(db, MailboxProvider.Imap, "imap-a", "Mailbox A");
        MailboxSourceRecord second = Source(db, MailboxProvider.Outlook, "store-b", "Mailbox B");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(
            db.Connection, [first.Id, second.Id]);
        MailboxImportRunRepository.Start(db.Connection, run.Id);

        MailboxImportSourceRecord failed =
            MailboxImportRunRepository.StartNextSource(db.Connection, run.Id)!;
        Assert.IsTrue(MailboxImportRunRepository.MarkSourceFailed(
            db.Connection,
            failed.Id,
            new("failed", 4, 10, Inserted: 3, Errors: 1),
            "Imap.ConnectionFailed"));

        MailboxImportSourceRecord succeeded =
            MailboxImportRunRepository.StartNextSource(db.Connection, run.Id)!;
        Assert.IsTrue(MailboxImportRunRepository.MarkSourceImported(
            db.Connection,
            succeeded.Id,
            new("imported", 7, 7, Inserted: 7)));

        Assert.AreEqual(
            MailboxImportRunStatus.Processing,
            MailboxImportRunRepository.FinishImportPhase(db.Connection, run.Id));
        Assert.AreEqual(
            MailboxImportRunStatus.CompletedWithErrors,
            MailboxImportRunRepository.CompleteProcessing(db.Connection, run.Id));

        MailboxImportSourceRecord failedResult =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id)[0];
        Assert.AreEqual("Imap.ConnectionFailed", failedResult.LastErrorCode);
    }

    [TestMethod]
    public void RequestCancellation_CancelsCurrentAndRemainingSources()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord first = Source(db, MailboxProvider.Imap, "imap-a", "Mailbox A");
        MailboxSourceRecord second = Source(db, MailboxProvider.Outlook, "store-b", "Mailbox B");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(
            db.Connection, [first.Id, second.Id]);
        MailboxImportRunRepository.Start(db.Connection, run.Id);
        _ = MailboxImportRunRepository.StartNextSource(db.Connection, run.Id);

        Assert.IsTrue(MailboxImportRunRepository.RequestCancellation(db.Connection, run.Id));
        Assert.AreEqual(
            MailboxImportRunStatus.Cancelled,
            MailboxImportRunRepository.FinishImportPhase(db.Connection, run.Id));

        MailboxImportRunRecord stored = MailboxImportRunRepository.Find(db.Connection, run.Id)!;
        Assert.IsTrue(stored.CancelRequested);
        Assert.AreEqual(MailboxImportRunStatus.Cancelled, stored.Status);
        Assert.IsTrue(MailboxImportRunRepository.ListSources(db.Connection, run.Id)
            .All(source => source.Status == MailboxImportSourceStatus.Cancelled));
    }

    [TestMethod]
    public void RecoverInterruptedImport_RequeuesOnlyInterruptedSource()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord first = Source(db, MailboxProvider.Imap, "imap-a", "Mailbox A");
        MailboxSourceRecord second = Source(db, MailboxProvider.Outlook, "store-b", "Mailbox B");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(
            db.Connection, [first.Id, second.Id]);
        MailboxImportRunRepository.Start(db.Connection, run.Id);
        MailboxImportSourceRecord interrupted =
            MailboxImportRunRepository.StartNextSource(db.Connection, run.Id)!;
        MailboxImportRunRepository.SaveProgress(
            db.Connection, interrupted.Id, new("messages", 5, 10, Inserted: 5));

        Assert.IsTrue(MailboxImportRunRepository.RecoverInterruptedImport(db.Connection, run.Id));

        Assert.AreEqual(
            MailboxImportRunStatus.Queued,
            MailboxImportRunRepository.Find(db.Connection, run.Id)!.Status);
        IReadOnlyList<MailboxImportSourceRecord> sources =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id);
        Assert.IsTrue(sources.All(source => source.Status == MailboxImportSourceStatus.Queued));
        Assert.AreEqual(5, sources[0].Processed);
    }

    [TestMethod]
    public void AppendSource_AddsMailboxToTailWhileImportIsRunning()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord first = Source(db, MailboxProvider.Imap, "imap-a", "Mailbox A");
        MailboxSourceRecord appended = Source(db, MailboxProvider.Outlook, "store-b", "Mailbox B");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [first.Id]);
        MailboxImportRunRepository.Start(db.Connection, run.Id);
        _ = MailboxImportRunRepository.StartNextSource(db.Connection, run.Id);

        MailboxImportSourceRecord added =
            MailboxImportRunRepository.AppendSource(db.Connection, run.Id, appended.Id);

        Assert.AreEqual(1, added.QueuePosition);
        Assert.AreEqual(appended.Id, added.MailboxSourceId);
        Assert.AreEqual(MailboxImportSourceStatus.Queued, added.Status);
    }

    [TestMethod]
    public void Create_RejectsSecondActiveRun()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord first = Source(db, MailboxProvider.Imap, "imap-a", "Mailbox A");
        MailboxSourceRecord second = Source(db, MailboxProvider.Outlook, "store-b", "Mailbox B");
        _ = MailboxImportRunRepository.Create(db.Connection, [first.Id]);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            MailboxImportRunRepository.Create(db.Connection, [second.Id]));
    }

    [TestMethod]
    public void FailureCode_RejectsMessageLikeText()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = Source(db, MailboxProvider.Imap, "imap-a", "Mailbox A");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [source.Id]);
        MailboxImportRunRepository.Start(db.Connection, run.Id);
        MailboxImportSourceRecord started =
            MailboxImportRunRepository.StartNextSource(db.Connection, run.Id)!;

        Assert.ThrowsExactly<ArgumentException>(() =>
            MailboxImportRunRepository.MarkSourceFailed(
                db.Connection,
                started.Id,
                new("failed", 0, Errors: 1),
                "Connection failed for user@example.invalid"));
    }

    [TestMethod]
    public void DeleteSource_PreservesQueuedSnapshot()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = Source(db, MailboxProvider.Imap, "imap-a", "Mailbox A");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [source.Id]);

        MailboxSourceRepository.Delete(db.Connection, source.Id);

        MailboxImportSourceRecord queued =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id).Single();
        Assert.IsNull(queued.MailboxSourceId);
        Assert.AreEqual("imap-a", queued.ExternalKey);
        Assert.AreEqual("Mailbox A", queued.DisplayName);
    }

    static MailboxSourceRecord Source(
        TestDatabase db,
        MailboxProvider provider,
        string externalKey,
        string displayName)
        => MailboxSourceRepository.Upsert(
            db.Connection,
            new(provider, externalKey, displayName));
}
