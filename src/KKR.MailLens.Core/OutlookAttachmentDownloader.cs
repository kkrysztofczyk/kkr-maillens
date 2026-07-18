using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace KKR.MailLens;

sealed record OutlookMessageLocator(string StoreId, string EntryId)
{
    const int CurrentVersion = 1;
    sealed record Payload(int Version, string StoreId, string EntryId);

    public string Encode()
    {
        if (string.IsNullOrWhiteSpace(StoreId) || string.IsNullOrWhiteSpace(EntryId))
            throw new InvalidDataException("Brak identyfikatora wiadomości Outlook.");
        return JsonSerializer.Serialize(new Payload(CurrentVersion, StoreId, EntryId));
    }

    public static OutlookMessageLocator Decode(string value)
    {
        Payload payload;
        try { payload = JsonSerializer.Deserialize<Payload>(value) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("Nieprawidłowy identyfikator wiadomości Outlook.", ex); }
        if (payload.Version != CurrentVersion || string.IsNullOrWhiteSpace(payload.StoreId)
            || string.IsNullOrWhiteSpace(payload.EntryId))
            throw new InvalidDataException("Nieprawidłowy identyfikator wiadomości Outlook.");
        return new OutlookMessageLocator(payload.StoreId, payload.EntryId);
    }
}

sealed class OutlookAttachmentBroker : IDisposable
{
    Outlook? _outlook;

    public DownloadedAttachment Download(MailAttachmentRepository.Item attachment,
        long maximumBytes = GmailAttachmentDownloader.DefaultMaximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (attachment.SizeBytes > maximumBytes)
            throw new InvalidDataException("Załącznik przekracza dozwolony limit rozmiaru.");
        OutlookMessageLocator locator = OutlookMessageLocator.Decode(attachment.ProviderMessageKey);
        if (!int.TryParse(attachment.PartId, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            || index <= 0)
            throw new InvalidDataException("Nieprawidłowy indeks załącznika Outlook.");
        cancellationToken.ThrowIfCancellationRequested();

        var workspace = OutlookAttachmentWorkspace.Create(Paths.TempDir);
        byte[]? bytes = null;
        bool cleaned = false;
        try
        {
            string extension = Path.GetExtension(Path.GetFileName(attachment.Filename));
            bool safeExtension = extension.Length is > 1 and <= 16 && extension[0] == '.'
                && extension.Skip(1).All(char.IsLetterOrDigit);
            string path = Path.Combine(workspace.DirectoryPath, "attachment" +
                (safeExtension ? extension : ".bin"));
            (_outlook ??= new Outlook()).SaveAttachment(locator.StoreId, locator.EntryId, index, path, maximumBytes);
            cancellationToken.ThrowIfCancellationRequested();
            long length = new FileInfo(path).Length;
            if (length <= 0) throw new InvalidDataException("Załącznik Outlook jest pusty.");
            if (length > maximumBytes || length > int.MaxValue)
                throw new InvalidDataException("Pobrany załącznik przekracza dozwolony limit rozmiaru.");
            if (attachment.SizeBytes > 0 && length != attachment.SizeBytes)
                throw new InvalidDataException("Rozmiar pobranego załącznika nie zgadza się z metadanymi Outlook.");

            bytes = File.ReadAllBytes(path);
            cancellationToken.ThrowIfCancellationRequested();
            string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string detectedMimeType = FileTypeDetector.Detect(attachment.Filename, attachment.MimeType, bytes).MimeType;
            var result = new DownloadedAttachment(bytes, sha256, detectedMimeType);
            workspace.Dispose();
            cleaned = true;
            return result;
        }
        catch (Exception processingError)
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (!cleaned)
            {
                try { workspace.Dispose(); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("Nie udało się usunąć jawnego pliku roboczego Outlook.",
                        new AggregateException(processingError, cleanupError));
                }
            }
            throw;
        }
    }

    public void Dispose() => _outlook?.Dispose();
}

sealed class OutlookAttachmentWorkspace : IDisposable
{
    FileStream? _lockFile;
    public string DirectoryPath { get; }

    OutlookAttachmentWorkspace(string directoryPath, FileStream lockFile)
    { DirectoryPath = directoryPath; _lockFile = lockFile; }

    public static OutlookAttachmentWorkspace Create(string rootDirectory)
    {
        string root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
        CleanupOrphans(root);
        string directory = Path.Combine(root, "outlook-attachment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var heldLock = new FileStream(Path.Combine(directory, ".lock"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        return new OutlookAttachmentWorkspace(directory, heldLock);
    }

    static void CleanupOrphans(string rootDirectory)
    {
        foreach (string directory in Directory.EnumerateDirectories(rootDirectory, "outlook-attachment-*"))
        {
            string lockPath = Path.Combine(directory, ".lock");
            try
            {
                using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        _lockFile?.Dispose();
        _lockFile = null;
        if (!Directory.Exists(DirectoryPath)) return;
        try { Directory.Delete(DirectoryPath, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Nie udało się usunąć jawnego pliku roboczego Outlook.", ex);
        }
    }
}
