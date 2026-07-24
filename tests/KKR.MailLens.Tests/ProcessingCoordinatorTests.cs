using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class ProcessingCoordinatorTests
{
    const string SessionKey = "test-session";

    [TestMethod]
    public async Task Run_DrainsJobsAddedByEarlierJobsAndCompletesRun()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Gmail,
            "first@example.invalid");
        long attachment = ProcessingTestData.AddAttachment(
            db.Connection,
            source.Id,
            "first");
        MailboxImportRunRecord run =
            ProcessingTestData.StartProcessingRun(db.Connection, source);
        ProcessingJobRepository.Enqueue(db.Connection, "download", attachment);
        int launches = 0;
        using var coordinator = Coordinator(db, () =>
        {
            launches++;
            return ImmediateWorker(() =>
            {
                using var workerDb = db.OpenAnotherConnection();
                ProcessingJob download = ProcessingJobRepository.LeaseNext(
                    workerDb,
                    "worker-1",
                    TimeSpan.FromMinutes(5))!;
                Assert.AreEqual("download", download.JobType);
                Assert.IsTrue(ProcessingJobRepository.Complete(
                    workerDb,
                    download.Id,
                    "worker-1"));
                ProcessingJobRepository.Enqueue(workerDb, "extract", attachment);
                ProcessingJob extract = ProcessingJobRepository.LeaseNext(
                    workerDb,
                    "worker-1",
                    TimeSpan.FromMinutes(5))!;
                Assert.AreEqual("extract", extract.JobType);
                Assert.IsTrue(ProcessingJobRepository.Complete(
                    workerDb,
                    extract.Id,
                    "worker-1"));
                return 0;
            });
        });

        ProcessingCoordinatorResult result = await coordinator.RunAsync(
            SessionKey,
            run.Id);

        Assert.AreEqual(ProcessingCoordinatorOutcome.Completed, result.Outcome);
        Assert.AreEqual(MailboxImportRunStatus.Completed, result.Run.Status);
        Assert.AreEqual(1, launches);
        Assert.AreEqual(2, result.Pipeline.Completed);
        Assert.AreEqual(0, result.Pipeline.Failed);
    }

    [TestMethod]
    public async Task Run_WaitsForRetryAndRelaunchesWorker()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Gmail,
            "first@example.invalid");
        long attachment = ProcessingTestData.AddAttachment(
            db.Connection,
            source.Id,
            "first");
        MailboxImportRunRecord run =
            ProcessingTestData.StartProcessingRun(db.Connection, source);
        ProcessingJobRepository.Enqueue(
            db.Connection,
            "extract",
            attachment,
            maxAttempts: 2);
        int launches = 0;
        using var coordinator = Coordinator(db, () =>
        {
            int launch = ++launches;
            return ImmediateWorker(() =>
            {
                using var workerDb = db.OpenAnotherConnection();
                ProcessingJob job = ProcessingJobRepository.LeaseNext(
                    workerDb,
                    $"worker-{launch}",
                    TimeSpan.FromMinutes(5))!;
                if (launch == 1)
                {
                    Assert.IsTrue(ProcessingJobRepository.Fail(
                        workerDb,
                        job.Id,
                        "worker-1",
                        "neutral-retry",
                        "Neutralny błąd tymczasowy",
                        TimeSpan.FromMilliseconds(80)));
                }
                else
                {
                    Assert.IsTrue(ProcessingJobRepository.Complete(
                        workerDb,
                        job.Id,
                        "worker-2"));
                }
                return 0;
            });
        });

        ProcessingCoordinatorResult result = await coordinator.RunAsync(
            SessionKey,
            run.Id);

        Assert.AreEqual(ProcessingCoordinatorOutcome.Completed, result.Outcome);
        Assert.AreEqual(2, launches);
        Assert.AreEqual(1, result.Pipeline.Completed);
    }

    [TestMethod]
    public async Task Run_CompletesWithErrorsWhenOnlyFailedJobsRemain()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Gmail,
            "first@example.invalid");
        long attachment = ProcessingTestData.AddAttachment(
            db.Connection,
            source.Id,
            "first");
        MailboxImportRunRecord run =
            ProcessingTestData.StartProcessingRun(db.Connection, source);
        ProcessingJobRepository.Enqueue(
            db.Connection,
            "ocr",
            attachment,
            maxAttempts: 1);
        ProcessingJob job = ProcessingJobRepository.LeaseNext(
            db.Connection,
            "worker-1",
            TimeSpan.FromMinutes(5))!;
        ProcessingJobRepository.Fail(
            db.Connection,
            job.Id,
            "worker-1",
            "neutral-error",
            "Neutralny błąd przetwarzania",
            TimeSpan.Zero);
        bool launched = false;
        using var coordinator = Coordinator(db, () =>
        {
            launched = true;
            return ImmediateWorker(() => 0);
        });

        ProcessingCoordinatorResult result = await coordinator.RunAsync(
            SessionKey,
            run.Id);

        Assert.IsFalse(launched);
        Assert.AreEqual(
            ProcessingCoordinatorOutcome.CompletedWithErrors,
            result.Outcome);
        Assert.AreEqual(
            MailboxImportRunStatus.CompletedWithErrors,
            result.Run.Status);
        Assert.AreEqual("ProcessingErrors", result.Run.LastErrorCode);
    }

    [TestMethod]
    public async Task Run_KeepsRunProcessingWhenWorkerReportsLockedSession()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Gmail,
            "first@example.invalid");
        long attachment = ProcessingTestData.AddAttachment(
            db.Connection,
            source.Id,
            "first");
        MailboxImportRunRecord run =
            ProcessingTestData.StartProcessingRun(db.Connection, source);
        ProcessingJobRepository.Enqueue(db.Connection, "download", attachment);
        using var coordinator = Coordinator(
            db,
            () => ImmediateWorker(() => 2));

        ProcessingCoordinatorResult result = await coordinator.RunAsync(
            SessionKey,
            run.Id);

        Assert.AreEqual(ProcessingCoordinatorOutcome.Paused, result.Outcome);
        Assert.AreEqual(MailboxImportRunStatus.Processing, result.Run.Status);
        Assert.AreEqual(2, result.WorkerExitCode);
    }

    [TestMethod]
    public async Task Run_MarksRunFailedWhenWorkerTaskFaults()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Gmail,
            "first@example.invalid");
        long attachment = ProcessingTestData.AddAttachment(
            db.Connection,
            source.Id,
            "first");
        MailboxImportRunRecord run =
            ProcessingTestData.StartProcessingRun(db.Connection, source);
        ProcessingJobRepository.Enqueue(db.Connection, "download", attachment);
        using var coordinator = Coordinator(
            db,
            () => new FakeWorkerSession(
                () => Task.FromException<int>(
                    new InvalidOperationException("Neutralny błąd workera.")),
                () => { }));

        ProcessingCoordinatorResult result = await coordinator.RunAsync(
            SessionKey,
            run.Id);

        Assert.AreEqual(ProcessingCoordinatorOutcome.Failed, result.Outcome);
        Assert.AreEqual(MailboxImportRunStatus.Failed, result.Run.Status);
        StringAssert.StartsWith(result.Run.LastErrorCode, "WorkerWait_");
    }

    [TestMethod]
    public async Task RequestCancellation_StopsWorkerAndCancelsRun()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Gmail,
            "first@example.invalid");
        long attachment = ProcessingTestData.AddAttachment(
            db.Connection,
            source.Id,
            "first");
        MailboxImportRunRecord run =
            ProcessingTestData.StartProcessingRun(db.Connection, source);
        ProcessingJobRepository.Enqueue(db.Connection, "transcribe", attachment);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = Coordinator(
            db,
            () => new FakeWorkerSession(
                () =>
                {
                    started.TrySetResult();
                    return stopped.Task;
                },
                () => stopped.TrySetResult(130)));

        Task<ProcessingCoordinatorResult> running = coordinator.RunAsync(
            SessionKey,
            run.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(coordinator.RequestCancellation(SessionKey, run.Id));
        ProcessingCoordinatorResult result =
            await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(ProcessingCoordinatorOutcome.Cancelled, result.Outcome);
        Assert.AreEqual(MailboxImportRunStatus.Cancelled, result.Run.Status);
        Assert.IsTrue(result.Run.CancelRequested);
    }

    [TestMethod]
    public async Task Pipeline_StartsProcessingAutomaticallyAfterImport()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Gmail,
            "first@example.invalid");
        long attachment = ProcessingTestData.AddAttachment(
            db.Connection,
            source.Id,
            "first");
        MailboxImportRunRecord run =
            MailboxImportRunRepository.Create(db.Connection, [source.Id]);
        var updates = new List<MailboxPipelineUpdate>();
        var importer = new FakeImporter(
            MailboxProvider.Gmail,
            (request, _) =>
            {
                ProcessingJobRepository.Enqueue(
                    request.Database,
                    "download",
                    attachment);
                return Task.FromResult(
                    new MailboxImportResult(1, 1, 0, 0, 0, true));
            });
        var imports = new MailboxImportCoordinator(
            [importer],
            _ => db.OpenAnotherConnection());
        var processing = Coordinator(
            db,
            () => ImmediateWorker(() =>
            {
                using var workerDb = db.OpenAnotherConnection();
                ProcessingJob job = ProcessingJobRepository.LeaseNext(
                    workerDb,
                    "worker-1",
                    TimeSpan.FromMinutes(5))!;
                ProcessingJobRepository.Complete(
                    workerDb,
                    job.Id,
                    "worker-1");
                return 0;
            }));
        using var pipeline = new MailboxPipelineCoordinator(imports, processing);

        MailboxPipelineResult result = await pipeline.RunAsync(
            SessionKey,
            run.Id,
            forceFull: false,
            new CallbackProgress<MailboxPipelineUpdate>(updates.Add));

        Assert.AreEqual(MailboxImportRunStatus.Completed, result.Run.Status);
        Assert.AreEqual(
            ProcessingCoordinatorOutcome.Completed,
            result.Processing?.Outcome);
        Assert.IsTrue(updates.Any(update =>
            update.Phase == MailboxPipelinePhase.Import));
        Assert.IsTrue(updates.Any(update =>
            update.Phase == MailboxPipelinePhase.Processing));
        Assert.AreEqual(MailboxPipelinePhase.Completed, updates.Last().Phase);
    }

    static ProcessingCoordinator Coordinator(
        TestDatabase database,
        Func<IProcessingWorkerSession> launcher)
        => new(
            _ => database.OpenAnotherConnection(),
            launcher,
            TimeSpan.FromMilliseconds(10));

    static IProcessingWorkerSession ImmediateWorker(Func<int> work)
        => new FakeWorkerSession(
            () => Task.FromResult(work()),
            () => { });

    sealed class FakeWorkerSession(
        Func<Task<int>> wait,
        Action stop) : IProcessingWorkerSession
    {
        public Task<int> WaitForExitAsync() => wait();

        public void RequestStop() => stop();

        public void Dispose()
        {
        }
    }

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
