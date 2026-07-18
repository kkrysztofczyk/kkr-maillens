using Google.Apis.Auth.OAuth2.Responses;
using System.Security.Cryptography;

namespace KKR.MailLens;

sealed record SessionCredentialMigrationResult(int GmailTokens, int ImapPasswords, int Failures);

static class SessionCredentialMigration
{
    public static async Task<SessionCredentialMigrationResult> RunAsync(string sessionKeyHex,
        CancellationToken cancellationToken = default)
    {
        int gmailTokens = 0;
        int failures = 0;
        using (var connection = Db.Open(sessionKeyHex, create: false))
        {
            Db.EnsureSchema(connection);
            using var store = new GmailTokenStore(Paths.GmailTokensDir, sessionKeyHex);
            foreach (GmailAccountRecord account in GmailRepository.ListAccounts(connection))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (await store.MigrateAsync<TokenResponse>(account.TokenKey).ConfigureAwait(false))
                        gmailTokens++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException
                    or FormatException or System.Text.Json.JsonException)
                {
                    failures++;
                }
            }
        }

        int imapPasswords = 0;
        try { imapPasswords = ImapAccounts.Load().MigrateCredentials(sessionKeyHex); }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or FormatException)
        { failures++; }
        return new SessionCredentialMigrationResult(gmailTokens, imapPasswords, failures);
    }
}
