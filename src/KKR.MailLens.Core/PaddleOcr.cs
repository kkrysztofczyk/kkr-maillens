using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KKR.MailLens;

sealed record PaddleOcrOptions(
    string PythonPath,
    string RunnerPath,
    string Language = "pl",
    string ModelVersion = "PP-OCRv6",
    string Device = "cpu",
    double MinimumConfidence = 0.50,
    TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes(5);
    public string ModelName => $"{ModelVersion}:{Language}:{Device}";
}

sealed class PaddleOcrEngine : IDisposable
{
    const int MaxOutputCharacters = 8 * 1024 * 1024;
    const int MaxErrorTailCharacters = 16 * 1024;
    readonly PaddleOcrOptions options;
    readonly string runnerPath;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly StringBuilder errorTail = new();
    readonly object errorSync = new();
    Process? process;
    Stream? input;
    StreamReader? output;
    Task? errorDrain;
    bool disposed;

    public string ModelName => options.ModelName;

    public PaddleOcrEngine(PaddleOcrOptions options)
    {
        this.options = options;
        if (string.IsNullOrWhiteSpace(options.PythonPath))
            throw new ArgumentException("Brak ścieżki do interpretera Python dla PaddleOCR.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.RunnerPath))
            throw new ArgumentException("Brak ścieżki do adaptera PaddleOCR.", nameof(options));
        if (!IsIdentifier(options.Language))
            throw new ArgumentException("Nieprawidłowy język PaddleOCR.", nameof(options));
        if (!IsIdentifier(options.ModelVersion))
            throw new ArgumentException("Nieprawidłowa wersja modelu PaddleOCR.", nameof(options));
        if (!IsDevice(options.Device))
            throw new ArgumentException("Nieprawidłowe urządzenie PaddleOCR.", nameof(options));
        if (options.MinimumConfidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.EffectiveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));

        runnerPath = ResolveRunnerPath(options.RunnerPath);
    }

    public async Task<ExtractionResult> ExtractAsync(byte[] image, string mimeType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length == 0) throw new InvalidDataException("Obraz OCR jest pusty.");
        if (!mimeType.StartsWith("image/", StringComparison.Ordinal))
            throw new NotSupportedException($"PaddleOCR nie obsługuje typu {mimeType}.");
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(options.EffectiveTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            PaddleResponse response;
            try
            {
                // Odczyty potoków procesu nie reagują na token, więc anulowanie
                // ubija proces — zamknięty potok odblokowuje oczekujące operacje.
                using CancellationTokenRegistration killOnCancel = linked.Token.Register(() =>
                {
                    Process? current = process;
                    if (current is not null) TryKill(current);
                });
                response = await ExchangeWithRetryAsync(image, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await KillAndDiagnoseAsync().ConfigureAwait(false);
                throw new TimeoutException($"PaddleOCR przekroczył limit {options.EffectiveTimeout.TotalSeconds:0} s.");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                string error = await KillAndDiagnoseAsync().ConfigureAwait(false);
                string detail = error.Length == 0 ? ex.Message : error;
                throw new InvalidOperationException($"PaddleOCR przerwał strumień wejściowy: {DiagnosticText.Limit(detail)}", ex);
            }
            catch
            {
                KillRunner();
                throw;
            }

            if (response.Error is not null)
                throw new InvalidOperationException(
                    $"PaddleOCR zgłosił błąd rozpoznawania: {DiagnosticText.Limit(response.Error)}");

            string raw = response.Text!;
            string clean = TextNormalizer.Normalize(raw);
            return new ExtractionResult(mimeType, raw, clean, false,
                clean.Length == 0 ? [] : [new ExtractedSegment(0, raw, clean)]);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { input?.Close(); } catch { }
        Process? current = process;
        if (current is not null)
        {
            try { if (!current.WaitForExit(2000)) TryKill(current); } catch { TryKill(current); }
            try { current.Dispose(); } catch { }
        }
        process = null;
        input = null;
        output = null;
        errorDrain = null;
        gate.Dispose();
    }

    async Task<PaddleResponse> ExchangeWithRetryAsync(byte[] image, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            EnsureRunnerStarted();
            token.ThrowIfCancellationRequested();
            try
            {
                await WriteFrameAsync(image, token).ConfigureAwait(false);
                string? line = await ReadResponseLineAsync(token).ConfigureAwait(false);
                if (line is not null) return ParseResponse(line);
                token.ThrowIfCancellationRequested();
                if (attempt > 0) throw await RunnerExitFailureAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 0 && !token.IsCancellationRequested
                && ex is IOException or ObjectDisposedException)
            {
                // Proces padł między stronami — restart i jednorazowe ponowienie.
            }
            KillRunner();
        }
    }

