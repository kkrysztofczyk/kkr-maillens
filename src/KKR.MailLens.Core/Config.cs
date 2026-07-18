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
    public int WorkerMemoryLimitMb { get; set; } = 1536;

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
