namespace KKR.MailLens;

sealed class GmailMailboxImporter : IMailboxImporter
{
    readonly Func<GmailAccountRecord, string, CancellationToken, Task<IGmailApiClient>> _apiClientFactory;

    public GmailMailboxImporter(
        Func<GmailAccountRecord, string, CancellationToken, Task<IGmailApiClient>>? apiClientFactory = null)
    {
        _apiClientFactory = apiClientFactory ?? GmailOAuth.CreateApiClientAsync;
    }

    public MailboxProvider Provider => MailboxProvider.Gmail;

    public async Task<MailboxImportResult> ImportAsync(
        MailboxImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Source.Provider != Provider)
            throw new ArgumentException("Źródło nie jest kontem Gmail.", nameof(request));

        long accountId = ParseAccountId(request.Source.CredentialReference);
        GmailAccountRecord account = GmailRepository.FindAccount(request.Database, accountId)
            ?? throw new InvalidOperationException("Połączone konto Gmail nie istnieje.");
        if (!string.Equals(account.Email, request.Source.ExternalKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Konfiguracja źródła Gmail nie odpowiada połączonemu kontu.");

        cancellationToken.ThrowIfCancellationRequested();
        GmailCancellation.Clear(account.Id);
        try
        {
            using IGmailApiClient api = await _apiClientFactory(
                account,
                request.SessionKeyHex,
                cancellationToken).ConfigureAwait(false);
            var progress = request.Progress is null
                ? null
                : new CallbackProgress<GmailSyncProgress>(value =>
                    request.Progress.Report(new MailboxImportProgress(
                        NormalizePhase(value.Phase),
                        value.Processed,
                        Errors: value.Errors)));
            var synchronizer = new GmailSynchronizer(request.Database, api, progress);
            GmailSyncResult result = await synchronizer.SyncAsync(
                account,
                request.ForceFull,
                cancellationToken).ConfigureAwait(false);
            var finalProgress = new MailboxImportProgress(
                "imported",
                result.Processed,
                Inserted: result.Inserted,
                Updated: result.Updated,
                Deleted: result.Deleted,
                Errors: result.Errors);
            request.Progress?.Report(finalProgress);
            return new MailboxImportResult(
                result.Processed,
                result.Inserted,
                result.Updated,
                result.Deleted,
                result.Errors,
                result.WasFullSync);
        }
        finally
        {
            GmailCancellation.Clear(account.Id);
        }
    }

    static long ParseAccountId(string? credentialReference)
    {
        const string prefix = "gmail-account:";
        if (credentialReference is null
            || !credentialReference.StartsWith(prefix, StringComparison.Ordinal)
            || !long.TryParse(credentialReference[prefix.Length..], out long accountId)
            || accountId <= 0)
            throw new InvalidOperationException("Źródło Gmail nie ma prawidłowego odwołania do konta.");
        return accountId;
    }

    static string NormalizePhase(string phase) => phase switch
    {
        "full" => "importing-full",
        "incremental" => "importing-incremental",
        "retry" => "retrying",
        "history-reset" => "resetting-history",
        _ => "importing",
    };
}
