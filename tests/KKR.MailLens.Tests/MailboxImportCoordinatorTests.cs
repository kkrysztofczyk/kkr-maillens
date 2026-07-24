using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class MailboxImportCoordinatorTests
{
    const string SessionKey =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task Run_ImportsFourSourcesStrictlySequentially()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord gmailOne = Source(db, MailboxProvider.Gmail, "one@example.invalid");
        MailboxSourceRecord imap = Source(db, MailboxProvider.Imap, "imap-a");
        MailboxSourceRecord outlook = Source(db, MailboxProvider.Outlook, "store-a");
        MailboxSourceRecord gmailTwo = Source(db, MailboxProvider.Gmail, "two@example.invalid");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(
            db.Connection,
            [gmailOne.Id, imap.Id, outlook.Id, gmailTwo.Id]);

        var order = new List<string>();
        int active = 0;
        int maximumActive = 0;
        async Task<MailboxImportResult> Import(
            MailboxImportRequest request,
            CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            lock (order)
                order.Add(request.Source.ExternalKey);
            request.Progress?.Report(new MailboxImportProgress("importing", 1, 1));
            await Task.Delay(20, cancellationToken);
            Interlocked.Decrement(ref active);
            return new MailboxImportResult(1, 1, 0, 0, 0, true);
        }

        using var coordinator = Coordinator(db,
            new FakeImporter(MailboxProvider.Gmail, Import),
            new FakeImporter(MailboxProvider.Imap, Import),
            new FakeImporter(MailboxProvider.Outlook, Import));

        MailboxImportRunRecord result = await coordinator.RunAsync(
            SessionKey,
            run.Id,
            forceFull: false);

        Assert.AreEqual(MailboxImportRunStatus.Processing, result.Status);
        Assert.AreEqual(1, maximumActive);
        CollectionAssert.AreEqual(
            new[]
            {
                gmailOne.ExternalKey,
                imap.ExternalKey,
                outlook.ExternalKey,
                gmailTwo.ExternalKey,
            },
            order);
        Assert.IsTrue(MailboxImportRunRepository.ListSources(db.Connection, run.Id)
            .All(source => source.Status == MailboxImportSourceStatus.Imported));
    }

    [TestMethod]
    public async Task Run_FailureUsesSafeCodeAndContinuesWithNextSource()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord gmail = Source(db, MailboxProvider.Gmail, "one@example.invalid");
        MailboxSourceRecord imap = Source(db, MailboxProvider.Imap, "imap-a");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(
            db.Connection,
            [gmail.Id, imap.Id]);
        bool imapImported = false;
        using var coordinator = Coordinator(db,
            new FakeImporter(MailboxProvider.Gmail, (_, _) =>
                throw new InvalidOperationException(
                    "Sensitive runtime detail sender@example.invalid")),
            new FakeImporter(MailboxProvider.Imap, (_, _) =>
            {
                imapImported = true;
                return Task.FromResult(new MailboxImportResult(2, 2, 0, 0, 0, true));
            }));

        MailboxImportRunRecord result = await coordinator.RunAsync(
            SessionKey,
            run.Id,
            forceFull: false);

        IReadOnlyList<MailboxImportSourceRecord> sources =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id);
        Assert.AreEqual(MailboxImportRunStatus.Processing, result.Status);
        Assert.AreEqual(MailboxImportSourceStatus.Failed, sources[0].Status);
        Assert.AreEqual("InvalidOperationException", sources[0].LastErrorCode);
        Assert.IsFalse(sources[0].LastErrorCode!.Contains("sender", StringComparison.Ordinal));
        Assert.AreEqual(MailboxImportSourceStatus.Imported, sources[1].Status);
        Assert.IsTrue(imapImported);

        Assert.AreEqual(
            MailboxImportRunStatus.CompletedWithErrors,
            MailboxImportRunRepository.CompleteProcessing(db.Connection, run.Id));
    }

    [TestMethod]
    public async Task RequestCancellation_StopsCurrentAndCancelsRemainingSources()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord first = Source(db, MailboxProvider.Gmail, "one@example.invalid");
        MailboxSourceRecord second = Source(db, MailboxProvider.Imap, "imap-a");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(
            db.Connection,
            [first.Id, second.Id]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = Coordinator(db,
            new FakeImporter(MailboxProvider.Gmail, async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new AssertFailedException("Anulowany importer nie powinien kontynuować.");
            }),
            new FakeImporter(MailboxProvider.Imap, (_, _) =>
                throw new AssertFailedException("Następna skrzynka nie powinna wystartować.")));

        Task<MailboxImportRunRecord> running = coordinator.RunAsync(
            SessionKey,
            run.Id,
            forceFull: false);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(coordinator.RequestCancellation(SessionKey, run.Id));
        MailboxImportRunRecord result = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(MailboxImportRunStatus.Cancelled, result.Status);
        Assert.IsTrue(MailboxImportRunRepository.ListSources(db.Connection, run.Id)
            .All(source => source.Status == MailboxImportSourceStatus.Cancelled));
    }

    [TestMethod]
    public async Task Run_PersistsProgressAndReportsLiveUpdates()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = Source(db, MailboxProvider.Gmail, "one@example.invalid");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [source.Id]);
        var updates = new List<MailboxImportCoordinatorUpdate>();
        using var coordinator = Coordinator(db,
            new FakeImporter(MailboxProvider.Gmail, (request, _) =>
            {
                request.Progress?.Report(new MailboxImportProgress(
                    "importing",
                    8,
                    10,
                    Inserted: 6,
                    Updated: 2,
                    Errors: 1));
                return Task.FromResult(new MailboxImportResult(10, 7, 2, 0, 1, true));
            }));

        MailboxImportRunRecord result = await coordinator.RunAsync(
            SessionKey,
            run.Id,
            forceFull: false,
            new CallbackProgress<MailboxImportCoordinatorUpdate>(updates.Add));

        MailboxImportSourceRecord stored =
            MailboxImportRunRepository.ListSources(db.Connection, run.Id).Single();
        Assert.AreEqual(MailboxImportRunStatus.Processing, result.Status);
        Assert.AreEqual(10, stored.Processed);
        Assert.AreEqual(7, stored.Inserted);
        Assert.AreEqual(1, stored.Errors);
        Assert.IsTrue(updates.Any(update =>
            update.Progress?.Phase == "importing"
            && update.Progress.Processed == 8));

        Assert.AreEqual(
            MailboxImportRunStatus.CompletedWithErrors,
            MailboxImportRunRepository.CompleteProcessing(db.Connection, run.Id));
    }

    [TestMethod]
    public async Task Run_QueuedCancellationFinishesWithoutStartingImporter()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = Source(db, MailboxProvider.Gmail, "one@example.invalid");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [source.Id]);
        MailboxImportRunRepository.RequestCancellation(db.Connection, run.Id);
        bool imported = false;
        using var coordinator = Coordinator(db,
            new FakeImporter(MailboxProvider.Gmail, (_, _) =>
            {
                imported = true;
                return Task.FromResult(new MailboxImportResult(0, 0, 0, 0, 0, true));
            }));

        MailboxImportRunRecord result = await coordinator.RunAsync(
            SessionKey,
            run.Id,
            forceFull: false);

        Assert.AreEqual(MailboxImportRunStatus.Cancelled, result.Status);
        Assert.IsFalse(imported);
    }

    [TestMethod]
    public async Task Run_ImportsSourceAppendedWhileCurrentSourceIsRunning()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord first = Source(db, MailboxProvider.Gmail, "one@example.invalid");
        MailboxSourceRecord appended = Source(db, MailboxProvider.Imap, "imap-a");
        MailboxImportRunRecord run = MailboxImportRunRepository.Create(db.Connection, [first.Id]);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var imported = new List<MailboxProvider>();
        using var coordinator = Coordinator(db,
            new FakeImporter(MailboxProvider.Gmail, async (_, cancellationToken) =>
            {
                imported.Add(MailboxProvider.Gmail);
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return new MailboxImportResult(1, 1, 0, 0, 0, true);
            }),
            new FakeImporter(MailboxProvider.Imap, (_, _) =>
            {
                imported.Add(MailboxProvider.Imap);
                return Task.FromResult(new MailboxImportResult(1, 1, 0, 0, 0, true));
            }));

        Task<MailboxImportRunRecord> running = coordinator.RunAsync(
            SessionKey,
            run.Id,
            forceFull: false);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        MailboxImportRunRepository.AppendSource(db.Connection, run.Id, appended.Id);
        releaseFirst.TrySetResult();

        MailboxImportRunRecord result = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(MailboxImportRunStatus.Processing, result.Status);
        CollectionAssert.AreEqual(
            new[] { MailboxProvider.Gmail, MailboxProvider.Imap },
            imported);
    }

    static MailboxImportCoordinator Coordinator(
        TestDatabase database,
        params IMailboxImporter[] importers)
        => new(importers, _ => database.OpenAnotherConnection());

    static MailboxSourceRecord Source(
        TestDatabase database,
        MailboxProvider provider,
        string externalKey)
        => MailboxSourceRepository.Upsert(
            database.Connection,
            new MailboxSourceDefinition(
                provider,
                externalKey,
                "Test mailbox",
                provider == MailboxProvider.Gmail ? "gmail-account:1" : null));

    sealed class FakeImporter(
        MailboxProvider provider,
        Func<MailboxImportRequest, CancellationToken, Task<MailboxImportResult>> import)
        : IMailboxImporter
    {
        public MailboxProvider Provider => provider;

        public Task<MailboxImportResult> ImportAsync(
            MailboxImportRequest request,
            CancellationToken cancellationToken)
            => import(request, cancellationToken);
    }
}
