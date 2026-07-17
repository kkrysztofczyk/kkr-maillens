namespace KKR.MailLens;

/// <summary>Lokalizacje korpusu. Domyslnie %LOCALAPPDATA%\kkr-maillens (standard per-user).
/// Nadpisywalne env KKR_MAILLENS_DIR (np. w sandboxie narzedzia, gdzie AppData bywa wirtualizowany
/// - wtedy wskaz neutralna sciezke robocza).</summary>
static class Paths
{
    public static string Base { get; } =
        Environment.GetEnvironmentVariable("KKR_MAILLENS_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kkr-maillens");

    public static string CorpusDb => Path.Combine(Base, "corpus.db");   // zaszyfrowany SQLCipher
    public static string SessionKey => Path.Combine(Base, "session.key"); // DPAPI({klucz, wygasa})
    public static string SaltFile => Path.Combine(Base, "salt.bin");     // sol PBKDF2 (nietajna); tez wyzwanie YubiKey
    public static string ModeFile => Path.Combine(Base, "mode.txt");     // "pin" | "pin+yubi" (sticky po opt-in --yubi)
    public static string NoiseRulesFile => Path.Combine(Base, "noise-rules.json"); // jawne reguly szumu (mail vs alert)
    public static string ImapAccountsFile => Path.Combine(Base, "imap-accounts.json"); // konta IMAP (haslo DPAPI)
    public static string ConfigFile => Path.Combine(Base, "config.json"); // konfiguracja harvestu (store filter, limit)
    public static string GmailOAuthClientFile => Path.Combine(Base, "gmail-oauth-client.json"); // lokalna konfiguracja klienta OAuth
    public static string GmailTokensDir => Path.Combine(Base, "oauth-tokens"); // tokeny OAuth zaszyfrowane DPAPI
    public static string GmailCancelFile => Path.Combine(Base, "gmail-sync.cancel"); // lokalny znacznik anulowania synchronizacji
    public static string BlobsDir => Path.Combine(Base, "blobs"); // zaszyfrowane, deduplikowane załączniki
}
