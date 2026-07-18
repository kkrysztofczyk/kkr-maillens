using System.Text.Json;
using System.Text.Json.Serialization;

namespace KKR.MailLens;

/// <summary>
/// Konfiguracja harvestu (nie-tajna) - plik `config.json` w katalogu danych. Zastepuje zaszyte w kodzie
/// filtr skrzynki i limity, dzieki czemu ta sama binarka dziala u kazdego (kazdy ustawia swoja skrzynke) i
/// GUI oraz CLI biora te same wartosci (koniec rozjazdu 1_000_000 vs 5000).
/// StoreFilter="" = wszystkie skrzynki Outlook. MaxPerFolder&lt;=0 = bez limitu.
/// </summary>
sealed class AppConfig
{
    public string StoreFilter { get; set; } = "";   // fragment nazwy skrzynki Outlook (DisplayName); "" = wszystkie
    public int MaxPerFolder { get; set; } = 5000;    // cap elementow skanowanych na folder; <=0 = bez limitu
    public string TesseractPath { get; set; } = "tesseract.exe";
    public string OcrLanguages { get; set; } = "pol+eng";
    public int OcrTimeoutSeconds { get; set; } = 120;
    public int OcrPdfDpi { get; set; } = 300;
    public int OcrMaxPdfPages { get; set; } = 100;
    public int OcrPdfRenderTimeoutSeconds { get; set; } = 120;
    public int OcrPdfBatchSize { get; set; } = 4;
    public bool PaddleOcrEnabled { get; set; }
    public string PaddleOcrPythonPath { get; set; } = "python.exe";
    public string PaddleOcrRunnerPath { get; set; } = "tools\\paddleocr_runner.py";
    public string PaddleOcrLanguage { get; set; } = "pl";
    public string PaddleOcrModelVersion { get; set; } = "PP-OCRv6";
    public string PaddleOcrDevice { get; set; } = "cpu";
    public double PaddleOcrMinimumConfidence { get; set; } = 0.50;
    public int PaddleOcrTimeoutSeconds { get; set; } = 300;
    public int WorkerMemoryLimitMb { get; set; } = 1536;
    public string FfmpegPath { get; set; } = "ffmpeg.exe";
    public string WhisperPath { get; set; } = "whisper-cli.exe";
    public string WhisperModelPath { get; set; } = "";
    public string WhisperLanguage { get; set; } = "auto";
    public int FfmpegTimeoutSeconds { get; set; } = 600;
    public int WhisperTimeoutSeconds { get; set; } = 3600;
    public int TranscriptionMaxMinutes { get; set; } = 120;
    public bool SemanticEnabled { get; set; }
    public string EmbeddingEndpoint { get; set; } = "http://127.0.0.1:11434";
    public string EmbeddingModel { get; set; } = "embeddinggemma";
    public int EmbeddingBatchSize { get; set; } = 16;
    public int EmbeddingTimeoutSeconds { get; set; } = 300;
    public int SemanticMaxCandidates { get; set; } = 25_000;

    /// <summary>Efektywny cap dla skanera (&lt;=0 traktujemy jako "bez limitu"). Nie serializowane.</summary>
    [JsonIgnore]
    public int EffectiveMax => MaxPerFolder <= 0 ? 1_000_000 : MaxPerFolder;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        string path = Paths.ConfigFile;
        if (File.Exists(path))
        {
            try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new(); }
            catch { return new(); }
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Base);
        File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(this, Json));
    }
}
