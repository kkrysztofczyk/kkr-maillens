using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed record MailboxImportCoordinatorUpdate(
    long RunId,
    MailboxImportRunStatus RunStatus,
    long? SourceRunId = null,
    MailboxImportSourceStatus? SourceStatus = null,
    MailboxImportProgress? Progress = null);

sealed class MailboxImportCoordinator : IDisposable
{
    readonly IReadOnlyDictionary<MailboxProvider, IMailboxImporter> _importers;
    readonly Func<string, SqliteConnection> _connectionFactory;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _activeLock = new();
    CancellationTokenSource? _activeCancellation;
    long? _activeRunId;
    bool _disposed;

    public MailboxImportCoordinator(
        IEnumerable<IMailboxImporter>? importers = null,
        Func<string, SqliteConnection>? connectionFactory = null)
    {
        IMailboxImporter[] configured = (importers ??
        [
            new GmailMailboxImporter(),
            new ImapMailboxImporter(),
            new OutlookMailboxImporter(),
        ]).ToArray();
        if (configured.GroupBy(importer => importer.Provider).Any(group => group.Count() != 1))
            throw new ArgumentException("Każdy typ skrzynki musi mieć dokładnie jeden importer.", nameof(importers));
        _importers = configured.ToDictionary(importer => importer.Provider);
        _connectionFactory = connectionFactory ?? (key => Db.Open(key, create: false));
    }

    public async Task<MailboxImportRunRecord> RunAsync(
        string sessionKeyHex,
        long runId,
        bool forceFull,
        IProgress<MailboxImportCoordinatorUpdate>? progress = null,
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

            if (run.Status == MailboxImportRunStatus.Importing)
            {
                MailboxImportRunRepository.RecoverInterruptedImport(database, runId);
                run = MailboxImportRunRepository.Find(database, runId)!;
            }
            if (run.CancelRequested)
                return FinishCancelledBeforeStart(database, run, progress);
            if (run.Status != MailboxImportRunStatus.Queued)
                return run;
            if (!MailboxImportRunRepository.Start(database, runId))
            {
                run = MailboxImportRunRepository.Find(database, runId)!;
                return run.CancelRequested
                    ? FinishCancelledBeforeStart(database, run, progress)
                    : run;
            }

            progress?.Report(new MailboxImportCoordinatorUpdate(
                runId,
                MailboxImportRunStatus.Importing));

            while (true)
            {
                if (linkedCancellation.IsCancellationRequested)
                {
                    MailboxImportRunRepository.RequestCancellation(database, runId);
                    break;
                }

                run = MailboxImportRunRepository.Find(database, runId)!;
                if (run.CancelRequested)
                {
                    linkedCancellation.Cancel();
                    break;
                }

                MailboxImportSourceRecord? source =
                    MailboxImportRunRepository.StartNextSource(database, runId);
                if (source is null)
                    break;

                progress?.Report(new MailboxImportCoordinatorUpdate(
                    runId,
                    MailboxImportRunStatus.Importing,
                    source.Id,
                    MailboxImportSourceStatus.Importing,
                    new MailboxImportProgress("connecting", 0)));

                MailboxImportProgress lastProgress = new("connecting", 0);
                var sourceProgress = new CallbackProgress<MailboxImportProgress>(value =>
                {
                    lastProgress = value;
                    MailboxImportRunRepository.SaveProgress(database, source.Id, value);
                    progress?.Report(new MailboxImportCoordinatorUpdate(
                        runId,
                        MailboxImportRunStatus.Importing,
                        source.Id,
                        MailboxImportSourceStatus.Importing,
                        value));
                });

                if (!_importers.TryGetValue(source.Provider, out IMailboxImporter? importer))
                {
                    MailboxImportProgress failed = FailedProgress(lastProgress);
                    MailboxImportRunRepository.MarkSourceFailed(
                        database,
                        source.Id,
                        failed,
                        "ImporterNotConfigured");
                    ReportSourceFinished(progress, runId, source.Id,
                        MailboxImportSourceStatus.Failed, failed);
                    continue;
                }

                try
                {
                    MailboxImportResult result = await importer.ImportAsync(
                        new MailboxImportRequest(
                            database,
                            sessionKeyHex,
                            source,
                            forceFull,
                            sourceProgress),
                        linkedCancellation.Token).ConfigureAwait(false);
                    MailboxImportProgress completed = new(
                        "imported",
                        result.Processed,
                        Inserted: result.Inserted,
                        Updated: result.Updated,
                        Deleted: result.Deleted,
                        Errors: result.Errors);
                    MailboxImportRunRepository.MarkSourceImported(
                        database,
                        source.Id,
                        completed);
                    ReportSourceFinished(progress, runId, source.Id,
                        MailboxImportSourceStatus.Imported, completed);
                }
                catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                {
                    MailboxImportRunRepository.RequestCancellation(database, runId);
                    MailboxImportProgress cancelled = lastProgress with { Phase = "cancelled" };
                    MailboxImportRunRepository.MarkSourceCancelled(
                        database,
                        source.Id,
                        cancelled);
                    ReportSourceFinished(progress, runId, source.Id,
                        MailboxImportSourceStatus.Cancelled, cancelled);
                    break;
                }
                catch (Exception exception)
                {
                    MailboxImportProgress failed = FailedProgress(lastProgress);
                    MailboxImportRunRepository.MarkSourceFailed(
                        database,
                        source.Id,
                        failed,
                        SafeErrorCode(exception));
                    ReportSourceFinished(progress, runId, source.Id,
                        MailboxImportSourceStatus.Failed, failed);
                }
            }

            MailboxImportRunStatus? finished =
                MailboxImportRunRepository.FinishImportPhase(database, runId);
            MailboxImportRunRecord resultRun =
                MailboxImportRunRepository.Find(database, runId)!;
            progress?.Report(new MailboxImportCoordinatorUpdate(
                runId,
                finished ?? resultRun.Status));
            return resultRun;
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
                _activeCancellation?.Cancel();
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
            _activeCancellation = null;
            _activeRunId = null;
        }
    }

    MailboxImportRunRecord FinishCancelledBeforeStart(
        SqliteConnection database,
        MailboxImportRunRecord run,
        IProgress<MailboxImportCoordinatorUpdate>? progress)
    {
        MailboxImportRunRepository.FinishImportPhase(database, run.Id);
        MailboxImportRunRecord cancelled =
            MailboxImportRunRepository.Find(database, run.Id)!;
        progress?.Report(new MailboxImportCoordinatorUpdate(
            run.Id,
            cancelled.Status));
        return cancelled;
    }

    static MailboxImportProgress FailedProgress(MailboxImportProgress last)
        => last with
        {
            Phase = "failed",
            Errors = Math.Max(1, last.Errors),
        };

    static string SafeErrorCode(Exception exception)
    {
        string typeName = exception.GetType().Name;
        string code = new(typeName
            .Select(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.'
                    ? character
                    : '_')
            .Take(80)
            .ToArray());
        return code.Length == 0 ? "ImportFailed" : code;
    }

    static void ReportSourceFinished(
        IProgress<MailboxImportCoordinatorUpdate>? progress,
        long runId,
        long sourceRunId,
        MailboxImportSourceStatus status,
        MailboxImportProgress sourceProgress)
        => progress?.Report(new MailboxImportCoordinatorUpdate(
            runId,
            MailboxImportRunStatus.Importing,
            sourceRunId,
            status,
            sourceProgress));

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
            }
        }
    }
}
