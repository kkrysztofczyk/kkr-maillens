using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

static class GmailOAuth
{
    static readonly string[] Scopes = [GmailService.Scope.GmailReadonly];

    public static async Task<GmailAccountRecord> ConnectAsync(SqliteConnection database,
        string sessionKeyHex, CancellationToken cancellationToken)
    {
        ClientSecrets secrets = GmailOAuthClientConfig.Load();
        using var store = new GmailTokenStore(Paths.GmailTokensDir, sessionKeyHex);
        string pendingKey = "pending-" + Guid.NewGuid().ToString("N");

        try
        {
            var initializer = new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = secrets,
                DataStore = store,
            };
            UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                initializer, Scopes, pendingKey, usePkce: true, cancellationToken, store).ConfigureAwait(false);

            using var service = new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "KKR MailLens",
            });
            var profile = await service.Users.GetProfile("me").ExecuteAsync(cancellationToken).ConfigureAwait(false);
            string email = profile.EmailAddress?.Trim() ?? "";
            if (email.Length == 0) throw new InvalidOperationException("Gmail API nie zwrocilo adresu polaczonego konta.");

            string proposedTokenKey = "gmail-" + Guid.NewGuid().ToString("N");
            GmailAccountRecord account = GmailRepository.UpsertAccount(database, email, email, proposedTokenKey);
            TokenResponse? token = await store.GetAsync<TokenResponse>(pendingKey).ConfigureAwait(false);
            if (token is null || string.IsNullOrWhiteSpace(token.RefreshToken))
                throw new InvalidOperationException("Google nie zwrocil refresh tokenu. Cofnij zgode aplikacji i polacz konto ponownie.");
            await store.StoreAsync(account.TokenKey, token).ConfigureAwait(false);
            return account;
        }
        finally
        {
            await store.DeleteAsync<TokenResponse>(pendingKey).ConfigureAwait(false);
        }
    }

    public static async Task<IGmailApiClient> CreateApiClientAsync(GmailAccountRecord account,
        string sessionKeyHex, CancellationToken cancellationToken)
    {
        ClientSecrets secrets = GmailOAuthClientConfig.Load();
        var store = new GmailTokenStore(Paths.GmailTokensDir, sessionKeyHex);
        GoogleAuthorizationCodeFlow? flow = null;
        GmailService? service = null;
        try
        {
            TokenResponse? token = await store.GetAsync<TokenResponse>(account.TokenKey).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (token is null || string.IsNullOrWhiteSpace(token.RefreshToken))
                throw new GmailAuthorizationException();

            flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = secrets,
                DataStore = store,
            });
            var credential = new UserCredential(flow, account.TokenKey, token);
            service = new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "KKR MailLens",
            });
            return new GoogleGmailApiClient(service, flow, store);
        }
        catch
        {
            service?.Dispose();
            flow?.Dispose();
            store.Dispose();
            throw;
        }
    }

    public static async Task RemoveTokenAsync(string tokenKey, string sessionKeyHex,
        CancellationToken cancellationToken = default)
    {
        using var store = new GmailTokenStore(Paths.GmailTokensDir, sessionKeyHex);
        try
        {
            TokenResponse? token = await store.GetAsync<TokenResponse>(tokenKey).ConfigureAwait(false);
            if (token is not null)
            {
                ClientSecrets secrets = GmailOAuthClientConfig.Load();
                using var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = secrets,
                    DataStore = store,
                });
                var credential = new UserCredential(flow, tokenKey, token);
                await credential.RevokeTokenAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Brak sieci lub konfiguracji klienta nie moze zablokowac lokalnego usuniecia tokenu.
        }
        finally { await store.DeleteAsync<TokenResponse>(tokenKey).ConfigureAwait(false); }
    }
}

static class GmailOAuthClientConfig
{
    const string ClientIdEnv = "KKR_MAILLENS_GMAIL_CLIENT_ID";
    const string ClientSecretEnv = "KKR_MAILLENS_GMAIL_CLIENT_SECRET";
    const string ConfigPathEnv = "KKR_MAILLENS_GMAIL_OAUTH_CONFIG";

    public static ClientSecrets Load()
    {
        string? envId = Environment.GetEnvironmentVariable(ClientIdEnv);
        string? envSecret = Environment.GetEnvironmentVariable(ClientSecretEnv);
        if (!string.IsNullOrWhiteSpace(envId) || !string.IsNullOrWhiteSpace(envSecret))
        {
            if (string.IsNullOrWhiteSpace(envId) || string.IsNullOrWhiteSpace(envSecret))
                throw new InvalidOperationException($"Ustaw jednoczesnie {ClientIdEnv} i {ClientSecretEnv}.");
            return new ClientSecrets { ClientId = envId.Trim(), ClientSecret = envSecret.Trim() };
        }

        string path = Environment.GetEnvironmentVariable(ConfigPathEnv) is { Length: > 0 } configured
            ? configured
            : Paths.GmailOAuthClientFile;
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Brak lokalnej konfiguracji OAuth. Ustaw {ConfigPathEnv} albo zapisz plik w: {Paths.GmailOAuthClientFile}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        if (!document.RootElement.TryGetProperty("installed", out JsonElement installed) || installed.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Konfiguracja OAuth musi byc klientem typu Desktop app (sekcja 'installed').");
        string id = ReadRequired(installed, "client_id");
        string secret = ReadRequired(installed, "client_secret");
        return new ClientSecrets { ClientId = id, ClientSecret = secret };
    }

    static string ReadRequired(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException($"Konfiguracja OAuth nie zawiera pola '{name}'.");
        return value.GetString()!.Trim();
    }
}
