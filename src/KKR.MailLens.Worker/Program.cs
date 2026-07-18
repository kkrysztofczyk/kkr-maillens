using KKR.MailLens;

SQLitePCL.Batteries_V2.Init();

if (OperatingSystem.IsWindows() && !RestrictedWorkerProcess.IsCurrentProcessRestricted())
{
    Console.Error.WriteLine("Worker wymaga uruchomienia przez ograniczony launcher KKR MailLens.");
    return 4;
}

string? key = Ipc.Request("GETKEY", 2_000);
if (string.IsNullOrWhiteSpace(key) || key == "LOCKED")
{
    Console.Error.WriteLine("LOCKED: odblokuj KKR MailLens GUI.");
    return 2;
}

bool drain = args.Contains("--drain", StringComparer.OrdinalIgnoreCase);
string workerId = $"{Environment.MachineName}:{Environment.ProcessId}";
TimeSpan leaseDuration = TimeSpan.FromMinutes(5);
using var connection = Db.Open(key, create: false);
Db.EnsureSchema(connection);
var store = new EncryptedBlobStore(Paths.BlobsDir, key);
using var outlookBroker = new OutlookAttachmentBroker();
using var shutdown = new CancellationTokenSource();
int sessionLocked = 0;
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += cancelHandler;
Task sessionMonitor = MonitorSessionAsync(shutdown, () => Interlocked.Exchange(ref sessionLocked, 1));

