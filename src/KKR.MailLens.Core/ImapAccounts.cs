using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KKR.MailLens;

/// <summary>Konto IMAP. Hasło szyfrowane kluczem sesji i dodatkowo chronione DPAPI CurrentUser.</summary>
sealed class ImapAccount
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;         // true = SSL/TLS na 993; false = STARTTLS (zwykle 143)
    public string User { get; set; } = "";
    public string PasswordProtected { get; set; } = "";

    public void SetPassword(string plain, string sessionKeyHex)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(plain);
        try
        {
            using var protector = Protector(sessionKeyHex);
            byte[] encrypted = protector.Protect(bytes, dpapiEntropy: null);
            try { PasswordProtected = Convert.ToBase64String(encrypted); }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string GetPassword(string sessionKeyHex)
    {
        if (string.IsNullOrEmpty(PasswordProtected)) return "";
        byte[] encrypted = Convert.FromBase64String(PasswordProtected);
        byte[]? plaintext = null;
        try
        {
            using var protector = Protector(sessionKeyHex);
            (plaintext, _) = protector.Unprotect(encrypted, dpapiEntropy: null);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public bool MigratePassword(string sessionKeyHex)
    {
        if (string.IsNullOrEmpty(PasswordProtected)) return false;
        byte[] encrypted = Convert.FromBase64String(PasswordProtected);
        byte[]? plaintext = null;
        try
        {
            using var protector = Protector(sessionKeyHex);
            bool legacy;
            (plaintext, legacy) = protector.Unprotect(encrypted, dpapiEntropy: null);
            if (!legacy) return false;
            byte[] protectedBytes = protector.Protect(plaintext, dpapiEntropy: null);
            try { PasswordProtected = Convert.ToBase64String(protectedBytes); }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    static SessionSecretProtector Protector(string sessionKeyHex) =>
        new(sessionKeyHex, "KKR.MailLens.ImapPassword.v2");
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

    public int MigrateCredentials(string sessionKeyHex)
    {
        int changed = 0;
        foreach (ImapAccount account in Accounts)
            if (account.MigratePassword(sessionKeyHex)) changed++;
        if (changed > 0) Save();
        return changed;
    }

    public ImapAccount? Find(string name)
    {
        foreach (var a in Accounts) if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }
}
