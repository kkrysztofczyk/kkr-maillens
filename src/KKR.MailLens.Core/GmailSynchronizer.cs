using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class GmailSynchronizer
{
    readonly SqliteConnection _database;
    readonly IGmailApiClient _api;
    readonly IProgress<GmailSyncProgress>? _progress;
    readonly int _parallelism;

    public GmailSynchronizer(SqliteConnection database, IGmailApiClient api,
        IProgress<GmailSyncProgress>? progress = null, int parallelism = 4)
    {
        _database = database;
        _api = api;
        _progress = progress;
        _parallelism = Math.Clamp(parallelism, 1, 8);
    }

    public async Task<GmailSyncResult> SyncAsync(GmailAccountRecord account, bool forceFull, CancellationToken cancellationToken)
    {
        account = GmailRepository.FindAccount(_database, account.Id) ?? throw new InvalidOperationException("Konto Gmail nie istnieje.");
        bool shouldFull = forceFull || !account.InitialSyncCompleted || string.IsNullOrWhiteSpace(account.LastHistoryId);
        GmailRepository.SetOperation(_database, account.Id, shouldFull ? "full-sync" : "incremental-sync", errors: 0);

        try
        {
            GmailSyncResult result;
            try
            {
                result = shouldFull
                    ? await FullSync(account, forceFull, cancellationToken).ConfigureAwait(false)
                    : await IncrementalSync(account, cancellationToken).ConfigureAwait(false);
            }
            catch (GmailHistoryExpiredException)
            {
                _progress?.Report(new GmailSyncProgress("history-reset", 0, 0));
                account = GmailRepository.FindAccount(_database, account.Id)!;
                result = await FullSync(account, reset: true, cancellationToken).ConfigureAwait(false);
            }

            GmailRepository.SetOperation(_database, account.Id, null, (int)Math.Min(int.MaxValue, result.Errors), completed: true);
            return result;
        }
        catch
        {
            var current = GmailRepository.FindAccount(_database, account.Id);
            GmailRepository.SetOperation(_database, account.Id, null, current?.LastErrorCount);
            throw;
        }
    }

    async Task<GmailSyncResult> FullSync(GmailAccountRecord account, bool reset, CancellationToken cancellationToken)
    {
        GmailCancellation.ThrowIfRequested(cancellationToken);
        GmailProfile profile = await _api.GetProfileAsync(cancellationToken).ConfigureAwait(false);
        bool startNewGeneration = reset || account.InitialSyncCompleted || account.SyncGeneration == 0;
        GmailRepository.BeginFullSync(_database, account.Id, profile.HistoryId, startNewGeneration);
        account = GmailRepository.FindAccount(_database, account.Id)!;

        IReadOnlyList<GmailApiLabel> labels = await _api.GetLabelsAsync(cancellationToken).ConfigureAwait(false);
        GmailRepository.UpsertLabels(_database, account.Id, labels);
        IReadOnlyDictionary<string, string> labelNames = GmailRepository.LabelNames(_database, account.Id);

        long processed = 0, inserted = 0, updated = 0, deleted = 0, errors = 0;
        string? pageToken = account.InitialPageToken;
        do
        {
            GmailCancellation.ThrowIfRequested(cancellationToken);
            GmailMessagePage page;
            try { page = await _api.ListMessageIdsAsync(pageToken, cancellationToken).ConfigureAwait(false); }
            catch (GmailPageTokenExpiredException)
            {
                GmailRepository.BeginFullSync(_database, account.Id, profile.HistoryId, reset: true);
                account = GmailRepository.FindAccount(_database, account.Id)!;
                pageToken = null;
                continue;
            }

            PageResult saved = await FetchAndSave(account, page.MessageIds, labelNames, cancellationToken).ConfigureAwait(false);
            processed += page.MessageIds.Count;
            inserted += saved.Inserted;
            updated += saved.Updated;
            deleted += saved.Deleted;
            errors += saved.Errors;
            GmailRepository.CheckpointFullPage(_database, account.Id, page.NextPageToken);
            pageToken = page.NextPageToken;
            _progress?.Report(new GmailSyncProgress("full", processed, errors));
        }
        while (!string.IsNullOrEmpty(pageToken));

        deleted += GmailRepository.PruneMissingMessages(_database, account.Id, account.SyncGeneration);

        string historyId = account.LastHistoryId ?? profile.HistoryId;
        IncrementalResult catchUp = await IncrementalCore(account, historyId, labelNames, cancellationToken).ConfigureAwait(false);
        processed += catchUp.Processed;
        inserted += catchUp.Inserted;
        updated += catchUp.Updated;
        deleted += catchUp.Deleted;
        errors += catchUp.Errors;
        historyId = catchUp.HistoryId;

        GmailRepository.CompleteFullSync(_database, account.Id, historyId, (int)Math.Min(int.MaxValue, errors));
        return new GmailSyncResult(processed, inserted, updated, deleted, errors, WasFullSync: true);
    }

    async Task<GmailSyncResult> IncrementalSync(GmailAccountRecord account, CancellationToken cancellationToken)
    {
        GmailCancellation.ThrowIfRequested(cancellationToken);
        IReadOnlyList<GmailApiLabel> labels = await _api.GetLabelsAsync(cancellationToken).ConfigureAwait(false);
        GmailRepository.UpsertLabels(_database, account.Id, labels);
        IReadOnlyDictionary<string, string> labelNames = GmailRepository.LabelNames(_database, account.Id);
        IncrementalResult result = await IncrementalCore(account, account.LastHistoryId!, labelNames, cancellationToken).ConfigureAwait(false);
        GmailRepository.UpdateHistory(_database, account.Id, result.HistoryId);
        return new GmailSyncResult(result.Processed, result.Inserted, result.Updated, result.Deleted, result.Errors, WasFullSync: false);
    }

    async Task<IncrementalResult> IncrementalCore(GmailAccountRecord account, string startHistoryId,
        IReadOnlyDictionary<string, string> labelNames, CancellationToken cancellationToken)
    {
        long processed = 0, inserted = 0, updated = 0, deleted = 0, errors = 0;
        string? pageToken = null;
        string latestHistoryId = startHistoryId;
        do
        {
            GmailCancellation.ThrowIfRequested(cancellationToken);
            GmailHistoryPage page = await _api.ListHistoryAsync(startHistoryId, pageToken, cancellationToken).ConfigureAwait(false);
            deleted += GmailRepository.DeleteMessages(_database, account.Id, page.DeletedMessageIds);
            PageResult saved = await FetchAndSave(account, page.UpsertMessageIds, labelNames, cancellationToken).ConfigureAwait(false);
            processed += page.UpsertMessageIds.Count + page.DeletedMessageIds.Count;
            inserted += saved.Inserted;
            updated += saved.Updated;
            deleted += saved.Deleted;
            errors += saved.Errors;
            latestHistoryId = string.IsNullOrWhiteSpace(page.HistoryId) ? latestHistoryId : page.HistoryId;
            pageToken = page.NextPageToken;
            _progress?.Report(new GmailSyncProgress("incremental", processed, errors));
        }
        while (!string.IsNullOrEmpty(pageToken));

        GmailRepository.UpdateHistory(_database, account.Id, latestHistoryId);
        return new IncrementalResult(processed, inserted, updated, deleted, errors, latestHistoryId);
    }

    async Task<PageResult> FetchAndSave(GmailAccountRecord account, IReadOnlyList<string> messageIds,
        IReadOnlyDictionary<string, string> labelNames, CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0) return new PageResult(0, 0, 0, 0);
        using var gate = new SemaphoreSlim(_parallelism);
        var mapped = new ConcurrentBag<GmailStoredMessage>();
        var missing = new ConcurrentBag<string>();
        var failures = new ConcurrentBag<(string MessageId, string Code)>();

        var tasks = messageIds.Distinct(StringComparer.Ordinal).Select(async messageId =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                GmailCancellation.ThrowIfRequested(cancellationToken);
                GmailApiMessage source = await _api.GetMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
                mapped.Add(GmailMessageMapper.Map(source, account.Id));
            }
            catch (GmailMessageNotFoundException) { missing.Add(messageId); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add((messageId, ex.GetType().Name)); }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        int deleted = GmailRepository.DeleteMessages(_database, account.Id, missing);
        foreach (var failure in failures)
            GmailRepository.RecordError(_database, account.Id, failure.MessageId, "download", failure.Code);

        GmailSaveBatchResult saved;
        using (var transaction = _database.BeginTransaction())
        {
            saved = GmailRepository.SaveMessages(_database, transaction, account.SyncGeneration, mapped.ToArray());
            if (saved.Saved.Count > 0)
            {
                string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                Corpus.Upsert(_database, transaction,
                    saved.Saved.Select(x => GmailMessageMapper.ToHarvested(x, labelNames)), stamp);
                MailAttachmentRepository.UpsertGmail(_database, transaction, account.SyncGeneration, saved.Saved);
            }
            transaction.Commit();
        }

        return new PageResult(saved.Inserted, saved.Updated, deleted, failures.Count + saved.FailedMessageIds.Count);
    }

    sealed record PageResult(long Inserted, long Updated, long Deleted, long Errors);
    sealed record IncrementalResult(long Processed, long Inserted, long Updated, long Deleted, long Errors, string HistoryId);
}
