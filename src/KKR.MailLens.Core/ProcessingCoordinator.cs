using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

enum ProcessingCoordinatorOutcome
{
    Completed,
    CompletedWithErrors,
    Cancelled,
    Paused,
    Failed,
}

sealed record ProcessingCoordinatorUpdate(
    long RunId,
    ProcessingPipelineSnapshot Pipeline,
    ProcessingCoordinatorOutcome? Outcome = null);

sealed record ProcessingCoordinatorResult(
    MailboxImportRunRecord Run,
    ProcessingPipelineSnapshot Pipeline,
    ProcessingCoordinatorOutcome Outcome,
    int? WorkerExitCode = null);

interface IProcessingWorkerSession : IDisposable
{
    Task<int> WaitForExitAsync();
    void RequestStop();
}

sealed class RestrictedProcessingWorkerSession : IProcessingWorkerSession
{
    readonly RestrictedWorkerProcess _worker;

    public RestrictedProcessingWorkerSession(RestrictedWorkerProcess worker)
    {
        _worker = worker;
    }

    public async Task<int> WaitForExitAsync()
    {
        await _worker.Process.WaitForExitAsync().ConfigureAwait(false);
        return _worker.Process.ExitCode;
    }

    public void RequestStop() => _worker.RequestStop();

    public void Dispose() => _worker.Dispose();
}

sealed class ProcessingCoordinator : IDisposable
{
    readonly Func<string, SqliteConnection> _connectionFactory;
    readonly Func<IProcessingWorkerSession> _workerLauncher;
    readonly TimeSpan _pollInterval;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _activeLock = new();
    CancellationTokenSource? _activeCancellation;
    IProcessingWorkerSession? _activeWorker;
    long? _activeRunId;
    bool _disposed;

