using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KKR.MailLens;

delegate ImapHarvestResult ImapHarvestOperation(
    ImapAccount account,
    string sessionKeyHex,
    DateTime? from,
    int maxPerFolder,
    Action<string> onFolder,
    Action<int, int>? onProgress,
    Action<List<HarvestedMail>> flush,
    int batchSize,
    CancellationToken cancellationToken);

sealed record ImapMailboxSettings(
    string Host,
    int Port,
    bool UseSsl,
    string User,
    int MaxPerFolder = 5000,
    string? SinceUtc = null);

static class ImapMailboxRegistration
{
    const string CredentialPrefix = "imap-account:";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static MailboxSourceRecord Register(
        Microsoft.Data.Sqlite.SqliteConnection database,
        ImapAccount account,
        int maxPerFolder = 5000,
        DateTime? sinceUtc = null)
    {
        Validate(account, maxPerFolder);
        var settings = new ImapMailboxSettings(
            account.Host.Trim(),
            account.Port,
            account.UseSsl,
            account.User.Trim(),
            maxPerFolder,
            sinceUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return MailboxSourceRepository.Upsert(database, new MailboxSourceDefinition(
            MailboxProvider.Imap,
            ExternalKey(account),
            account.Name.Trim(),
            CredentialPrefix + account.Name.Trim(),
            JsonSerializer.Serialize(settings, Json)));
    }

    public static string AccountName(string? credentialReference)
    {
        if (credentialReference is null
            || !credentialReference.StartsWith(CredentialPrefix, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(credentialReference[CredentialPrefix.Length..]))
            throw new InvalidOperationException("Źródło IMAP nie ma prawidłowego odwołania do konta.");
        return credentialReference[CredentialPrefix.Length..];
    }

    public static string ExternalKey(ImapAccount account)
    {
        Validate(account, maxPerFolder: 1);
        string canonical = string.Join('\n',
            account.Host.Trim().ToLowerInvariant(),
            account.Port.ToString(CultureInfo.InvariantCulture),
            account.UseSsl ? "ssl-on-connect" : "starttls",
            account.User.Trim());
        byte[] bytes = Encoding.UTF8.GetBytes(canonical);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static ImapMailboxSettings ReadSettings(string settingsJson)
    {
        ImapMailboxSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<ImapMailboxSettings>(settingsJson, Json)
                ?? throw new JsonException();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Nieprawidłowe ustawienia źródła IMAP.", ex);
        }

        Validate(new ImapAccount
        {
            Name = "snapshot",
            Host = settings.Host,
            Port = settings.Port,
            UseSsl = settings.UseSsl,
            User = settings.User,
        }, settings.MaxPerFolder);
        if (settings.SinceUtc is not null
            && !DateTimeOffset.TryParse(
                settings.SinceUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
            throw new InvalidDataException("Nieprawidłowa data początkowa źródła IMAP.");
        return settings;
    }

    static void Validate(ImapAccount account, int maxPerFolder)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(account.Name))
            throw new ArgumentException("Nazwa konta IMAP nie może być pusta.", nameof(account));
        if (string.IsNullOrWhiteSpace(account.Host))
            throw new ArgumentException("Host IMAP nie może być pusty.", nameof(account));
        if (account.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(account), "Port IMAP musi mieścić się w zakresie 1..65535.");
        if (string.IsNullOrWhiteSpace(account.User))
            throw new ArgumentException("Użytkownik IMAP nie może być pusty.", nameof(account));
        if (maxPerFolder < 0)
            throw new ArgumentOutOfRangeException(nameof(maxPerFolder));
    }
}

sealed class ImapMailboxImporter : IMailboxImporter
{
    readonly Func<string, ImapAccount?> _accountResolver;
    readonly ImapHarvestOperation _harvest;

    public ImapMailboxImporter(
        Func<string, ImapAccount?>? accountResolver = null,
        ImapHarvestOperation? harvest = null)
    {
        _accountResolver = accountResolver ?? (name => ImapAccounts.Load().Find(name));
        _harvest = harvest ?? Imap.HarvestDetailed;
    }

    public MailboxProvider Provider => MailboxProvider.Imap;

    public Task<MailboxImportResult> ImportAsync(
        MailboxImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Source.Provider != Provider)
            throw new ArgumentException("Źródło nie jest kontem IMAP.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() => Import(request, cancellationToken), cancellationToken);
    }

    MailboxImportResult Import(
        MailboxImportRequest request,
        CancellationToken cancellationToken)
    {
        string accountName = ImapMailboxRegistration.AccountName(request.Source.CredentialReference);
        ImapAccount stored = _accountResolver(accountName)
            ?? throw new InvalidOperationException("Chroniona konfiguracja konta IMAP nie istnieje.");
        ImapMailboxSettings settings = ImapMailboxRegistration.ReadSettings(request.Source.SettingsJson);
        var account = new ImapAccount
        {
            Name = accountName,
            Host = settings.Host,
            Port = settings.Port,
            UseSsl = settings.UseSsl,
            User = settings.User,
            PasswordProtected = stored.PasswordProtected,
        };
        if (!string.Equals(
                ImapMailboxRegistration.ExternalKey(account),
                request.Source.ExternalKey,
                StringComparison.Ordinal))
            throw new InvalidOperationException("Migawka konfiguracji IMAP ma nieprawidłową tożsamość.");

        DateTime? since = request.ForceFull ? null : ParseSince(settings.SinceUtc);
        long inserted = 0;
        long updated = 0;
        int processedCount = 0;
        int totalCount = 0;
        string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        ImapHarvestResult harvestResult = _harvest(
            account,
            request.SessionKeyHex,
            since,
            settings.MaxPerFolder,
            _ => request.Progress?.Report(new MailboxImportProgress(
                "listing-folders",
                processedCount,
                totalCount,
                Inserted: inserted,
                Updated: updated)),
            (processed, total) =>
            {
                processedCount = processed;
                totalCount = total;
                request.Progress?.Report(new MailboxImportProgress(
                    "importing",
                    processed,
                    total,
                    inserted,
                    updated));
            },
            batch =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Corpus.Stats stats = Corpus.Upsert(
                    request.Database,
                    batch,
                    stamp,
                    cancellationToken,
                    request.Source.MailboxSourceId);
                inserted += stats.Inserted;
                updated += stats.Updated;
            },
            batchSize: 500,
            cancellationToken: cancellationToken);

        var finalProgress = new MailboxImportProgress(
            "imported",
            harvestResult.Processed,
            Inserted: inserted,
            Updated: updated,
            Errors: harvestResult.Errors);
        request.Progress?.Report(finalProgress);
        return new MailboxImportResult(
            harvestResult.Processed,
            inserted,
            updated,
            Deleted: 0,
            harvestResult.Errors,
            WasFullImport: since is null);
    }

    static DateTime? ParseSince(string? value)
        => value is null
            ? null
            : DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).UtcDateTime;
}
