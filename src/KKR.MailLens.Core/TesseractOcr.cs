using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed record TesseractOptions(
    string ExecutablePath,
    string Languages = "pol+eng",
    TimeSpan? Timeout = null,
    int PageSegmentationMode = 6)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes(2);
}

sealed class TesseractOcrEngine
{
    readonly TesseractOptions options;

    public TesseractOcrEngine(TesseractOptions options)
    {
        this.options = options;
        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
            throw new ArgumentException("Brak ścieżki do Tesseracta.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Languages) || options.Languages.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '_' or '.')))
            throw new ArgumentException("Nieprawidłowa lista języków OCR.", nameof(options));
        if (options.EffectiveTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.PageSegmentationMode is < 0 or > 13) throw new ArgumentOutOfRangeException(nameof(options));
    }

    public async Task<ExtractionResult> ExtractAsync(byte[] image, string mimeType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length == 0) throw new InvalidDataException("Obraz OCR jest pusty.");
        if (!mimeType.StartsWith("image/", StringComparison.Ordinal))
            throw new NotSupportedException($"OCR obrazu nie obsługuje typu {mimeType}.");

        using Process process = StartProcess();
        using var timeout = new CancellationTokenSource(options.EffectiveTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(image, linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Tesseract zakończył się kodem {process.ExitCode}: {Limit(error)}");
            string clean = TextNormalizer.Normalize(output);
            return new ExtractionResult(mimeType, output, clean, false,
                clean.Length == 0 ? [] : [new ExtractedSegment(0, output, clean)]);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Tesseract przekroczył limit {options.EffectiveTimeout.TotalSeconds:0} s.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    Process StartProcess()
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetExtension(options.ExecutablePath).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(options.ExecutablePath).Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(options.ExecutablePath);
        }
        else
        {
            start.FileName = options.ExecutablePath;
        }
        start.ArgumentList.Add("stdin");
        start.ArgumentList.Add("stdout");
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add(options.Languages);
        start.ArgumentList.Add("--psm");
        start.ArgumentList.Add(options.PageSegmentationMode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("txt");
        return Process.Start(start) ?? throw new InvalidOperationException("Nie udało się uruchomić Tesseracta.");
    }

    static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    static string Limit(string value)
    {
        string clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 500 ? clean : clean[..500];
    }
}

static class OcrAttachmentProcessor
{
    public static async Task ProcessAsync(SqliteConnection connection, EncryptedBlobStore store,
        long attachmentId, long documentId, TesseractOptions options, CancellationToken cancellationToken)
    {
        MailAttachmentRepository.Item item = MailAttachmentRepository.Get(connection, attachmentId);
        StoredBlob blob = EncryptedBlobStore.Get(connection,
            item.BlobId ?? throw new InvalidOperationException("Załącznik nie ma pobranego blobu."));
        byte[] plaintext = store.Read(blob);
        try
        {
            DetectedFile detected = FileTypeDetector.Detect(item.Filename, item.MimeType, plaintext);
            var engine = new TesseractOcrEngine(options);
            ExtractionResult result = await engine.ExtractAsync(plaintext, detected.MimeType, cancellationToken)
                .ConfigureAwait(false);
            if (result.CleanText.Length == 0)
                throw new InvalidDataException("Tesseract nie zwrócił użytecznego tekstu.");
            ContentDocumentRepository.SaveExtraction(connection, documentId, result, "tesseract", "cli-v1",
                documentKind: "ocr", modelName: options.Languages);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
