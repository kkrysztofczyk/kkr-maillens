using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KKR.MailLens;

delegate int OutlookHarvestOperation(
    string storeId,
    DateTime? from,
    int maxPerFolder,
    Action<string> onFolder,
    Action<int, int>? onProgress,
    Action<List<HarvestedMail>> flush,
    string[]? includeFolders,
    int batchSize,
    CancellationToken cancellationToken);

sealed record OutlookMailboxSettings(
    string StoreId,
    OutlookStoreKind StoreKind,
    string? FilePath,
    int MaxPerFolder = 5000,
    string[]? IncludeFolders = null,
    string? SinceUtc = null);

static class OutlookMailboxRegistration
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static MailboxSourceRecord Register(
        Microsoft.Data.Sqlite.SqliteConnection database,
        OutlookStoreInfo store,
        int maxPerFolder = 5000,
        string[]? includeFolders = null,
        DateTime? sinceUtc = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (maxPerFolder < 0)
            throw new ArgumentOutOfRangeException(nameof(maxPerFolder));
        string[]? normalizedFolders = NormalizeFolders(includeFolders);
        var settings = new OutlookMailboxSettings(
            store.StoreId,
            store.Kind,
            store.FilePath,
            maxPerFolder,
            normalizedFolders,
            sinceUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return MailboxSourceRepository.Upsert(database, new MailboxSourceDefinition(
            MailboxProvider.Outlook,
            ExternalKey(store.StoreId),
            store.DisplayName,
            SettingsJson: JsonSerializer.Serialize(settings, Json)));
    }

    public static OutlookMailboxSettings ReadSettings(string settingsJson)
    {
        OutlookMailboxSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<OutlookMailboxSettings>(settingsJson, Json)
                ?? throw new JsonException();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Nieprawidłowe ustawienia magazynu Outlook.", ex);
        }
        if (string.IsNullOrWhiteSpace(settings.StoreId))
            throw new InvalidDataException("Brak identyfikatora magazynu Outlook.");
        if (!Enum.IsDefined(settings.StoreKind))
            throw new InvalidDataException("Nieprawidłowy typ magazynu Outlook.");
        if (settings.MaxPerFolder < 0)
            throw new InvalidDataException("Nieprawidłowy limit wiadomości magazynu Outlook.");
        if (settings.SinceUtc is not null
            && !DateTimeOffset.TryParse(
                settings.SinceUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
            throw new InvalidDataException("Nieprawidłowa data początkowa magazynu Outlook.");
        return settings with { IncludeFolders = NormalizeFolders(settings.IncludeFolders) };
    }

    public static string ExternalKey(string storeId)
    {
        if (string.IsNullOrWhiteSpace(storeId))
            throw new ArgumentException("Brak identyfikatora magazynu Outlook.", nameof(storeId));
        byte[] bytes = Encoding.UTF8.GetBytes(storeId.Trim().ToUpperInvariant());
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    static string[]? NormalizeFolders(IEnumerable<string>? folders)
    {
        string[] result = folders?
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => folder.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        return result.Length == 0 ? null : result;
    }
}

sealed class OutlookMailboxImporter : IMailboxImporter
{
    readonly OutlookHarvestOperation _harvest;

    public OutlookMailboxImporter(OutlookHarvestOperation? harvest = null)
    {
        _harvest = harvest ?? Harvest;
    }

    public MailboxProvider Provider => MailboxProvider.Outlook;

    public Task<MailboxImportResult> ImportAsync(
        MailboxImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Source.Provider != Provider)
            throw new ArgumentException("Źródło nie jest magazynem Outlook.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Import(request, cancellationToken), cancellationToken);
    }

    MailboxImportResult Import(
        MailboxImportRequest request,
        CancellationToken cancellationToken)
    {
        OutlookMailboxSettings settings =
            OutlookMailboxRegistration.ReadSettings(request.Source.SettingsJson);
        if (!string.Equals(
                OutlookMailboxRegistration.ExternalKey(settings.StoreId),
                request.Source.ExternalKey,
                StringComparison.Ordinal))
            throw new InvalidOperationException("Migawka magazynu Outlook ma nieprawidłową tożsamość.");

        DateTime? since = request.ForceFull ? null : ParseSince(settings.SinceUtc);
        long inserted = 0;
        long updated = 0;
        int processedCount = 0;
        int totalCount = 0;
        string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        int processed = _harvest(
            settings.StoreId,
            since,
            settings.MaxPerFolder,
            _ => request.Progress?.Report(new MailboxImportProgress(
                "listing-folders",
                processedCount,
                totalCount,
                Inserted: inserted,
                Updated: updated)),
            (current, total) =>
            {
                processedCount = current;
                totalCount = total;
                request.Progress?.Report(new MailboxImportProgress(
                    "importing",
                    current,
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
            settings.IncludeFolders,
            batchSize: 500,
            cancellationToken);

        request.Progress?.Report(new MailboxImportProgress(
            "imported",
            processed,
            Inserted: inserted,
            Updated: updated));
        return new MailboxImportResult(
            processed,
            inserted,
            updated,
            Deleted: 0,
            Errors: 0,
            WasFullImport: since is null);
    }

    static int Harvest(
        string storeId,
        DateTime? from,
        int maxPerFolder,
        Action<string> onFolder,
        Action<int, int>? onProgress,
        Action<List<HarvestedMail>> flush,
        string[]? includeFolders,
        int batchSize,
        CancellationToken cancellationToken)
    {
        using var outlook = new Outlook();
        return outlook.HarvestMail(
            null,
            from,
            maxPerFolder,
            onFolder,
            onProgress,
            flush,
            includeFolders,
            batchSize,
            cancellationToken,
            exactStoreId: storeId);
    }

    static DateTime? ParseSince(string? value)
        => value is null
            ? null
            : DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).UtcDateTime;
}
