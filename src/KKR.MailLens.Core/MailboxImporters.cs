using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class MailboxImportRequest
{
    public MailboxImportRequest(
        SqliteConnection database,
        string sessionKeyHex,
        MailboxImportSourceRecord source,
        bool forceFull,
        IProgress<MailboxImportProgress>? progress = null)
    {
        Database = database ?? throw new ArgumentNullException(nameof(database));
        if (string.IsNullOrWhiteSpace(sessionKeyHex))
            throw new ArgumentException("Brak klucza aktywnej sesji.", nameof(sessionKeyHex));
        SessionKeyHex = sessionKeyHex;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ForceFull = forceFull;
        Progress = progress;
    }

    public SqliteConnection Database { get; }
    public string SessionKeyHex { get; }
    public MailboxImportSourceRecord Source { get; }
    public bool ForceFull { get; }
    public IProgress<MailboxImportProgress>? Progress { get; }

    public override string ToString()
        => $"MailboxImportRequest {{ Source = {Source.Id}, Provider = {Source.Provider}, ForceFull = {ForceFull} }}";
}

sealed record MailboxImportResult(
    long Processed,
    long Inserted,
    long Updated,
    long Deleted,
    long Errors,
    bool WasFullImport);

interface IMailboxImporter
{
    MailboxProvider Provider { get; }

    Task<MailboxImportResult> ImportAsync(
        MailboxImportRequest request,
        CancellationToken cancellationToken);
}

sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
