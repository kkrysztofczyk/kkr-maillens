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

sealed class PaddleOcrEngine
{
    const int MaxOutputCharacters = 8 * 1024 * 1024;
    readonly PaddleOcrOptions options;
    readonly string runnerPath;

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

        using Process process = StartProcess();
        using var timeout = new CancellationTokenSource(options.EffectiveTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Task<string> outputTask = ReadBoundedAsync(process.StandardOutput, MaxOutputCharacters, linked.Token);
        Task<string> errorTask = ReadBoundedAsync(process.StandardError, 16 * 1024, linked.Token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(image, linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"PaddleOCR zakończył się kodem {process.ExitCode}: {Limit(error)}");

            string raw = ParseText(output);
            string clean = TextNormalizer.Normalize(raw);
            return new ExtractionResult(mimeType, raw, clean, false,
                clean.Length == 0 ? [] : [new ExtractedSegment(0, raw, clean)]);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"PaddleOCR przekroczył limit {options.EffectiveTimeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            TryKill(process);
            string error = await DiagnosticAsync(errorTask).ConfigureAwait(false);
            string detail = error.Length == 0 ? ex.Message : error;
            throw new InvalidOperationException($"PaddleOCR przerwał strumień wejściowy: {Limit(detail)}", ex);
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
        if (Path.GetExtension(options.PythonPath).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(options.PythonPath).Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(options.PythonPath);
        }
        else
        {
            start.FileName = options.PythonPath;
            start.ArgumentList.Add("-I");
        }
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
        return Process.Start(start) ?? throw new InvalidOperationException("Nie udało się uruchomić PaddleOCR.");
    }

    static string ParseText(string output)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(output, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (!json.RootElement.TryGetProperty("text", out JsonElement text)
                || text.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Adapter PaddleOCR nie zwrócił pola text.");
            return text.GetString() ?? "";
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Adapter PaddleOCR zwrócił nieprawidłowy JSON.", ex);
        }
    }

    static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(maxCharacters, 16 * 1024));
        char[] buffer = new char[4096];
        bool exceeded = false;
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (exceeded) throw new InvalidDataException("PaddleOCR przekroczył limit rozmiaru odpowiedzi.");
                return result.ToString();
            }
            int remaining = maxCharacters - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(read, remaining));
            exceeded |= read > remaining;
        }
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