try
{
    do
    {
        if (shutdown.IsCancellationRequested)
            return Volatile.Read(ref sessionLocked) == 1 ? 2 : 130;
        ProcessingJob? job = ProcessingJobRepository.LeaseNext(connection, workerId, leaseDuration);
        if (job is null) break;
        try
        {
            switch (job.JobType)
            {
                case "download":
                    await DownloadAsync(connection, store, outlookBroker, job, key, shutdown.Token);
                    break;
                case "extract":
                    if (job.AttachmentId is null || job.DocumentId is null)
                        throw new InvalidDataException("Zadanie ekstrakcji nie wskazuje dokumentu i załącznika.");
                    AttachmentExtractionOutcome outcome = AttachmentExtractionProcessor.Process(
                        connection, store, job.AttachmentId.Value, job.DocumentId.Value);
                    if (outcome.Status == "needs-ocr" && (outcome.DetectedMimeType == "application/pdf"
                        || outcome.DetectedMimeType.StartsWith("image/", StringComparison.Ordinal)))
                        ProcessingJobRepository.Enqueue(connection, "ocr", job.AttachmentId.Value, job.DocumentId.Value);
                    else if (outcome.Status == "needs-transcription" && MediaTypes.IsMedia(outcome.DetectedMimeType))
                        ProcessingJobRepository.Enqueue(connection, "transcribe", job.AttachmentId.Value, job.DocumentId.Value);
                    break;
                case "ocr":
                    if (job.AttachmentId is null || job.DocumentId is null)
                        throw new InvalidDataException("Zadanie OCR nie wskazuje dokumentu i załącznika.");
                    AppConfig config = AppConfig.Load();
                    int ocrTimeoutSeconds = Math.Clamp(config.OcrTimeoutSeconds, 10, 3600);
                    int renderTimeoutSeconds = Math.Clamp(config.OcrPdfRenderTimeoutSeconds, 10, 3600);
                    TimeSpan ocrLeaseDuration = TimeSpan.FromSeconds(
                        Math.Max(ocrTimeoutSeconds, renderTimeoutSeconds) + 60);
                    if (!ProcessingJobRepository.RenewLease(connection, job.Id, workerId, ocrLeaseDuration))
                        throw new InvalidOperationException("Worker utracił lease zadania OCR.");
                    await OcrAttachmentProcessor.ProcessAsync(connection, store, job.AttachmentId.Value,
                        job.DocumentId.Value, new TesseractOptions(config.TesseractPath, config.OcrLanguages,
                            TimeSpan.FromSeconds(ocrTimeoutSeconds)), shutdown.Token,
                        pdfOptions: new PdfRenderOptions(
                            Math.Clamp(config.OcrPdfDpi, 72, 600),
                            Math.Clamp(config.OcrMaxPdfPages, 1, 10_000),
                            TimeSpan.FromSeconds(renderTimeoutSeconds),
                            Math.Clamp(config.OcrPdfBatchSize, 1, 16)),
                        heartbeat: () =>
                        {
                            shutdown.Token.ThrowIfCancellationRequested();
                            if (!ProcessingJobRepository.RenewLease(connection, job.Id, workerId, ocrLeaseDuration))
                                throw new InvalidOperationException("Worker utracił lease zadania OCR.");
                    });
                    break;
                case "transcribe":
                    if (job.AttachmentId is null || job.DocumentId is null)
                        throw new InvalidDataException("Zadanie transkrypcji nie wskazuje dokumentu i załącznika.");
                    AppConfig transcriptionConfig = AppConfig.Load();
                    int ffmpegTimeoutSeconds = Math.Clamp(transcriptionConfig.FfmpegTimeoutSeconds, 10, 3600);
                    int whisperTimeoutSeconds = Math.Clamp(transcriptionConfig.WhisperTimeoutSeconds, 30, 24 * 3600);
                    TimeSpan transcriptionLeaseDuration = TimeSpan.FromSeconds(
                        ffmpegTimeoutSeconds + whisperTimeoutSeconds + 60);
                    if (!ProcessingJobRepository.RenewLease(connection, job.Id, workerId, transcriptionLeaseDuration))
                        throw new InvalidOperationException("Worker utracił lease zadania transkrypcji.");
                    var transcriptionOptions = new MediaTranscriptionOptions(
                        transcriptionConfig.FfmpegPath, transcriptionConfig.WhisperPath,
                        transcriptionConfig.WhisperModelPath, transcriptionConfig.WhisperLanguage,
                        TimeSpan.FromSeconds(ffmpegTimeoutSeconds), TimeSpan.FromSeconds(whisperTimeoutSeconds),
                        Math.Clamp(transcriptionConfig.TranscriptionMaxMinutes, 1, 24 * 60));
                    await MediaTranscriptionProcessor.ProcessAsync(connection, store, job.AttachmentId.Value,
                        job.DocumentId.Value, new FfmpegWhisperTranscriber(transcriptionOptions), shutdown.Token,
                        heartbeat: () =>
                        {
                            shutdown.Token.ThrowIfCancellationRequested();
                            if (!ProcessingJobRepository.RenewLease(connection, job.Id, workerId, transcriptionLeaseDuration))
                                throw new InvalidOperationException("Worker utracił lease zadania transkrypcji.");
                        });
                    break;
                default:
                    throw new NotSupportedException($"Nieobsługiwany typ zadania: {job.JobType}");
            }
            shutdown.Token.ThrowIfCancellationRequested();
            if (!ProcessingJobRepository.Complete(connection, job.Id, workerId))
            {
                Console.Error.WriteLine($"lost lease job={job.Id} type={job.JobType}");
                return 3;
            }
            Console.WriteLine($"completed job={job.Id} type={job.JobType}");
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            if (!ProcessingJobRepository.Abandon(connection, job.Id, workerId))
            {
                Console.Error.WriteLine($"lost lease job={job.Id} type={job.JobType}");
                return 3;
            }
            Console.Error.WriteLine($"cancelled job={job.Id} type={job.JobType}");
            return Volatile.Read(ref sessionLocked) == 1 ? 2 : 130;
        }
        catch (Exception ex)
        {
            if (!ProcessingJobRepository.Fail(connection, job.Id, workerId, ex.GetType().Name,
                ex.Message, TimeSpan.FromMinutes(1)))
            {
                Console.Error.WriteLine($"lost lease job={job.Id} type={job.JobType}");
                return 3;
            }
            if (job.DocumentId is not null && job.Attempts >= job.MaxAttempts)
                ContentDocumentRepository.MarkFailed(connection, job.DocumentId.Value, ex.GetType().Name, ex.Message);
            Console.Error.WriteLine($"failed job={job.Id}: {ex.GetType().Name}");
            if (!drain) return 1;
        }
    }
    while (drain);

    return 0;
}
finally
{
    shutdown.Cancel();
    await sessionMonitor.ConfigureAwait(false);
    Console.CancelKeyPress -= cancelHandler;
}

