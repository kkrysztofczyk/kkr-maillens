using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

enum MailboxProvider
{
    Gmail,
    Imap,
    Outlook,
}

sealed record MailboxSourceDefinition(
    MailboxProvider Provider,
    string ExternalKey,
    string DisplayName,
    string? CredentialReference = null,
    string SettingsJson = "{}",
    bool Enabled = true);

sealed record MailboxSourceRecord(
    long Id,
    MailboxProvider Provider,
    string ExternalKey,
    string DisplayName,
    string? CredentialReference,
    string SettingsJson,
    bool Enabled,
    int SortOrder,
    string CreatedAt,
    string UpdatedAt);

static class MailboxSourceRepository
{
    public static MailboxSourceRecord Upsert(SqliteConnection connection, MailboxSourceDefinition definition)
    {
        string provider = ProviderName(definition.Provider);
        string externalKey = NormalizeExternalKey(definition.Provider, definition.ExternalKey);
        string displayName = string.IsNullOrWhiteSpace(definition.DisplayName)
            ? externalKey
            : definition.DisplayName.Trim();
        string? credentialReference = NormalizeOptional(definition.CredentialReference);
        string settingsJson = NormalizeSettings(definition.SettingsJson);
        string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mailbox_sources(
                provider,external_key,display_name,credential_ref,settings_json,enabled,sort_order,created_at,updated_at)
            VALUES(
                $provider,$externalKey,$displayName,$credentialRef,$settings,$enabled,
                COALESCE((SELECT MAX(sort_order)+1 FROM mailbox_sources),0),$now,$now)
            ON CONFLICT(provider,external_key) DO UPDATE SET
                display_name=excluded.display_name,
                credential_ref=excluded.credential_ref,
                settings_json=excluded.settings_json,
                enabled=excluded.enabled,
                updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$externalKey", externalKey);
        command.Parameters.AddWithValue("$displayName", displayName);
        command.Parameters.AddWithValue("$credentialRef", (object?)credentialReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$settings", settingsJson);
        command.Parameters.AddWithValue("$enabled", definition.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();

        return Find(connection, definition.Provider, externalKey)
            ?? throw new InvalidOperationException("Nie zapisano źródła poczty.");
    }

    public static IReadOnlyList<MailboxSourceRecord> List(SqliteConnection connection)
    {
        var result = new List<MailboxSourceRecord>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,provider,external_key,display_name,credential_ref,settings_json,
                   enabled,sort_order,created_at,updated_at
            FROM mailbox_sources
            ORDER BY sort_order,id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(Read(reader));
        return result;
    }

    public static MailboxSourceRecord? Find(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,provider,external_key,display_name,credential_ref,settings_json,
                   enabled,sort_order,created_at,updated_at
            FROM mailbox_sources WHERE id=$id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static MailboxSourceRecord? Find(
        SqliteConnection connection,
        MailboxProvider provider,
        string externalKey)
    {
        string normalizedKey = NormalizeExternalKey(provider, externalKey);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,provider,external_key,display_name,credential_ref,settings_json,
                   enabled,sort_order,created_at,updated_at
            FROM mailbox_sources
            WHERE provider=$provider AND external_key=$externalKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$provider", ProviderName(provider));
        command.Parameters.AddWithValue("$externalKey", normalizedKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static MailboxSourceRecord? FindByCredentialReference(
        SqliteConnection connection,
        string credentialReference)
    {
        if (string.IsNullOrWhiteSpace(credentialReference))
            return null;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,provider,external_key,display_name,credential_ref,settings_json,
                   enabled,sort_order,created_at,updated_at
            FROM mailbox_sources
            WHERE credential_ref=$credentialRef
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$credentialRef", credentialReference.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static bool SetEnabled(SqliteConnection connection, long id, bool enabled)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mailbox_sources
            SET enabled=$enabled,updated_at=$now
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    public static bool SetSortOrder(SqliteConnection connection, long id, int sortOrder)
    {
        if (sortOrder < 0)
            throw new ArgumentOutOfRangeException(nameof(sortOrder));

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mailbox_sources
            SET sort_order=$sortOrder,updated_at=$now
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    public static bool Delete(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mailbox_sources WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    public static bool Delete(
        SqliteConnection connection,
        MailboxProvider provider,
        string externalKey)
    {
        MailboxSourceRecord? source = Find(connection, provider, externalKey);
        return source is not null && Delete(connection, source.Id);
    }

    static MailboxSourceRecord Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        ParseProvider(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.GetInt64(6) != 0,
        reader.GetInt32(7),
        reader.GetString(8),
        reader.GetString(9));

    internal static string ProviderName(MailboxProvider provider) => provider switch
    {
        MailboxProvider.Gmail => "gmail",
        MailboxProvider.Imap => "imap",
        MailboxProvider.Outlook => "outlook",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    internal static MailboxProvider ParseProvider(string provider) => provider switch
    {
        "gmail" => MailboxProvider.Gmail,
        "imap" => MailboxProvider.Imap,
        "outlook" => MailboxProvider.Outlook,
        _ => throw new InvalidDataException($"Nieobsługiwany dostawca źródła poczty: {provider}."),
    };

    static string NormalizeExternalKey(MailboxProvider provider, string externalKey)
    {
        if (string.IsNullOrWhiteSpace(externalKey))
            throw new ArgumentException("Brak zewnętrznego identyfikatora źródła poczty.", nameof(externalKey));

        string value = externalKey.Trim();
        return provider == MailboxProvider.Gmail ? value.ToLowerInvariant() : value;
    }

    static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    static string NormalizeSettings(string? settingsJson)
    {
        string value = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson.Trim();
        using JsonDocument document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Ustawienia źródła poczty muszą być obiektem JSON.", nameof(settingsJson));
        return document.RootElement.GetRawText();
    }
}
