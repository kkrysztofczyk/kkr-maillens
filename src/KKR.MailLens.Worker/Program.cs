using KKR.MailLens;

SQLitePCL.Batteries_V2.Init();

string? key = Ipc.Request("GETKEY", 2_000);
if (string.IsNullOrWhiteSpace(key) || key == "LOCKED")
{
    Console.Error.WriteLine("LOCKED: odblokuj KKR MailLens GUI.");
    return 2;
}

bool drain = args.Contains("--drain", StringComparer.OrdinalIgnoreCase);
string workerId = $"{Environment.MachineName}:{Environment.ProcessId}";
using var connection = Db.Open(key, create: false);
Db.EnsureSchema(connection);
var store = new EncryptedBlobStore(Paths.BlobsDir, key);

do
{
    if (Ipc.Request("STATUS", 500)?.StartsWith("UNLOCKED ", StringComparison.Ordinal) != true) return 2;
    ProcessingJob? job = ProcessingJobRepository.LeaseNext(connection, workerId, TimeSpan.FromMinutes(5));
    if (job is null) break;
    try
    {
        switch (job.JobType)
        {
            case "download":
                await DownloadAsync(connection, store, job);
                break;
            case "extract":
                if (job.AttachmentId is null || job.DocumentId is null)
                    throw new InvalidDataException("Zadanie ekstrakcji nie wskazuje dokumentu i załącznika.");
                AttachmentExtractionOutcome outcome = AttachmentExtractionProcessor.Process(
                    connection, store, job.AttachmentId.Value, job.DocumentId.Value);
                if (outcome.Status == "needs-ocr" && outcome.DetectedMimeType.StartsWith("image/", StringComparison.Ordinal))
                    ProcessingJobRepository.Enqueue(connection, "ocr", job.AttachmentId.Value, job.DocumentId.Value);
                break;
            case "ocr":
                if (job.AttachmentId is null || job.DocumentId is null)
                    throw new InvalidDataException("Zadanie OCR nie wskazuje dokumentu i załącznika.");
                AppConfig config = AppConfig.Load();
                await OcrAttachmentProcessor.ProcessAsync(connection, store, job.AttachmentId.Value,
                    job.DocumentId.Value, new TesseractOptions(config.TesseractPath, config.OcrLanguages,
                        TimeSpan.FromSeconds(Math.Clamp(config.OcrTimeoutSeconds, 10, 3600))), CancellationToken.None);
                break;
            default:
                throw new NotSupportedException($"Nieobsługiwany typ zadania: {job.JobType}");
        }
        ProcessingJobRepository.Complete(connection, job.Id);
        Console.WriteLine($"completed job={job.Id} type={job.JobType}");
    }
    catch (Exception ex)
    {
        ProcessingJobRepository.Fail(connection, job.Id, ex.GetType().Name, ex.Message, TimeSpan.FromMinutes(1));
        if (job.DocumentId is not null && job.Attempts >= job.MaxAttempts)
            ContentDocumentRepository.MarkFailed(connection, job.DocumentId.Value, ex.GetType().Name, ex.Message);
        Console.Error.WriteLine($"failed job={job.Id}: {ex.GetType().Name}");
        if (!drain) return 1;
    }
}
while (drain);

return 0;

static async Task DownloadAsync(Microsoft.Data.Sqlite.SqliteConnection connection, EncryptedBlobStore store,
    ProcessingJob job)
{
    if (job.AttachmentId is null) throw new InvalidDataException("Zadanie pobierania nie wskazuje załącznika.");
    MailAttachmentRepository.Item item = MailAttachmentRepository.Get(connection, job.AttachmentId.Value);
    if (item.Provider != "gmail") throw new NotSupportedException($"Nieobsługiwany provider: {item.Provider}");
    long accountId = ParseGmailAccountId(item.MailEntryId);
    GmailAccountRecord account = GmailRepository.FindAccount(connection, accountId)
        ?? throw new InvalidOperationException("Konto Gmail nie istnieje.");
    using IGmailApiClient api = await GmailOAuth.CreateApiClientAsync(account, CancellationToken.None);
    var attachment = new GmailAttachmentRecord(item.PartId,
        item.ProviderAttachmentKey.StartsWith("part:", StringComparison.Ordinal) ? "" : item.ProviderAttachmentKey,
        item.InlineBase64Data, item.Filename, item.MimeType, item.SizeBytes, item.ContentId, item.IsInline);
    DownloadedAttachment downloaded = await GmailAttachmentDownloader.DownloadAsync(
        api, item.ProviderMessageKey, attachment, cancellationToken: CancellationToken.None);
    try
    {
        StoredBlob blob = store.Put(connection, downloaded.Bytes);
        MailAttachmentRepository.MarkDownloaded(connection, item.Id, blob, downloaded.DetectedMimeType);
        long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
            connection, item.Id, blob.Sha256, downloaded.DetectedMimeType);
        ProcessingJobRepository.Enqueue(connection, "extract", item.Id, documentId);
    }
    finally
    {
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(downloaded.Bytes);
    }
}

static long ParseGmailAccountId(string entryId)
{
    string[] parts = entryId.Split(':', 3);
    if (parts.Length != 3 || parts[0] != "gmail" || !long.TryParse(parts[1], out long id))
        throw new InvalidDataException("Nieprawidłowy identyfikator wiadomości Gmail.");
    return id;
}