    void EnsureRunnerStarted()
    {
        if (process is { HasExited: false }) return;
        KillRunner();
        ProcessStartInfo start = ProcessRunner.CreateStartInfo(options.PythonPath, redirectInput: true);
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        if (!ProcessRunner.IsBatchScript(options.PythonPath)) start.ArgumentList.Add("-I");
        start.ArgumentList.Add(runnerPath);
        start.ArgumentList.Add("--lang");
        start.ArgumentList.Add(options.Language);
        start.ArgumentList.Add("--ocr-version");
        start.ArgumentList.Add(options.ModelVersion);
        start.ArgumentList.Add("--device");
        start.ArgumentList.Add(options.Device);
        start.ArgumentList.Add("--min-confidence");
        start.ArgumentList.Add(options.MinimumConfidence.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        Process started = ProcessRunner.Start(start, "PaddleOCR");
        process = started;
        input = started.StandardInput.BaseStream;
        output = started.StandardOutput;
        lock (errorSync) errorTail.Clear();
        errorDrain = DrainErrorAsync(started.StandardError, errorTail, errorSync);
    }

    async Task WriteFrameAsync(byte[] image, CancellationToken token)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Length);
        Stream stream = input!;
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(image, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    async Task<string?> ReadResponseLineAsync(CancellationToken token)
    {
        StreamReader reader = output!;
        var line = new StringBuilder(256);
        char[] single = new char[1];
        while (true)
        {
            int read = await reader.ReadAsync(single.AsMemory(), token).ConfigureAwait(false);
            if (read == 0)
            {
                token.ThrowIfCancellationRequested();
                return null;
            }
            if (single[0] == '\n') return line.ToString();
            if (line.Length >= MaxOutputCharacters)
                throw new InvalidDataException("PaddleOCR przekroczył limit rozmiaru odpowiedzi.");
            line.Append(single[0]);
        }
    }

    static PaddleResponse ParseResponse(string line)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String)
                return new PaddleResponse(null, error.GetString() ?? "");
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("text", out JsonElement text)
                || text.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Adapter PaddleOCR nie zwrócił pola text.");
            return new PaddleResponse(text.GetString() ?? "", null);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Adapter PaddleOCR zwrócił nieprawidłowy JSON.", ex);
        }
    }

    async Task<Exception> RunnerExitFailureAsync()
    {
        int? exitCode = null;
        try
        {
            Process? current = process;
            if (current is not null && current.WaitForExit(2000)) exitCode = current.ExitCode;
        }
        catch { }
        string error = await KillAndDiagnoseAsync().ConfigureAwait(false);
        return exitCode is int code
            ? new InvalidOperationException($"PaddleOCR zakończył się kodem {code}: {DiagnosticText.Limit(error)}")
            : new InvalidOperationException(
                $"PaddleOCR zakończył strumień wyjściowy bez odpowiedzi: {DiagnosticText.Limit(error)}");
    }

    async Task<string> KillAndDiagnoseAsync()
    {
        Task? drain = errorDrain;
        KillRunner();
        if (drain is not null)
        {
            try { await drain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        lock (errorSync) return errorTail.ToString();
    }

    void KillRunner()
    {
        Process? current = process;
        process = null;
        input = null;
        output = null;
        errorDrain = null;
        if (current is null) return;
        TryKill(current);
        try { current.Dispose(); } catch { }
    }

    static async Task DrainErrorAsync(StreamReader reader, StringBuilder tail, object sync)
    {
        char[] buffer = new char[4096];
        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (read == 0) return;
                lock (sync)
                {
                    tail.Append(buffer, 0, read);
                    if (tail.Length > MaxErrorTailCharacters * 2)
                        tail.Remove(0, tail.Length - MaxErrorTailCharacters);
                }
            }
        }
        catch { }
    }

    static string ResolveRunnerPath(string configuredPath)
    {
        string[] candidates = Path.IsPathRooted(configuredPath)
            ? [configuredPath]
            : [Path.Combine(AppContext.BaseDirectory, configuredPath), Path.GetFullPath(configuredPath)];
        string? existing = candidates.FirstOrDefault(File.Exists);
        if (existing is null)
            throw new FileNotFoundException("Nie znaleziono lokalnego adaptera PaddleOCR.", candidates[0]);
        return Path.GetFullPath(existing);
    }

    static bool IsIdentifier(string value) => value.Length is > 0 and <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    static bool IsDevice(string value) => value == "cpu" || value == "gpu"
        || (value.StartsWith("gpu:", StringComparison.Ordinal)
            && value.AsSpan(4).Length > 0 && value.AsSpan(4).ToArray().All(char.IsAsciiDigit));

    static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    readonly record struct PaddleResponse(string? Text, string? Error);
}
