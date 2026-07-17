using System.Text;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class GmailTokenStoreTests
{
    [TestMethod]
    public async Task Store_ProtectsTokenWithDpapi_AndRoundTrips()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-token-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new GmailTokenStore(directory);
            var token = new TokenResponse { AccessToken = "neutral-access-token", RefreshToken = "neutral-refresh-token" };
            await store.StoreAsync("account-1", token);

            string file = Directory.GetFiles(directory, "*.bin").Single();
            string raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Assert.IsFalse(raw.Contains("neutral-refresh-token", StringComparison.Ordinal));
            TokenResponse loaded = await store.GetAsync<TokenResponse>("account-1");
            Assert.AreEqual(token.RefreshToken, loaded.RefreshToken);

            await store.DeleteAsync<TokenResponse>("account-1");
            Assert.IsFalse(File.Exists(file));
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch { } }
    }
}
