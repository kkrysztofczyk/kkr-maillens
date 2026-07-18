using System.Security.Cryptography;
using System.Text;

namespace KKR.MailLens;

sealed class SessionSecretProtector : IDisposable
{
    static readonly byte[] Magic = "KKRSEC02"u8.ToArray();
    const byte Version = 2;
    const int NonceSize = 12;
    const int TagSize = 16;
    static readonly byte[] Aad = [.. Magic, Version];
    readonly byte[] _key;

    public SessionSecretProtector(string sessionKeyHex, string context)
    {
        if (string.IsNullOrWhiteSpace(context)) throw new ArgumentException("Brak kontekstu ochrony sekretu.", nameof(context));
        byte[] sessionKey;
        try { sessionKey = Convert.FromHexString(sessionKeyHex); }
        catch (FormatException ex) { throw new ArgumentException("Nieprawidłowy klucz sesji.", nameof(sessionKeyHex), ex); }
        if (sessionKey.Length < 32) throw new ArgumentException("Klucz sesji jest zbyt krótki.", nameof(sessionKeyHex));
        try
        {
            _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, 32,
                salt: new byte[32], info: Encoding.UTF8.GetBytes(context));
        }
        finally { CryptographicOperations.ZeroMemory(sessionKey); }
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, byte[]? dpapiEntropy)
    {
        if (plaintext.IsEmpty) throw new ArgumentException("Sekret jest pusty.", nameof(plaintext));
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];
        byte[] envelope = new byte[Aad.Length + NonceSize + TagSize + ciphertext.Length];
        try
        {
            using (var aes = new AesGcm(_key, TagSize))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad);
            Aad.CopyTo(envelope, 0);
            nonce.CopyTo(envelope, Aad.Length);
            tag.CopyTo(envelope, Aad.Length + NonceSize);
            ciphertext.CopyTo(envelope, Aad.Length + NonceSize + TagSize);
            return ProtectedData.Protect(envelope, dpapiEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public (byte[] Plaintext, bool WasLegacy) Unprotect(ReadOnlySpan<byte> protectedBytes,
        byte[]? dpapiEntropy)
    {
        if (protectedBytes.IsEmpty) throw new InvalidDataException("Chroniony sekret jest pusty.");
        byte[] protectedCopy = protectedBytes.ToArray();
        byte[] inner;
        try { inner = ProtectedData.Unprotect(protectedCopy, dpapiEntropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(protectedCopy); }
        if (!IsEnvelope(inner)) return (inner, true);

        int header = Aad.Length + NonceSize + TagSize;
        if (inner.Length <= header) { CryptographicOperations.ZeroMemory(inner); throw new InvalidDataException("Nieprawidłowy format chronionego sekretu."); }
        ReadOnlySpan<byte> nonce = inner.AsSpan(Aad.Length, NonceSize);
        ReadOnlySpan<byte> tag = inner.AsSpan(Aad.Length + NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = inner.AsSpan(header);
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad);
            return (plaintext, false);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(inner); }
    }

    internal static bool IsEnvelope(ReadOnlySpan<byte> value) =>
        value.Length >= Aad.Length && value[..Aad.Length].SequenceEqual(Aad);

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}
