using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KKR.MailLens;

/// <summary>Konto IMAP. Haslo trzymane jako DPAPI (CurrentUser) w base64 - nie plaintext w pliku.</summary>
sealed class ImapAccount
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;         // true = SSL/TLS na 993; false = STARTTLS (zwykle 143)
    public string User { get; set; } = "";
    public string PasswordProtected { get; set; } = ""; // base64(DPAPI(haslo))

    public void SetPassword(string plain)
        => PasswordProtected = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public string GetPassword()
    {
        if (string.IsNullOrEmpty(PasswordProtected)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(PasswordProtected), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}

/// <summary>Lista kont IMAP (plik `imap-accounts.json` w katalogu danych).</summary>
sealed class ImapAccounts
{
    public List<ImapAccount> Accounts { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ImapAccounts Load()
    {
        string path = Paths.ImapAccountsFile;
        if (!File.Exists(path)) return new ImapAccounts();
        try { return JsonSerializer.Deserialize<ImapAccounts>(File.ReadAllText(path)) ?? new ImapAccounts(); }
        catch { return new ImapAccounts(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Base);
        File.WriteAllText(Paths.ImapAccountsFile, JsonSerializer.Serialize(this, Json));
    }

    public ImapAccount? Find(string name)
    {
        foreach (var a in Accounts) if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }
}
