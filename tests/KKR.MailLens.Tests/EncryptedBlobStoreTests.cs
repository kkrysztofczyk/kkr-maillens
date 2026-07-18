using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class EncryptedBlobStoreTests
{
    [TestMethod]
    public void Put_DeduplicatesEncryptsAndDecryptsContent()
    {
        using var db = new TestDatabase();
        string root = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            byte[] plaintext = Encoding.UTF8.GetBytes("Neutralny tekst wiadomości używany do testowania indeksu");
            var store = new EncryptedBlobStore(root, new string('A', 64));

            StoredBlob first = store.Put(db.Connection, plaintext);
            StoredBlob second = store.Put(db.Connection, plaintext);

            Assert.AreEqual(first.Id, second.Id);
            Assert.AreEqual(2, first.EncryptionVersion);
            Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM stored_blobs;"));
            string path = Path.Combine(root, first.EncryptedPath.Replace('/', Path.DirectorySeparatorChar));
            byte[] encrypted = File.ReadAllBytes(path);
            Assert.IsFalse(Encoding.UTF8.GetString(encrypted).Contains("Neutralny tekst", StringComparison.Ordinal));
            CollectionAssert.AreEqual(plaintext, store.Read(first));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [TestMethod]
    public void Read_WithDifferentSessionKeyIsRejected()
    {
        using var db = new TestDatabase();
        string root = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new EncryptedBlobStore(root, new string('A', 64));
            StoredBlob blob = writer.Put(db.Connection, "Neutralny tekst"u8);
            var reader = new EncryptedBlobStore(root, new string('B', 64));

            Assert.Throws<CryptographicException>(() => reader.Read(blob));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [TestMethod]
    public void Read_LegacyVersionOneBlobRemainsSupported()
    {
        using var db = new TestDatabase();
        string root = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            byte[] plaintext = "Neutralny tekst legacy"u8.ToArray();
            const string sessionKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            StoredBlob legacy = WriteLegacyBlob(db, root, sessionKey, plaintext);
            var store = new EncryptedBlobStore(root, sessionKey);

            CollectionAssert.AreEqual(plaintext, store.Read(legacy));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [TestMethod]
    public void Read_VersionTwoAuthenticatesVersionByte()
    {
        using var db = new TestDatabase();
        string root = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new EncryptedBlobStore(root, new string('A', 64));
            StoredBlob blob = store.Put(db.Connection, "Neutralny tekst"u8);
            string path = Path.Combine(root, blob.EncryptedPath.Replace('/', Path.DirectorySeparatorChar));
            byte[] encrypted = File.ReadAllBytes(path);
            encrypted[8] = 1;
            File.WriteAllBytes(path, encrypted);

            Assert.Throws<CryptographicException>(() => store.Read(blob with { EncryptionVersion = 1 }));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [TestMethod]
    public void MoveIntoPlace_KeepsExistingContentWithoutRaceError()
    {
        string root = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string destination = Path.Combine(root, "record.blob");
            string first = Path.Combine(root, "first.tmp");
            string second = Path.Combine(root, "second.tmp");
            File.WriteAllText(first, "first");
            File.WriteAllText(second, "second");

            EncryptedBlobStore.MoveIntoPlace(first, destination);
            EncryptedBlobStore.MoveIntoPlace(second, destination);

            Assert.AreEqual("first", File.ReadAllText(destination));
            Assert.IsTrue(File.Exists(second));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [TestMethod]
    public void Read_RejectsPathTraversalBeforeOpeningFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new EncryptedBlobStore(root, new string('A', 64));
            var blob = new StoredBlob(1, new string('a', 64), "../outside.blob", 1, 2);

            Assert.Throws<InvalidDataException>(() => store.Read(blob));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    static StoredBlob WriteLegacyBlob(TestDatabase db, string root, string sessionKeyHex, byte[] plaintext)
    {
        byte[] magic = "KKRMLB01"u8.ToArray();
        byte[] sessionKey = Convert.FromHexString(sessionKeyHex);
        byte[] key = DeriveLegacyKey(sessionKey, "KKR-MailLens-AttachmentStore-v1");
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag, magic);
        byte[] encrypted = new byte[magic.Length + 1 + nonce.Length + tag.Length + ciphertext.Length];
        magic.CopyTo(encrypted, 0);
        encrypted[magic.Length] = 1;
        nonce.CopyTo(encrypted, magic.Length + 1);
        tag.CopyTo(encrypted, magic.Length + 1 + nonce.Length);
        ciphertext.CopyTo(encrypted, magic.Length + 1 + nonce.Length + tag.Length);
        string hash = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
        string relative = Path.Combine(hash[..2], hash.Substring(2, 2), hash + ".blob");
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, encrypted);

        using var command = db.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO stored_blobs(sha256,encrypted_path,original_size,encryption_version,created_at)
            VALUES($hash,$path,$size,1,$now) RETURNING id;
            """;
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$path", relative.Replace('\\', '/'));
        command.Parameters.AddWithValue("$size", plaintext.Length);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        long id = Convert.ToInt64(command.ExecuteScalar());
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(sessionKey);
        CryptographicOperations.ZeroMemory(ciphertext);
        return new StoredBlob(id, hash, relative.Replace('\\', '/'), plaintext.Length, 1);
    }

    static byte[] DeriveLegacyKey(byte[] inputKey, string context)
    {
        byte[] salt = new byte[32];
        byte[] prk;
        using (var extract = new HMACSHA256(salt)) prk = extract.ComputeHash(inputKey);
        byte[] info = Encoding.UTF8.GetBytes(context);
        byte[] expandInput = new byte[info.Length + 1];
        info.CopyTo(expandInput, 0);
        expandInput[^1] = 1;
        try { using var expand = new HMACSHA256(prk); return expand.ComputeHash(expandInput); }
        finally
        {
            CryptographicOperations.ZeroMemory(prk);
            CryptographicOperations.ZeroMemory(expandInput);
        }
    }
}
