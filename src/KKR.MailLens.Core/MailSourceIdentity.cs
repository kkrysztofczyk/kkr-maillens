using System.Security.Cryptography;
using System.Text;

namespace KKR.MailLens;

static class MailSourceIdentity
{
    public static string Create(string provider, string providerMessageKey)
    {
        provider = provider.Trim().ToLowerInvariant();
        if (provider is not ("gmail" or "imap" or "outlook"))
            throw new ArgumentException("Nieobsługiwany provider wiadomości.", nameof(provider));
        if (string.IsNullOrWhiteSpace(providerMessageKey))
            throw new ArgumentException("Brak identyfikatora wiadomości providera.", nameof(providerMessageKey));
        byte[] bytes = Encoding.UTF8.GetBytes(providerMessageKey);
        try { return provider + ":" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
