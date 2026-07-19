using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            TryKill(process);
            string error = await DiagnosticAsync(errorTask).ConfigureAwait(false);
            string detail = error.Length == 0 ? ex.Message : error;
            throw new InvalidOperationException($"Tesseract przerwał strumień wejściowy: {Limit(detail)}", ex);
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
            // Tesseract pisze tekst w UTF-8; domyślne dekodowanie w kodowaniu ANSI
            // zamieniałoby polskie znaki na mojibake.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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

    static async Task<string> DiagnosticAsync(Task<string> errorTask)
    {
        try { return await errorTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { return ""; }
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
        long attachmentId, long documentId, TesseractOptions options, CancellationToken cancellationToken,
        IPdfPageRenderer? pdfRenderer = null, PdfRenderOptions? pdfOptions = null,
        Action? heartbeat = null, PaddleOcrOptions? fallbackOptions = null)
    {
        MailAttachmentRepository.Item item = MailAttachmentRepository.Get(connection, attachmentId);
        StoredBlob blob = EncryptedBlobStore.Get(connection,
            item.BlobId ?? throw new InvalidOperationException("Załącznik nie ma pobranego blobu."));
        byte[] plaintext = store.Read(blob);
        try
        {
            DetectedFile detected = FileTypeDetector.Detect(item.Filename, item.MimeType, plaintext);
            var engine = new TesseractOcrEngine(options);
            using PaddleOcrEngine? fallback = fallbackOptions is null ? null : new PaddleOcrEngine(fallbackOptions);
            if (detected.MimeType == "application/pdf")
            {
                OcrRun result = await ExtractPdfAsync(detected, engine, fallback,
                    pdfRenderer ?? new PdfiumPageRenderer(), pdfOptions ?? new PdfRenderOptions(),
                    cancellationToken, heartbeat).ConfigureAwait(false);
                ContentDocumentRepository.SaveExtraction(connection, documentId, result.Result,
                    result.UsedFallback ? "pdfpig+tesseract+paddleocr-fallback" : "pdfpig+tesseract", "1",
                    modelName: result.UsedFallback ? $"{options.Languages};{fallback!.ModelName}" : options.Languages,
                    ocrCompleted: true);
            }
            else
            {
                OcrRun result = await ExtractWithFallbackAsync(engine, fallback, plaintext,
                    detected.MimeType, cancellationToken).ConfigureAwait(false);
                heartbeat?.Invoke();
                ContentDocumentRepository.SaveExtraction(connection, documentId, result.Result,
                    result.UsedFallback ? "paddleocr-fallback" : "tesseract", "cli-v1",
                    documentKind: "ocr",
                    modelName: result.UsedFallback ? fallback!.ModelName : options.Languages,
                    ocrCompleted: true);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    static async Task<OcrRun> ExtractPdfAsync(DetectedFile detected, TesseractOcrEngine engine,
        PaddleOcrEngine? fallback, IPdfPageRenderer renderer, PdfRenderOptions renderOptions,
        CancellationToken cancellationToken, Action? heartbeat)
    {
        renderOptions.Validate();
        ExtractionResult textResult = new PdfTextExtractor().Extract(detected, new TextExtractionOptions());
        int[] pagesToOcr = textResult.OcrPageNumbers.Distinct().Order().ToArray();
        if (pagesToOcr.Length == 0) return new OcrRun(textResult, false);
        if (pagesToOcr.Length > renderOptions.MaxPages)
            throw new InvalidDataException(
                $"Dokument wymaga OCR dla {pagesToOcr.Length} stron; limit wynosi {renderOptions.MaxPages}.");

        var ocrSegments = new List<SegmentDraft>(pagesToOcr.Length);
        bool usedFallback = false;
        foreach (int[] batch in pagesToOcr.Chunk(renderOptions.BatchSize))
        {
            heartbeat?.Invoke();
            IReadOnlyList<RenderedPdfPage> rendered = await renderer.RenderAsync(detected.Content,
                batch, renderOptions, cancellationToken).ConfigureAwait(false);
            try
            {
                if (rendered.Count != batch.Length
                    || !rendered.Select(page => page.PageNumber).SequenceEqual(batch))
                    throw new InvalidDataException(
                        $"Renderer PDF nie zwrócił oczekiwanego batcha stron: {string.Join(',', batch)}.");

                foreach (RenderedPdfPage page in rendered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        OcrRun pageResult = await ExtractWithFallbackAsync(engine, fallback,
                            page.PngBytes, "image/png", cancellationToken).ConfigureAwait(false);
                        usedFallback |= pageResult.UsedFallback;
                        if (pageResult.Result.CleanText.Length > 0)
                            ocrSegments.Add(new SegmentDraft(pageResult.Result.RawText, PageNumber: page.PageNumber));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(page.PngBytes);
                    }
                    heartbeat?.Invoke();
                }
            }
            finally
            {
                Zero(rendered);
            }
        }

        var missing = pagesToOcr.ToHashSet();
        IEnumerable<SegmentDraft> textSegments = textResult.Segments
            .Where(segment => segment.PageNumber is null || !missing.Contains(segment.PageNumber.Value))
            .Select(segment => new SegmentDraft(segment.RawText, segment.PageNumber));
        ExtractionResult merged = ExtractionResultBuilder.Build(detected.MimeType,
            textSegments.Concat(ocrSegments).OrderBy(segment => segment.PageNumber),
            new TextExtractionOptions());
        return new OcrRun(merged with
        {
            WasTruncated = merged.WasTruncated || textResult.WasTruncated,
            OcrPageNumbers = [],
        }, usedFallback);
    }

    static async Task<OcrRun> ExtractWithFallbackAsync(TesseractOcrEngine primary,
        PaddleOcrEngine? fallback, byte[] image, string mimeType, CancellationToken cancellationToken)
    {
        ExtractionResult primaryResult = await primary.ExtractAsync(image, mimeType, cancellationToken)
            .ConfigureAwait(false);
        if (primaryResult.CleanText.Length > 0 || fallback is null)
            return new OcrRun(primaryResult, false);

        ExtractionResult fallbackResult;
        try
        {
            fallbackResult = await fallback.ExtractAsync(image, mimeType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fallback jest opcjonalnym ulepszeniem — jego awaria nie może unieważnić
            // poprawnie zakończonego (pustego) wyniku Tesseracta.
            Trace.TraceWarning("PaddleOCR fallback nie powiódł się; zachowano wynik Tesseracta: {0}", ex.Message);
            return new OcrRun(primaryResult, false);
        }
        return fallbackResult.CleanText.Length == 0
            ? new OcrRun(primaryResult, false)
            : new OcrRun(fallbackResult, true);
    }

    static void Zero(IEnumerable<RenderedPdfPage> pages)
    {
        foreach (RenderedPdfPage page in pages)
            CryptographicOperations.ZeroMemory(page.PngBytes);
    }

    sealed record OcrRun(ExtractionResult Result, bool UsedFallback);
}
