using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class SessionCredentialTests
{
    const string SessionKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    const string WrongSessionKey = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [TestMethod]
    public void ImapPassword_RequiresMatchingSessionKey()
    {
        var account = new ImapAccount();
        account.SetPassword("neutral-password", SessionKey);

        Assert.AreEqual("neutral-password", account.GetPassword(SessionKey));
        Assert.Throws<CryptographicException>(() => account.GetPassword(WrongSessionKey));
        Assert.IsFalse(account.PasswordProtected.Contains("neutral-password", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ImapPassword_MigratesLegacyDpapiOnlyValue()
    {
        byte[] plaintext = Encoding.UTF8.GetBytes("neutral-legacy-password");
        byte[] legacy = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        try
        {
            var account = new ImapAccount { PasswordProtected = Convert.ToBase64String(legacy) };

            Assert.IsTrue(account.MigratePassword(SessionKey));
            Assert.IsFalse(account.MigratePassword(SessionKey));
            Assert.AreEqual("neutral-legacy-password", account.GetPassword(SessionKey));
            Assert.Throws<CryptographicException>(() => account.GetPassword(WrongSessionKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(legacy);
        }
    }
}
