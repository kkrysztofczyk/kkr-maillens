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
}