    public ProcessingCoordinator(
        Func<string, SqliteConnection>? connectionFactory = null,
        Func<IProcessingWorkerSession>? workerLauncher = null,
        TimeSpan? pollInterval = null)
    {
        _connectionFactory = connectionFactory ?? (key => Db.Open(key, create: false));
        _workerLauncher = workerLauncher ?? StartRestrictedWorker;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        if (_pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public async Task<ProcessingCoordinatorResult> RunAsync(
        string sessionKeyHex,
        long runId,
        IProgress<ProcessingCoordinatorUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sessionKeyHex))
            throw new ArgumentException("Brak klucza aktywnej sesji.", nameof(sessionKeyHex));
        if (runId <= 0)
            throw new ArgumentOutOfRangeException(nameof(runId));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetActive(runId, linkedCancellation);
        try
        {
            using SqliteConnection database = _connectionFactory(sessionKeyHex);
            Db.EnsureSchema(database);
            MailboxImportRunRecord run = MailboxImportRunRepository.Find(database, runId)
                ?? throw new KeyNotFoundException($"Nie istnieje kolejka importu {runId}.");
            if (run.Status != MailboxImportRunStatus.Processing)
                return ExistingResult(database, run);

            while (true)
            {
                run = MailboxImportRunRepository.Find(database, runId)!;
                ProcessingPipelineSnapshot pipeline = ProcessingPipelineStatus.Read(
                    database,
                    run.ProcessingJobBaselineId,
                    mailboxImportRunId: run.Id);
                progress?.Report(new ProcessingCoordinatorUpdate(runId, pipeline));

                if (run.CancelRequested || linkedCancellation.IsCancellationRequested)
                    return await CancelAsync(database, run, pipeline).ConfigureAwait(false);

                if (!pipeline.HasActiveJobs)
                {
                    bool hadErrors = pipeline.Failed > 0;
                    MailboxImportRunRepository.CompleteProcessing(database, runId, hadErrors);
                    MailboxImportRunRecord completed =
                        MailboxImportRunRepository.Find(database, runId)!;
                    ProcessingCoordinatorOutcome outcome = hadErrors
                        ? ProcessingCoordinatorOutcome.CompletedWithErrors
                        : ProcessingCoordinatorOutcome.Completed;
                    progress?.Report(new ProcessingCoordinatorUpdate(runId, pipeline, outcome));
                    return new ProcessingCoordinatorResult(completed, pipeline, outcome);
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                bool runningLeaseCanBeRecovered = pipeline.Running > 0
                    && pipeline.NextLeaseExpiryAt is { } leaseExpiry
                    && leaseExpiry <= now;
                if ((pipeline.Running > 0 && !runningLeaseCanBeRecovered)
                    || (pipeline.Running == 0 && pipeline.Ready == 0))
                {
                    try
                    {
                        await Task.Delay(_pollInterval, linkedCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                    {
                        return await CancelAsync(database, run, pipeline).ConfigureAwait(false);
                    }
                    continue;
                }

                IProcessingWorkerSession worker;
                try
                {
                    worker = _workerLauncher();
                }
                catch (Exception exception)
                {
                    string code = SafeCode("WorkerStart", exception);
                    MailboxImportRunRepository.FailRun(database, runId, code);
                    MailboxImportRunRecord failed =
                        MailboxImportRunRepository.Find(database, runId)!;
                    progress?.Report(new ProcessingCoordinatorUpdate(
                        runId,
                        pipeline,
                        ProcessingCoordinatorOutcome.Failed));
                    return new ProcessingCoordinatorResult(
                        failed,
                        pipeline,
                        ProcessingCoordinatorOutcome.Failed);
                }

                using (worker)
                {
                    SetActiveWorker(worker);
                    Task<int> exit;
                    try
                    {
                        exit = worker.WaitForExitAsync();
                    }
                    catch (Exception exception)
                    {
                        return Fail(
                            database,
                            runId,
                            pipeline,
                            SafeCode("WorkerWait", exception),
                            progress);
                    }
                    try
                    {
                        while (!exit.IsCompleted)
                        {
                            try
                            {
                                await Task.Delay(_pollInterval, linkedCancellation.Token)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                            {
                                return await CancelWorkerAsync(database, run, worker, exit, pipeline)
                                    .ConfigureAwait(false);
                            }
                            pipeline = ProcessingPipelineStatus.Read(
                                database,
                                run.ProcessingJobBaselineId,
                                mailboxImportRunId: run.Id);
                            progress?.Report(new ProcessingCoordinatorUpdate(runId, pipeline));
                            if (MailboxImportRunRepository.Find(database, runId)!.CancelRequested)
                                return await CancelWorkerAsync(database, run, worker, exit, pipeline)
                                    .ConfigureAwait(false);
                        }

                        int exitCode;
                        try
                        {
                            exitCode = await exit.ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            return Fail(
                                database,
                                runId,
                                pipeline,
                                SafeCode("WorkerWait", exception),
                                progress);
                        }
                        pipeline = ProcessingPipelineStatus.Read(
                            database,
                            run.ProcessingJobBaselineId,
                            mailboxImportRunId: run.Id);
                        progress?.Report(new ProcessingCoordinatorUpdate(runId, pipeline));
                        if (exitCode == 0)
                            continue;
                        if (exitCode == 2)
                        {
                            progress?.Report(new ProcessingCoordinatorUpdate(
                                runId,
                                pipeline,
                                ProcessingCoordinatorOutcome.Paused));
                            return new ProcessingCoordinatorResult(
                                MailboxImportRunRepository.Find(database, runId)!,
                                pipeline,
                                ProcessingCoordinatorOutcome.Paused,
                                exitCode);
                        }
                        if (exitCode == 130)
                            return await CancelWorkerAsync(database, run, worker, exit, pipeline)
                                .ConfigureAwait(false);

                        return Fail(
                            database,
                            runId,
                            pipeline,
                            $"WorkerExit_{exitCode}",
                            progress,
                            exitCode);
                    }
                    finally
                    {
                        ClearActiveWorker(worker);
                    }
                }
            }
        }
        finally
        {
            ClearActive(runId, linkedCancellation);
            _gate.Release();
        }
    }

    public bool RequestCancellation(string sessionKeyHex, long runId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using SqliteConnection database = _connectionFactory(sessionKeyHex);
        Db.EnsureSchema(database);
        bool changed = MailboxImportRunRepository.RequestCancellation(database, runId);
        lock (_activeLock)
        {
            if (_activeRunId == runId)
            {
                _activeCancellation?.Cancel();
                _activeWorker?.RequestStop();
            }
        }
        return changed;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_activeLock)
        {
            _activeCancellation?.Cancel();
            _activeWorker?.RequestStop();
            _activeCancellation = null;
            _activeWorker = null;
            _activeRunId = null;
        }
    }

    async Task<ProcessingCoordinatorResult> CancelWorkerAsync(
        SqliteConnection database,
        MailboxImportRunRecord run,
        IProcessingWorkerSession worker,
        Task<int> exit,
        ProcessingPipelineSnapshot pipeline)
    {
        MailboxImportRunRepository.RequestCancellation(database, run.Id);
        worker.RequestStop();
        try
        {
            await exit.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        return await CancelAsync(database, run, pipeline).ConfigureAwait(false);
    }

    Task<ProcessingCoordinatorResult> CancelAsync(
        SqliteConnection database,
        MailboxImportRunRecord run,
        ProcessingPipelineSnapshot pipeline)
    {
        MailboxImportRunRepository.RequestCancellation(database, run.Id);
        MailboxImportRunRepository.CompleteProcessing(
            database,
            run.Id,
            processingHadErrors: pipeline.Failed > 0);
        MailboxImportRunRecord cancelled =
            MailboxImportRunRepository.Find(database, run.Id)!;
        return Task.FromResult(new ProcessingCoordinatorResult(
            cancelled,
            pipeline,
            ProcessingCoordinatorOutcome.Cancelled));
    }

    static ProcessingCoordinatorResult ExistingResult(
        SqliteConnection database,
        MailboxImportRunRecord run)
    {
        ProcessingPipelineSnapshot pipeline = ProcessingPipelineStatus.Read(
            database,
            run.ProcessingJobBaselineId,
            mailboxImportRunId: run.Id);
        ProcessingCoordinatorOutcome outcome = run.Status switch
        {
            MailboxImportRunStatus.Completed => ProcessingCoordinatorOutcome.Completed,
            MailboxImportRunStatus.CompletedWithErrors => ProcessingCoordinatorOutcome.CompletedWithErrors,
            MailboxImportRunStatus.Cancelled => ProcessingCoordinatorOutcome.Cancelled,
            MailboxImportRunStatus.Failed => ProcessingCoordinatorOutcome.Failed,
            _ => throw new InvalidOperationException("Import nie zakończył jeszcze fazy pobierania skrzynek."),
        };
        return new ProcessingCoordinatorResult(run, pipeline, outcome);
    }

    static IProcessingWorkerSession StartRestrictedWorker()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "KKR.MailLens.Worker.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException(
                "Brak KKR.MailLens.Worker.exe w katalogu aplikacji.",
                executable);
        int memoryLimitMb = Math.Clamp(AppConfig.Load().WorkerMemoryLimitMb, 256, 16_384);
        return new RestrictedProcessingWorkerSession(
            RestrictedWorkerProcess.Start(
                executable,
                "--drain",
                memoryLimitMb * 1024L * 1024L));
    }

    static string SafeCode(string prefix, Exception exception)
    {
        string raw = $"{prefix}_{exception.GetType().Name}";
        string code = new(raw
            .Select(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.'
                    ? character
                    : '_')
            .Take(80)
            .ToArray());
        return code.Length == 0 ? "ProcessingFailed" : code;
    }

    static ProcessingCoordinatorResult Fail(
        SqliteConnection database,
        long runId,
        ProcessingPipelineSnapshot pipeline,
        string errorCode,
        IProgress<ProcessingCoordinatorUpdate>? progress,
        int? workerExitCode = null)
    {
        MailboxImportRunRepository.FailRun(database, runId, errorCode);
        MailboxImportRunRecord failed =
            MailboxImportRunRepository.Find(database, runId)!;
        progress?.Report(new ProcessingCoordinatorUpdate(
            runId,
            pipeline,
            ProcessingCoordinatorOutcome.Failed));
        return new ProcessingCoordinatorResult(
            failed,
            pipeline,
            ProcessingCoordinatorOutcome.Failed,
            workerExitCode);
    }

    void SetActive(long runId, CancellationTokenSource cancellation)
    {
        lock (_activeLock)
        {
            _activeRunId = runId;
            _activeCancellation = cancellation;
        }
    }

    void ClearActive(long runId, CancellationTokenSource cancellation)
    {
        lock (_activeLock)
        {
            if (_activeRunId == runId && ReferenceEquals(_activeCancellation, cancellation))
            {
                _activeRunId = null;
                _activeCancellation = null;
                _activeWorker = null;
            }
        }
    }

    void SetActiveWorker(IProcessingWorkerSession worker)
    {
        lock (_activeLock)
            _activeWorker = worker;
    }

    void ClearActiveWorker(IProcessingWorkerSession worker)
    {
        lock (_activeLock)
        {
            if (ReferenceEquals(_activeWorker, worker))
                _activeWorker = null;
        }
    }
}
