using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed record StoredBlob(long Id, string Sha256, string EncryptedPath, long OriginalSize, int EncryptionVersion);

sealed class EncryptedBlobStore
{
    static readonly byte[] Magic = "KKRMLB01"u8.ToArray();
    const byte LegacyVersion = 1;
    const byte CurrentVersion = 2;
    static readonly byte[] CurrentAad = [.. Magic, CurrentVersion];
    const int NonceSize = 12;
    const int TagSize = 16;
    readonly string _root;
    readonly byte[] _key;

    public EncryptedBlobStore(string root, string sessionKeyHex)
    {
        _root = Path.GetFullPath(root);
        byte[] sessionKey;
        try { sessionKey = Convert.FromHexString(sessionKeyHex); }
        catch (FormatException ex) { throw new ArgumentException("Nieprawidłowy klucz sesji.", nameof(sessionKeyHex), ex); }
        if (sessionKey.Length < 32) throw new ArgumentException("Klucz sesji jest zbyt krótki.", nameof(sessionKeyHex));
        _key = DeriveKey(sessionKey, "KKR-MailLens-AttachmentStore-v1");
        CryptographicOperations.ZeroMemory(sessionKey);
    }

    public StoredBlob Put(SqliteConnection connection, ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.IsEmpty) throw new InvalidDataException("Nie można zapisać pustego blobu.");
        string hash = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
        StoredBlob? existing = Find(connection, hash);
        if (existing is not null && File.Exists(Absolute(existing.EncryptedPath))) return existing;

        string relative = Path.Combine(hash[..2], hash.Substring(2, 2), hash + ".blob");
        string destination = Absolute(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        byte[] encrypted = Encrypt(plaintext);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, encrypted);
            MoveIntoPlace(temporary, destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO stored_blobs(sha256,encrypted_path,original_size,encryption_version,created_at)
            VALUES($hash,$path,$size,$version,$now)
            ON CONFLICT(sha256) DO UPDATE SET encrypted_path=excluded.encrypted_path
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$path", relative.Replace('\\', '/'));
        command.Parameters.AddWithValue("$size", plaintext.Length);
        command.Parameters.AddWithValue("$version", CurrentVersion);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        long id = Convert.ToInt64(command.ExecuteScalar());
        return new StoredBlob(id, hash, relative.Replace('\\', '/'), plaintext.Length, CurrentVersion);
    }

    public byte[] Read(StoredBlob blob)
    {
        byte[] encrypted = File.ReadAllBytes(Absolute(blob.EncryptedPath));
        try
        {
            if (encrypted.Length <= Magic.Length || encrypted[Magic.Length] != blob.EncryptionVersion)
                throw new InvalidDataException("Wersja blobu nie zgadza się z metadanymi.");
            byte[] plaintext = Decrypt(encrypted);
            string actual = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(blob.Sha256)))
            { CryptographicOperations.ZeroMemory(plaintext); throw new CryptographicException("Niezgodny hash odszyfrowanego blobu."); }
            return plaintext;
        }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    public static StoredBlob Get(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,sha256,encrypted_path,original_size,encryption_version
            FROM stored_blobs WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Blob nie istnieje.");
        return new StoredBlob(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3), reader.GetInt32(4));
    }

    static StoredBlob? Find(SqliteConnection connection, string hash)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,sha256,encrypted_path,original_size,encryption_version FROM stored_blobs WHERE sha256=$hash;";
        command.Parameters.AddWithValue("$hash", hash);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new StoredBlob(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4)) : null;
    }

    byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];
        using (var aes = new AesGcm(_key, TagSize))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, CurrentAad);
        byte[] result = new byte[Magic.Length + 1 + NonceSize + TagSize + ciphertext.Length];
        Magic.CopyTo(result, 0); result[Magic.Length] = CurrentVersion;
        nonce.CopyTo(result, Magic.Length + 1);
        tag.CopyTo(result, Magic.Length + 1 + NonceSize);
        ciphertext.CopyTo(result, Magic.Length + 1 + NonceSize + TagSize);
        return result;
    }

    byte[] Decrypt(ReadOnlySpan<byte> encrypted)
    {
        int header = Magic.Length + 1 + NonceSize + TagSize;
        if (encrypted.Length <= header || !encrypted[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Nieobsługiwany format zaszyfrowanego blobu.");
        byte version = encrypted[Magic.Length];
        ReadOnlySpan<byte> aad = version switch
        {
            LegacyVersion => Magic,
            CurrentVersion => CurrentAad,
            _ => throw new InvalidDataException("Nieobsługiwana wersja zaszyfrowanego blobu."),
        };
        ReadOnlySpan<byte> nonce = encrypted.Slice(Magic.Length + 1, NonceSize);
        ReadOnlySpan<byte> tag = encrypted.Slice(Magic.Length + 1 + NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = encrypted[header..];
        byte[] plaintext = new byte[ciphertext.Length];
        try { using var aes = new AesGcm(_key, TagSize); aes.Decrypt(nonce, ciphertext, tag, plaintext, aad); return plaintext; }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
    }

    string Absolute(string relative)
        => ResolvePath(_root, relative);

    internal static string ResolvePath(string root, string relative)
    {
        string normalizedRoot = Path.GetFullPath(root);
        string full = Path.GetFullPath(Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ścieżka blobu wychodzi poza magazyn.");
        return full;
    }

    internal static void MoveIntoPlace(string temporary, string destination)
    {
        try { File.Move(temporary, destination); }
        catch (IOException) when (File.Exists(destination)) { }
    }

    static byte[] DeriveKey(byte[] inputKey, string context) => HKDF.DeriveKey(
        HashAlgorithmName.SHA256, inputKey, 32, salt: new byte[32], info: Encoding.UTF8.GetBytes(context));
}