static async Task DownloadAsync(Microsoft.Data.Sqlite.SqliteConnection connection, EncryptedBlobStore store,
    OutlookAttachmentBroker outlookBroker, ProcessingJob job, string sessionKeyHex,
    CancellationToken cancellationToken)
{
    if (job.AttachmentId is null) throw new InvalidDataException("Zadanie pobierania nie wskazuje załącznika.");
    MailAttachmentRepository.Item item = MailAttachmentRepository.Get(connection, job.AttachmentId.Value);
    DownloadedAttachment downloaded = item.Provider switch
    {
        "gmail" => await DownloadGmailAsync(connection, item, sessionKeyHex, cancellationToken),
        "imap" => await DownloadImapAsync(item, sessionKeyHex, cancellationToken),
        "outlook" => outlookBroker.Download(item, cancellationToken: cancellationToken),
        _ => throw new NotSupportedException($"Nieobsługiwany provider: {item.Provider}"),
    };
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

static async Task<DownloadedAttachment> DownloadGmailAsync(
    Microsoft.Data.Sqlite.SqliteConnection connection, MailAttachmentRepository.Item item,
    string sessionKeyHex, CancellationToken cancellationToken)
{
    long accountId = ParseGmailAccountId(item.MailEntryId);
    GmailAccountRecord account = GmailRepository.FindAccount(connection, accountId)
        ?? throw new InvalidOperationException("Konto Gmail nie istnieje.");
    using IGmailApiClient api = await GmailOAuth.CreateApiClientAsync(
        account, sessionKeyHex, cancellationToken);
    var attachment = new GmailAttachmentRecord(item.PartId,
        item.ProviderAttachmentKey.StartsWith("part:", StringComparison.Ordinal) ? "" : item.ProviderAttachmentKey,
        item.InlineBase64Data, item.Filename, item.MimeType, item.SizeBytes, item.ContentId, item.IsInline);
    DownloadedAttachment downloaded = await GmailAttachmentDownloader.DownloadAsync(
        api, item.ProviderMessageKey, attachment, cancellationToken: cancellationToken);
    return downloaded;
}

static async Task<DownloadedAttachment> DownloadImapAsync(MailAttachmentRepository.Item item,
    string sessionKeyHex, CancellationToken cancellationToken)
{
    ImapMessageLocator locator = ImapMessageLocator.Decode(item.ProviderMessageKey);
    ImapAccount account = ImapAccounts.Load().Find(locator.AccountName)
        ?? throw new InvalidOperationException("Konto IMAP nie istnieje w lokalnej konfiguracji.");
    return await ImapAttachmentDownloader.DownloadAsync(account, sessionKeyHex, item,
        cancellationToken: cancellationToken);
}

static async Task MonitorSessionAsync(CancellationTokenSource shutdown, Action onLocked)
{
    int missedResponses = 0;
    try
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), shutdown.Token).ConfigureAwait(false);
            string? status = Ipc.Request("STATUS", 500);
            if (status?.StartsWith("UNLOCKED ", StringComparison.Ordinal) == true)
            {
                missedResponses = 0;
                continue;
            }
            if (status?.StartsWith("LOCKED", StringComparison.Ordinal) == true || ++missedResponses >= 3)
            {
                onLocked();
                shutdown.Cancel();
                return;
            }
        }
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
}

static long ParseGmailAccountId(string entryId)
{
    string[] parts = entryId.Split(':', 3);
    if (parts.Length != 3 || parts[0] != "gmail" || !long.TryParse(parts[1], out long id))
        throw new InvalidDataException("Nieprawidłowy identyfikator wiadomości Gmail.");
    return id;
}
