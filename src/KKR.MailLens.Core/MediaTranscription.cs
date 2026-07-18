using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed record MediaTranscriptionOptions(
    string FfmpegPath,
    string WhisperPath,
    string ModelPath,
    string Language = "auto",
    TimeSpan? FfmpegTimeout = null,
    TimeSpan? WhisperTimeout = null,
    int MaxDurationMinutes = 120,
    string? TempDirectory = null,
    string? FallbackModelPath = null)
{
    public TimeSpan EffectiveFfmpegTimeout => FfmpegTimeout ?? TimeSpan.FromMinutes(10);
    public TimeSpan EffectiveWhisperTimeout => WhisperTimeout ?? TimeSpan.FromHours(1);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FfmpegPath)) throw new ArgumentException("Brak ścieżki do FFmpeg.", nameof(FfmpegPath));
        if (string.IsNullOrWhiteSpace(WhisperPath)) throw new ArgumentException("Brak ścieżki do whisper.cpp.", nameof(WhisperPath));
        if (string.IsNullOrWhiteSpace(ModelPath) || !File.Exists(ModelPath))
            throw new FileNotFoundException("Nie znaleziono lokalnego modelu whisper.cpp.", ModelPath);
        if (!string.IsNullOrWhiteSpace(FallbackModelPath) && !File.Exists(FallbackModelPath))
            throw new FileNotFoundException("Nie znaleziono lokalnego modelu fallback whisper.cpp.", FallbackModelPath);
        if (string.IsNullOrWhiteSpace(Language) || Language.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Nieprawidłowy język transkrypcji.", nameof(Language));
        if (EffectiveFfmpegTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(FfmpegTimeout));
        if (EffectiveWhisperTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(WhisperTimeout));
        if (MaxDurationMinutes is < 1 or > 24 * 60) throw new ArgumentOutOfRangeException(nameof(MaxDurationMinutes));
    }
}

interface IMediaTranscriber
{
    string ModelName { get; }
    Task<ExtractionResult> TranscribeAsync(byte[] media, string mimeType,
        CancellationToken cancellationToken = default);
}

sealed class FfmpegWhisperTranscriber : IMediaTranscriber
{
    readonly MediaTranscriptionOptions options;
    string modelName;

    public FfmpegWhisperTranscriber(MediaTranscriptionOptions options)
    {
        this.options = options;
        options.Validate();
        modelName = Path.GetFileName(options.ModelPath);
    }

    public string ModelName => modelName;

    public async Task<ExtractionResult> TranscribeAsync(byte[] media, string mimeType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.Length == 0) throw new InvalidDataException("Plik multimedialny jest pusty.");
        if (!MediaTypes.IsMedia(mimeType)) throw new NotSupportedException($"Transkrypcja nie obsługuje typu {mimeType}.");

        using var workspace = TranscriptionWorkspace.Create(options.TempDirectory ?? Paths.TempDir);
        string wavPath = Path.Combine(workspace.DirectoryPath, "audio.wav");
        string outputPrefix = Path.Combine(workspace.DirectoryPath, "transcript");
        modelName = Path.GetFileName(options.ModelPath);
        await RunFfmpeg(media, wavPath, cancellationToken).ConfigureAwait(false);
        ExtractionResult primary = await TranscribeWav(wavPath, outputPrefix, options.ModelPath,
            mimeType, cancellationToken).ConfigureAwait(false);
        if (primary.CleanText.Length > 0 || string.IsNullOrWhiteSpace(options.FallbackModelPath))
            return primary;

