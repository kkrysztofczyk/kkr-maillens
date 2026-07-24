namespace KKR.MailLens;

enum MailboxPipelinePhase
{
    Import,
    Processing,
    Completed,
}

sealed record MailboxPipelineUpdate(
    long RunId,
    MailboxPipelinePhase Phase,
    MailboxImportCoordinatorUpdate? Import = null,
    ProcessingCoordinatorUpdate? Processing = null);

sealed record MailboxPipelineResult(
    MailboxImportRunRecord Run,
    ProcessingCoordinatorResult? Processing);

sealed class MailboxPipelineCoordinator : IDisposable
{
    readonly MailboxImportCoordinator _imports;
    readonly ProcessingCoordinator _processing;
    bool _disposed;

    public MailboxPipelineCoordinator(
        MailboxImportCoordinator? imports = null,
        ProcessingCoordinator? processing = null)
    {
        _imports = imports ?? new MailboxImportCoordinator();
        _processing = processing ?? new ProcessingCoordinator();
    }

    public async Task<MailboxPipelineResult> RunAsync(
        string sessionKeyHex,
        long runId,
        bool? forceFull = null,
        IProgress<MailboxPipelineUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var importProgress = new CallbackProgress<MailboxImportCoordinatorUpdate>(
            update => progress?.Report(new MailboxPipelineUpdate(
                runId,
                MailboxPipelinePhase.Import,
                Import: update)));
        MailboxImportRunRecord run = await _imports.RunAsync(
            sessionKeyHex,
            runId,
            forceFull,
            importProgress,
            cancellationToken).ConfigureAwait(false);

        if (run.Status != MailboxImportRunStatus.Processing)
        {
            progress?.Report(new MailboxPipelineUpdate(
                runId,
                MailboxPipelinePhase.Completed));
            return new MailboxPipelineResult(run, null);
        }

        var processingProgress = new CallbackProgress<ProcessingCoordinatorUpdate>(
            update => progress?.Report(new MailboxPipelineUpdate(
                runId,
                MailboxPipelinePhase.Processing,
                Processing: update)));
        ProcessingCoordinatorResult processing = await _processing.RunAsync(
            sessionKeyHex,
            runId,
            processingProgress,
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new MailboxPipelineUpdate(
            runId,
            MailboxPipelinePhase.Completed,
            Processing: new ProcessingCoordinatorUpdate(
                runId,
                processing.Pipeline,
                processing.Outcome)));
        return new MailboxPipelineResult(processing.Run, processing);
    }

    public bool RequestCancellation(string sessionKeyHex, long runId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool importChanged = _imports.RequestCancellation(sessionKeyHex, runId);
        bool processingChanged = _processing.RequestCancellation(sessionKeyHex, runId);
        return importChanged || processingChanged;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _imports.Dispose();
        _processing.Dispose();
    }
}
