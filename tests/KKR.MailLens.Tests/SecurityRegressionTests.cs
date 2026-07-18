using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
    [DoNotParallelize]
public sealed class SecurityRegressionTests
{
    const string SessionKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public void SecretOptionsAreDetectedInBothSupportedArgumentForms()
    {
        Assert.IsTrue(CommandLineSecurity.ContainsOption(["init", "--pin=neutral-secret"], "--pin"));
        Assert.IsTrue(CommandLineSecurity.ContainsOption(["imap-add", "--pass", "neutral-secret"], "--pass"));
        Assert.IsTrue(CommandLineSecurity.ContainsOption(["INIT", "--PIN", "neutral-secret"], "--pin"));
        Assert.IsFalse(CommandLineSecurity.ContainsOption(["init", "--yubi"], "--pin"));
    }

    [TestMethod]
    public async Task MissingRefreshTokenDoesNotCreateGmailAccount()
    {
        using var database = new TestDatabase();
        string directory = TemporaryDirectory();
        try
        {
            using var store = new GmailTokenStore(directory, SessionKey);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                GmailOAuth.PersistAuthorizedAccountAsync(database.Connection, store,
                    "sender@example.invalid", new TokenResponse()));
            Assert.AreEqual(0, GmailRepository.ListAccounts(database.Connection).Count);
            Assert.IsFalse(Directory.EnumerateFiles(directory).Any());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task FailedAccountInsertRemovesNewOAuthToken()
    {
        using var database = new TestDatabase();
        using (var trigger = database.Connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER reject_gmail_account BEFORE INSERT ON accounts
                BEGIN SELECT RAISE(ABORT,'neutral account failure'); END;
                """;
            trigger.ExecuteNonQuery();
        }

        string directory = TemporaryDirectory();
        try
        {
            using var store = new GmailTokenStore(directory, SessionKey);
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                GmailOAuth.PersistAuthorizedAccountAsync(database.Connection, store,
                    "sender@example.invalid", new TokenResponse { RefreshToken = "neutral-refresh-token" }));
            Assert.AreEqual(0, GmailRepository.ListAccounts(database.Connection).Count);
            Assert.IsFalse(Directory.EnumerateFiles(directory).Any());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
