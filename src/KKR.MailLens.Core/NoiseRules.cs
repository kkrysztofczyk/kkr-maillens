using System.Text.Json;

namespace KKR.MailLens;

/// <summary>
/// Jawna, deterministyczna definicja "szumu" (mail vs alert). Plik `noise-rules.json` w katalogu danych -
/// czytelny, edytowalny recznie (docelowo z GUI). JEDNO zrodlo prawdy: i harvest, i reclassify wolaja IsNoise.
/// Brak AI - same reguly: dokladny adres nadawcy, fragment adresu/nazwy, nazwa folderu.
/// </summary>
sealed class NoiseRules
{
    public List<string> NoiseSenders { get; set; } = new();          // dokladny sender_email (case-insens.)
    public List<string> NoiseSenderContains { get; set; } = new();   // fragment w adresie LUB nazwie nadawcy
    public List<string> NoiseFolders { get; set; } = new();          // nazwa folderu (leaf), case-insens.

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static NoiseRules? _cached;
    static DateTime _cachedStamp;  // mtime pliku dla ktorego zbudowano cache

    /// <summary>Wczytuje z cache, ale AUTOMATYCZNIE odswieza gdy noise-rules.json zmienil sie na dysku
    /// (GUI zyje godzinami w trayu - reczna edycja pliku + harvest musi lapac nowe reguly bez restartu).
    /// Tworzy plik z domyslnymi regulami jesli brak.</summary>
    public static NoiseRules Load()
    {
        string path = Paths.NoiseRulesFile;
        DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (_cached != null && stamp == _cachedStamp) return _cached;

        if (File.Exists(path))
        {
            try { _cached = JsonSerializer.Deserialize<NoiseRules>(File.ReadAllText(path)) ?? Default(); }
            catch { _cached = Default(); }
        }
        else
        {
            _cached = Default();
            try { Directory.CreateDirectory(Paths.Base); File.WriteAllText(path, JsonSerializer.Serialize(_cached, Json)); stamp = File.GetLastWriteTimeUtc(path); } catch { }
        }
        _cachedStamp = stamp;
        return _cached;
    }

    public static void Reload() { _cached = null; Load(); }

    /// <summary>Zapisuje reguly do pliku i odswieza cache.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Paths.Base);
        File.WriteAllText(Paths.NoiseRulesFile, JsonSerializer.Serialize(this, Json));
        _cached = this;
        try { _cachedStamp = File.GetLastWriteTimeUtc(Paths.NoiseRulesFile); } catch { _cachedStamp = DateTime.MinValue; }
    }

    /// <summary>Domyslne reguly = PUSTE. Kazdy buduje wlasne (harvest bez regul = wszystko 'mail').
    /// Personalne reguly zyja tylko w noise-rules.json w katalogu danych - nie zaszywamy ich w kodzie
    /// (inaczej trafialyby do paczki dla kogos innego wbrew dokumentacji "budujesz swoje").</summary>
    static NoiseRules Default() => new();

    /// <summary>Etykiety WSZYSTKICH regul, ktore trafiaja w tego maila (do analizy wkladu/zazebienia).</summary>
    public List<string> MatchingRules(string? folderLeaf, string? senderEmail, string? senderName)
    {
        var hits = new List<string>();
        string fl = (folderLeaf ?? "").Trim().ToLowerInvariant();
        foreach (var f in NoiseFolders) if (fl.Length > 0 && f.Trim().ToLowerInvariant() == fl) hits.Add("folder:" + f);
        string se = (senderEmail ?? "").ToLowerInvariant();
        foreach (var s in NoiseSenders) if (se.Length > 0 && s.Trim().ToLowerInvariant() == se) hits.Add("sender:" + s);
        string blob = se + " " + (senderName ?? "").ToLowerInvariant();
        foreach (var s in NoiseSenderContains) { var n = s.Trim().ToLowerInvariant(); if (n.Length > 0 && blob.Contains(n)) hits.Add("contains:" + s); }
        return hits;
    }

    public bool IsNoise(string? folderLeaf, string? senderEmail, string? senderName)
    {
        string fl = (folderLeaf ?? "").Trim().ToLowerInvariant();
        foreach (var f in NoiseFolders) if (f.Trim().ToLowerInvariant() == fl && fl.Length > 0) return true;

        string se = (senderEmail ?? "").ToLowerInvariant();
        foreach (var s in NoiseSenders) if (s.Trim().ToLowerInvariant() == se && se.Length > 0) return true;

        string blob = se + " " + (senderName ?? "").ToLowerInvariant();
        foreach (var s in NoiseSenderContains)
        {
            string needle = s.Trim().ToLowerInvariant();
            if (needle.Length > 0 && blob.Contains(needle)) return true;
        }
        return false;
    }
}
