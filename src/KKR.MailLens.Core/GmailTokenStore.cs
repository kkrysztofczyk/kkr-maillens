using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Util.Store;

namespace KKR.MailLens;

/// <summary>Magazyn IDataStore: obiekty OAuth są szyfrowane kluczem sesji, a następnie chronione DPAPI CurrentUser.</summary>
sealed class GmailTokenStore : IDataStore, IDisposable
{
    static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("KKR.MailLens.GmailOAuth.v1"));
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly string _directory;
    readonly SessionSecretProtector _protector;

    public GmailTokenStore(string directory, string sessionKeyHex)
    {
        _directory = directory;
        _protector = new SessionSecretProtector(sessionKeyHex, "KKR.MailLens.GmailTokenStore.v2");
    }

    public async Task StoreAsync<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        Directory.CreateDirectory(_directory);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        byte[]? encrypted = null;
        string? temporary = null;
        try
        {
            encrypted = _protector.Protect(json, Entropy);
            string path = PathFor<T>(key);
            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllBytesAsync(temporary, encrypted).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task DeleteAsync<T>(string key)
    {
        string path = PathFor<T>(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<T> GetAsync<T>(string key) => (await ReadAsync<T>(key).ConfigureAwait(false)).Value;

    internal async Task<bool> MigrateAsync<T>(string key) =>
        (await ReadAsync<T>(key).ConfigureAwait(false)).WasLegacy;

    async Task<(T Value, bool WasLegacy)> ReadAsync<T>(string key)
    {
        string path = PathFor<T>(key);
        if (!File.Exists(path)) return (default!, false);
        try
        {
            byte[] encrypted = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            try
            {
                (byte[] json, bool legacy) = _protector.Unprotect(encrypted, Entropy);
                try
                {
                    T value = JsonSerializer.Deserialize<T>(json, Json)!;
                    if (legacy && value is not null) await StoreAsync(key, value).ConfigureAwait(false);
                    return (value, legacy);
                }
                finally { CryptographicOperations.ZeroMemory(json); }
            }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Nie można odszyfrować tokenu OAuth dla bieżącego użytkownika i aktywnej sesji KKR MailLens.");
        }
    }

    public Task ClearAsync()
    {
        if (!Directory.Exists(_directory)) return Task.CompletedTask;
        foreach (string file in Directory.EnumerateFiles(_directory, "*.bin")) File.Delete(file);
        return Task.CompletedTask;
    }

    string PathFor<T>(string key)
    {
        string material = typeof(T).FullName + "|" + key;
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return Path.Combine(_directory, name + ".bin");
    }

    public void Dispose() => _protector.Dispose();
}
