using System.Globalization;

namespace KKR.MailLens;

/// <summary>
/// Parsowanie opcji komendy `config` na AppConfig - wydzielone z Cli, by walidacje dalo sie testowac.
/// Kontrakt: zla wartosc (nie-liczba, poza zakresem, nie-bool) trafia do Errors i NIE zmienia configu -
/// wywolujacy nie zapisuje przy bledach. Ciche pomijanie zlych wartosci maskowalo literowki (--max abc)
/// i przepuszczalo ujemne limity (--max -5 dzialalo jak "bez limitu").
/// </summary>
static class ConfigOptions
{
    public sealed record Result(bool Changed, IReadOnlyList<string> Errors);

    public static Result Apply(AppConfig cfg, string[] args)
    {
        bool changed = false;
        var errors = new List<string>();

        string? Str(string name) => Args.Str(args, name);

        int? Int(string name, int min, int max)
        {
            if (Args.Str(args, name) is not { } raw) return null;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            { errors.Add($"Opcja {name}: '{raw}' nie jest liczbą całkowitą."); return null; }
            if (value < min || value > max)
            { errors.Add($"Opcja {name}: {value} poza zakresem {min}..{max}."); return null; }
            return value;
        }

        bool? Bool(string name)
        {
            if (Args.Str(args, name) is not { } raw) return null;
            if (!bool.TryParse(raw, out bool value))
            { errors.Add($"Opcja {name}: '{raw}' nie jest wartością true|false."); return null; }
            return value;
        }

        double? Dbl(string name, double min, double max)
        {
            if (Args.Str(args, name) is not { } raw) return null;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            { errors.Add($"Opcja {name}: '{raw}' nie jest liczbą."); return null; }
            if (value < min || value > max)
            { errors.Add($"Opcja {name}: {value} poza zakresem {min}..{max}."); return null; }
            return value;
        }

        if (Str("--store") is { } store) { cfg.StoreFilter = store.Trim(); changed = true; }
        if (Int("--max", 0, 1_000_000) is { } max) { cfg.MaxPerFolder = max; changed = true; } // 0 = bez limitu; ujemne odrzucamy
        if (Str("--tesseract") is { } tesseract) { cfg.TesseractPath = tesseract.Trim(); changed = true; }
        if (Str("--ocr-languages") is { } languages) { cfg.OcrLanguages = languages.Trim(); changed = true; }
        if (Int("--ocr-timeout", 10, 3600) is { } ocrTimeout) { cfg.OcrTimeoutSeconds = ocrTimeout; changed = true; }
        if (Int("--ocr-pdf-dpi", 72, 600) is { } dpi) { cfg.OcrPdfDpi = dpi; changed = true; }
        if (Int("--ocr-max-pdf-pages", 1, 10_000) is { } pages) { cfg.OcrMaxPdfPages = pages; changed = true; }
        if (Int("--ocr-pdf-render-timeout", 10, 3600) is { } renderTimeout)
        { cfg.OcrPdfRenderTimeoutSeconds = renderTimeout; changed = true; }
        if (Int("--ocr-pdf-batch-size", 1, 16) is { } batchPages) { cfg.OcrPdfBatchSize = batchPages; changed = true; }
        if (Bool("--paddleocr-enabled") is { } usePaddle) { cfg.PaddleOcrEnabled = usePaddle; changed = true; }
        if (Str("--paddleocr-python") is { } paddlePython) { cfg.PaddleOcrPythonPath = paddlePython.Trim(); changed = true; }
        if (Str("--paddleocr-runner") is { } paddleRunner) { cfg.PaddleOcrRunnerPath = paddleRunner.Trim(); changed = true; }
        if (Str("--paddleocr-language") is { } paddleLanguage) { cfg.PaddleOcrLanguage = paddleLanguage.Trim(); changed = true; }
        if (Str("--paddleocr-version") is { } paddleVersion) { cfg.PaddleOcrModelVersion = paddleVersion.Trim(); changed = true; }
        if (Str("--paddleocr-device") is { } paddleDevice) { cfg.PaddleOcrDevice = paddleDevice.Trim(); changed = true; }
        if (Dbl("--paddleocr-min-confidence", 0, 1) is { } minimumConfidence)
        { cfg.PaddleOcrMinimumConfidence = minimumConfidence; changed = true; }
        if (Int("--paddleocr-timeout", 10, 3600) is { } paddleSeconds) { cfg.PaddleOcrTimeoutSeconds = paddleSeconds; changed = true; }
        if (Int("--worker-memory-mb", 256, 16_384) is { } memoryMb) { cfg.WorkerMemoryLimitMb = memoryMb; changed = true; }
        if (Str("--ffmpeg") is { } ffmpeg) { cfg.FfmpegPath = ffmpeg.Trim(); changed = true; }
        if (Str("--whisper") is { } whisper) { cfg.WhisperPath = whisper.Trim(); changed = true; }
        if (Str("--whisper-model") is { } model) { cfg.WhisperModelPath = model.Trim(); changed = true; }
        if (Str("--whisper-fallback-model") is { } fallbackModel)
        { cfg.WhisperFallbackModelPath = fallbackModel.Trim(); changed = true; }
        if (Str("--whisper-language") is { } whisperLanguage) { cfg.WhisperLanguage = whisperLanguage.Trim(); changed = true; }
        if (Int("--ffmpeg-timeout", 10, 3600) is { } ffmpegSeconds) { cfg.FfmpegTimeoutSeconds = ffmpegSeconds; changed = true; }
        if (Int("--whisper-timeout", 30, 24 * 3600) is { } whisperSeconds)
        { cfg.WhisperTimeoutSeconds = whisperSeconds; changed = true; }
        if (Int("--transcription-max-minutes", 1, 24 * 60) is { } minutes)
        { cfg.TranscriptionMaxMinutes = minutes; changed = true; }
        if (Bool("--semantic-enabled") is { } enabled) { cfg.SemanticEnabled = enabled; changed = true; }
        if (Str("--embedding-endpoint") is { } endpoint) { cfg.EmbeddingEndpoint = endpoint.Trim(); changed = true; }
        if (Str("--embedding-model") is { } embeddingModel) { cfg.EmbeddingModel = embeddingModel.Trim(); changed = true; }
        if (Int("--embedding-batch-size", 1, 64) is { } embeddingBatchSize)
        { cfg.EmbeddingBatchSize = embeddingBatchSize; changed = true; }
        if (Int("--embedding-timeout", 10, 3600) is { } embeddingTimeoutSeconds)
        { cfg.EmbeddingTimeoutSeconds = embeddingTimeoutSeconds; changed = true; }
        if (Int("--semantic-max-candidates", 100, 250_000) is { } maxCandidates)
        { cfg.SemanticMaxCandidates = maxCandidates; changed = true; }

        return new Result(changed, errors);
    }
}
