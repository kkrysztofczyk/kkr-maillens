using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Util.Store;

namespace KKR.MailLens;

/// <summary>Magazyn IDataStore: kazdy obiekt OAuth jest serializowany, a nastepnie chroniony DPAPI CurrentUser.</summary>
sealed class GmailTokenStore : IDataStore
{
    static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("KKR.MailLens.GmailOAuth.v1"));
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly string _directory;

    public GmailTokenStore(string directory) => _directory = directory;

    public async Task StoreAsync<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        Directory.CreateDirectory(_directory);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        try
        {
            byte[] encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            string path = PathFor<T>(key);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllBytesAsync(temporary, encrypted).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    public Task DeleteAsync<T>(string key)
    {
        string path = PathFor<T>(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<T> GetAsync<T>(string key)
    {
        string path = PathFor<T>(key);
        if (!File.Exists(path)) return default!;
        try
        {
            byte[] encrypted = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            byte[] json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<T>(json, Json)!; }
            finally { CryptographicOperations.ZeroMemory(json); }
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Nie mozna odszyfrowac lokalnego tokenu OAuth dla biezacego uzytkownika Windows.");
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
}