        string fallbackPrefix = Path.Combine(workspace.DirectoryPath, "transcript-fallback");
        ExtractionResult fallback = await TranscribeWav(wavPath, fallbackPrefix, options.FallbackModelPath,
            mimeType, cancellationToken).ConfigureAwait(false);
        if (fallback.CleanText.Length == 0) return primary;
        modelName = Path.GetFileName(options.FallbackModelPath);
        return fallback;
    }

    async Task<ExtractionResult> TranscribeWav(string wavPath, string outputPrefix, string modelPath,
        string mimeType, CancellationToken cancellationToken)
    {
        await RunWhisper(wavPath, outputPrefix, modelPath, cancellationToken).ConfigureAwait(false);
        string jsonPath = outputPrefix + ".json";
        if (!File.Exists(jsonPath)) throw new InvalidDataException("whisper.cpp nie utworzył wyniku JSON.");
        string json = await File.ReadAllTextAsync(jsonPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return WhisperJsonParser.Parse(json, mimeType);
    }

    async Task RunFfmpeg(byte[] media, string wavPath, CancellationToken cancellationToken)
    {
        int maxSeconds = checked(options.MaxDurationMinutes * 60);
        using Process process = Start(options.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", "pipe:0", "-t", maxSeconds.ToString(),
             "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wavPath], redirectInput: true);
        using var timeout = new CancellationTokenSource(options.EffectiveFfmpegTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(media, linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException(
                $"FFmpeg zakończył się kodem {process.ExitCode}: {Limit(error)}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"FFmpeg przekroczył limit {options.EffectiveFfmpegTimeout.TotalSeconds:0} s.");
        }
        catch { TryKill(process); throw; }
    }

    async Task RunWhisper(string wavPath, string outputPrefix, string modelPath,
        CancellationToken cancellationToken)
    {
        using Process process = Start(options.WhisperPath,
            ["-m", Path.GetFullPath(modelPath), "-f", wavPath, "-l", options.Language,
             "-oj", "-of", outputPrefix, "-np"], redirectInput: false);
        using var timeout = new CancellationTokenSource(options.EffectiveWhisperTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException(
                $"whisper.cpp zakończył się kodem {process.ExitCode}: {Limit(error.Length > 0 ? error : output)}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"whisper.cpp przekroczył limit {options.EffectiveWhisperTimeout.TotalSeconds:0} s.");
        }
        catch { TryKill(process); throw; }
    }

    static Process Start(string executable, IReadOnlyList<string> arguments, bool redirectInput)
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(executable);
        }
        else start.FileName = executable;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException($"Nie udało się uruchomić {Path.GetFileName(executable)}.");
    }

    static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
    static string Limit(string value)
    {
        string clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 500 ? clean : clean[..500];
    }
}

static class WhisperJsonParser
{
    public static ExtractionResult Parse(string json, string mimeType)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("transcription", out JsonElement transcription)
            || transcription.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Wynik whisper.cpp nie zawiera tablicy transcription.");

        var segments = new List<ExtractedSegment>();
        foreach (JsonElement item in transcription.EnumerateArray())
        {
            string raw = item.TryGetProperty("text", out JsonElement text) ? text.GetString() ?? "" : "";
            string clean = TextNormalizer.Normalize(raw);
            if (clean.Length == 0) continue;
            if (!item.TryGetProperty("offsets", out JsonElement offsets)
                || !offsets.TryGetProperty("from", out JsonElement from)
                || !offsets.TryGetProperty("to", out JsonElement to)
                || !from.TryGetInt64(out long startMs) || !to.TryGetInt64(out long endMs))
                throw new InvalidDataException("Segment whisper.cpp nie zawiera prawidłowych offsetów.");
            if (startMs < 0 || endMs < startMs)
                throw new InvalidDataException("Segment whisper.cpp ma nieprawidłowy zakres czasu.");
            segments.Add(new ExtractedSegment(segments.Count, raw, clean, StartMs: startMs, EndMs: endMs));
        }

        string? language = null;
        if (root.TryGetProperty("result", out JsonElement result)
            && result.TryGetProperty("language", out JsonElement languageElement))
            language = languageElement.GetString();
        return new ExtractionResult(mimeType, string.Join('\n', segments.Select(x => x.RawText)),
            string.Join('\n', segments.Select(x => x.CleanText)), false, segments)
        { DetectedLanguage = language };
    }
}

static class MediaTypes
{
    public static bool IsMedia(string mimeType) => mimeType.StartsWith("audio/", StringComparison.Ordinal)
        || mimeType.StartsWith("video/", StringComparison.Ordinal);
}

static class MediaTranscriptionProcessor
{
    public static async Task ProcessAsync(SqliteConnection connection, EncryptedBlobStore store,
        long attachmentId, long documentId, IMediaTranscriber transcriber,
        CancellationToken cancellationToken, Action? heartbeat = null)
    {
        MailAttachmentRepository.Item item = MailAttachmentRepository.Get(connection, attachmentId);
        StoredBlob blob = EncryptedBlobStore.Get(connection,
            item.BlobId ?? throw new InvalidOperationException("Załącznik nie ma pobranego blobu."));
        byte[] plaintext = store.Read(blob);
        try
        {
            DetectedFile detected = FileTypeDetector.Detect(item.Filename, item.MimeType, plaintext);
            if (!MediaTypes.IsMedia(detected.MimeType))
                throw new NotSupportedException($"Transkrypcja nie obsługuje typu {detected.MimeType}.");
            heartbeat?.Invoke();
            ExtractionResult result = await transcriber.TranscribeAsync(
                plaintext, detected.MimeType, cancellationToken).ConfigureAwait(false);
            heartbeat?.Invoke();
            ContentDocumentRepository.SaveExtraction(connection, documentId, result,
                "ffmpeg+whisper.cpp", "1", documentKind: "transcript",
                modelName: transcriber.ModelName, transcriptionCompleted: true);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}

sealed class TranscriptionWorkspace : IDisposable
{
    FileStream? lockFile;
    public string DirectoryPath { get; }

    TranscriptionWorkspace(string directoryPath, FileStream lockFile)
    { DirectoryPath = directoryPath; this.lockFile = lockFile; }

    public static TranscriptionWorkspace Create(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
        CleanupOrphans(root);
        string directory = Path.Combine(root, "transcription-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var heldLock = new FileStream(Path.Combine(directory, ".lock"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        return new TranscriptionWorkspace(directory, heldLock);
    }

    static void CleanupOrphans(string rootDirectory)
    {
        foreach (string directory in Directory.EnumerateDirectories(rootDirectory, "transcription-*"))
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
        lockFile?.Dispose();
        lockFile = null;
        if (!Directory.Exists(DirectoryPath)) return;
        try { Directory.Delete(DirectoryPath, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Nie udało się usunąć jawnych plików roboczych transkrypcji.", ex);
        }
    }
}
