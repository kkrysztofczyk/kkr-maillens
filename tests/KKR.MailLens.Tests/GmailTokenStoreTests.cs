using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class GmailTokenStoreTests
{
    [TestMethod]
    public async Task Store_RequiresDpapiAndMatchingSessionKey()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-token-tests", Guid.NewGuid().ToString("N"));
        try
        {
            const string sessionKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            using var store = new GmailTokenStore(directory, sessionKey);
            var token = new TokenResponse { AccessToken = "neutral-access-token", RefreshToken = "neutral-refresh-token" };
            await store.StoreAsync("account-1", token);

            string file = Directory.GetFiles(directory, "*.bin").Single();
            string raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Assert.IsFalse(raw.Contains("neutral-refresh-token", StringComparison.Ordinal));
            TokenResponse loaded = await store.GetAsync<TokenResponse>("account-1");
            Assert.AreEqual(token.RefreshToken, loaded.RefreshToken);

            using var wrongSession = new GmailTokenStore(directory,
                "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => wrongSession.GetAsync<TokenResponse>("account-1"));

            await store.DeleteAsync<TokenResponse>("account-1");
            Assert.IsFalse(File.Exists(file));
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch { } }
    }

    [TestMethod]
    public async Task Get_MigratesLegacyDpapiOnlyTokenToSessionEnvelope()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-token-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string tokenKey = "account-legacy";
            const string sessionKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            var token = new TokenResponse { RefreshToken = "neutral-legacy-refresh-token" };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(token, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            byte[] entropy = SHA256.HashData(Encoding.UTF8.GetBytes("KKR.MailLens.GmailOAuth.v1"));
            byte[] legacy = ProtectedData.Protect(json, entropy, DataProtectionScope.CurrentUser);
            string path = TokenPath<TokenResponse>(directory, tokenKey);
            await File.WriteAllBytesAsync(path, legacy);

            using var store = new GmailTokenStore(directory, sessionKey);
            TokenResponse loaded = await store.GetAsync<TokenResponse>(tokenKey);
            Assert.AreEqual(token.RefreshToken, loaded.RefreshToken);

            byte[] migratedOuter = await File.ReadAllBytesAsync(path);
            byte[] migratedInner = ProtectedData.Unprotect(migratedOuter, entropy, DataProtectionScope.CurrentUser);
            Assert.IsTrue(SessionSecretProtector.IsEnvelope(migratedInner));
            using var wrongSession = new GmailTokenStore(directory,
                "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => wrongSession.GetAsync<TokenResponse>(tokenKey));

            CryptographicOperations.ZeroMemory(json);
            CryptographicOperations.ZeroMemory(legacy);
            CryptographicOperations.ZeroMemory(migratedOuter);
            CryptographicOperations.ZeroMemory(migratedInner);
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch { } }
    }

    static string TokenPath<T>(string directory, string key)
    {
        string material = typeof(T).FullName + "|" + key;
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return Path.Combine(directory, name + ".bin");
    }
}
