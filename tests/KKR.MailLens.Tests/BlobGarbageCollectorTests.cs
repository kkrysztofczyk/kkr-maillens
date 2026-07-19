using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class BlobGarbageCollectorTests
{
    [TestMethod]
    public void Collect_DeletesBlobOnlyAfterLastSharedReferenceIsRemoved()
    {
        using var db = new TestDatabase();
        string root = TemporaryRoot();
        try
        {
            GmailAccountRecord account = db.AddAccount();
            (long firstAttachment, string firstMail) = AddAttachment(db, account, "message-one", "attachment-one");
            (long secondAttachment, string secondMail) = AddAttachment(db, account, "message-two", "attachment-two");
            var store = new EncryptedBlobStore(root, new string('A', 64));
            StoredBlob blob = store.Put(db.Connection, "Neutralny tekst współdzielonego pliku"u8);
            MailAttachmentRepository.MarkDownloaded(db.Connection, firstAttachment, blob, "text/plain");
            MailAttachmentRepository.MarkDownloaded(db.Connection, secondAttachment, blob, "text/plain");
            string path = Path.Combine(root, blob.EncryptedPath.Replace('/', Path.DirectorySeparatorChar));

            DeleteMail(db, firstMail);
            BlobGarbageCollectionResult retained = BlobGarbageCollector.Collect(db.Connection, root,
                safetyWindow: TimeSpan.Zero);

            Assert.AreEqual(0, retained.Orphaned);
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM stored_blobs;"));

            DeleteMail(db, secondMail);
            BlobGarbageCollectionResult collected = BlobGarbageCollector.Collect(db.Connection, root,
                safetyWindow: TimeSpan.Zero);

            Assert.AreEqual(1, collected.Orphaned);
            Assert.AreEqual(1, collected.Deleted);
            Assert.AreEqual(blob.OriginalSize, collected.ReclaimedBytes);
            Assert.IsFalse(File.Exists(path));
            Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM stored_blobs;"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [TestMethod]
    public void Collect_ClearsDeletedAttachmentPipelineStateForSafeRedownload()
    {
        using var db = new TestDatabase();
        string root = TemporaryRoot();
        try
        {
            GmailAccountRecord account = db.AddAccount();
            (long attachmentId, _) = AddAttachment(db, account, "message-state", "attachment-state");
            var store = new EncryptedBlobStore(root, new string('A', 64));
            StoredBlob blob = store.Put(db.Connection, "Neutralny tekst stanu"u8);
            MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "text/plain");
            long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
                db.Connection, attachmentId, blob.Sha256, "text/plain");
            ContentDocumentRepository.SaveExtraction(db.Connection, documentId,
                new ContentExtractionDispatcher().Extract("record.txt", "text/plain", "Neutralny tekst stanu"u8.ToArray()),
                "plain-text", "1");
            SetAttachmentDeleted(db, attachmentId);

            BlobGarbageCollectionResult result = BlobGarbageCollector.Collect(db.Connection, root,
                safetyWindow: TimeSpan.Zero);

            Assert.AreEqual(1, result.Deleted);
            Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM content_documents;"));
            Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM content_segments;"));
            Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM content_fts;"));
            Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM processing_jobs;"));
            Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM mail_attachments WHERE blob_id IS NOT NULL;"));
            Assert.AreEqual("metadata-only", db.ScalarText("SELECT download_status FROM mail_attachments;"));
            Assert.AreEqual("pending", db.ScalarText("SELECT processing_status FROM mail_attachments;"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [TestMethod]
    public void Preview_DoesNotIncludeBlobUsedByRunningJob()
    {
        using var db = new TestDatabase();
        string root = TemporaryRoot();
        try
        {
            GmailAccountRecord account = db.AddAccount();
            (long attachmentId, _) = AddAttachment(db, account, "message-running", "attachment-running");
            var store = new EncryptedBlobStore(root, new string('A', 64));
            StoredBlob blob = store.Put(db.Connection, "Neutralny tekst aktywnego zadania"u8);
            MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "text/plain");
            SetAttachmentDeleted(db, attachmentId);
            using (var command = db.Connection.CreateCommand())
            {
                command.CommandText = "UPDATE processing_jobs SET status='running',locked_by='worker-test';";
                command.ExecuteNonQuery();
            }

            BlobGarbageCollectionResult preview = BlobGarbageCollector.Preview(db.Connection,
                safetyWindow: TimeSpan.Zero);

            Assert.AreEqual(0, preview.Orphaned);
            Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM stored_blobs;"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [TestMethod]
    public void Collect_LeavesFreshUnreferencedBlobUntilDownloadAttachesIt()
    {
        using var db = new TestDatabase();
        string root = TemporaryRoot();
        try
        {
            GmailAccountRecord account = db.AddAccount();
            (long attachmentId, _) = AddAttachment(db, account, "message-race", "attachment-race");
            var store = new EncryptedBlobStore(root, new string('A', 64));
            StoredBlob blob = store.Put(db.Connection, "Neutralny tekst pobierania w toku"u8);
            string path = Path.Combine(root, blob.EncryptedPath.Replace('/', Path.DirectorySeparatorChar));

            BlobGarbageCollectionResult result = BlobGarbageCollector.Collect(db.Connection, root);

            Assert.AreEqual(0, result.Orphaned);
            Assert.AreEqual(0, result.Deleted);
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM stored_blobs;"));

            MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "text/plain");

            Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM mail_attachments WHERE blob_id IS NOT NULL;"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [TestMethod]
    public void Collect_KeepsFileWhenBlobRowDeleteFails()
    {
        using var db = new TestDatabase();
        string root = TemporaryRoot();
        try
        {
            GmailAccountRecord account = db.AddAccount();
            (long attachmentId, string mail) = AddAttachment(db, account, "message-order", "attachment-order");
            var store = new EncryptedBlobStore(root, new string('A', 64));
            StoredBlob blob = store.Put(db.Connection, "Neutralny tekst kolejności usuwania"u8);
            MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "text/plain");
            string path = Path.Combine(root, blob.EncryptedPath.Replace('/', Path.DirectorySeparatorChar));
            DeleteMail(db, mail);
            Execute(db, """
                CREATE TRIGGER test_block_blob_delete BEFORE DELETE ON stored_blobs
                BEGIN SELECT RAISE(ABORT,'blokada testowa'); END;
                """);
            var warnings = new List<string>();

            BlobGarbageCollectionResult blocked = BlobGarbageCollector.Collect(db.Connection, root,
                warnings.Add, safetyWindow: TimeSpan.Zero);

            Assert.AreEqual(1, blocked.Orphaned);
            Assert.AreEqual(0, blocked.Deleted);
            Assert.AreEqual(1, blocked.Failed);
            Assert.AreEqual(1, warnings.Count);
            Assert.IsTrue(File.Exists(path));
            Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM stored_blobs;"));

            Execute(db, "DROP TRIGGER test_block_blob_delete;");
            BlobGarbageCollectionResult collected = BlobGarbageCollector.Collect(db.Connection, root,
                warnings.Add, safetyWindow: TimeSpan.Zero);

            Assert.AreEqual(1, collected.Deleted);
            Assert.IsFalse(File.Exists(path));
            Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM stored_blobs;"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [TestMethod]
    public void BlobIntegrityTriggers_FailLoudlyOnDanglingReferenceAndGuardedDelete()
    {
        using var db = new TestDatabase();
        string root = TemporaryRoot();
        try
        {
            GmailAccountRecord account = db.AddAccount();
            (long attachmentId, _) = AddAttachment(db, account, "message-fk", "attachment-fk");
            var dangling = new StoredBlob(12_345, new string('a', 64), "aa/bb/missing.blob", 10, 2);

            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
                MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, dangling, "text/plain"));

            var store = new EncryptedBlobStore(root, new string('A', 64));
            StoredBlob blob = store.Put(db.Connection, "Neutralny tekst integralności"u8);
            MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "text/plain");

            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
                Execute(db, "DELETE FROM stored_blobs;"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    static void Execute(TestDatabase db, string sql)
    {
        using var command = db.Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static (long AttachmentId, string MailEntryId) AddAttachment(TestDatabase db, GmailAccountRecord account,
        string messageId, string attachmentKey)
    {
        var part = new GmailApiPart
        {
            PartId = "2",
            MimeType = "text/plain",
            Filename = "record.txt",
            AttachmentId = attachmentKey,
            Size = 64,
            Headers = [new GmailHeader("Content-Disposition", "attachment")],
        };
        GmailStoredMessage message = GmailMessageMapper.Map(
            GmailTestMessage.Create(messageId, extraParts: [part]), account.Id);
        Corpus.Upsert(db.Connection, [GmailMessageMapper.ToHarvested(message)], "2026-01-01 00:00:00");
        MailAttachmentRepository.UpsertGmail(db.Connection, 1, [message]);
        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT id FROM mail_attachments WHERE mail_entry_id=$mail;";
        command.Parameters.AddWithValue("$mail", message.EntryId);
        return (Convert.ToInt64(command.ExecuteScalar()), message.EntryId);
    }

    static void DeleteMail(TestDatabase db, string entryId)
    {
        using var command = db.Connection.CreateCommand();
        command.CommandText = "DELETE FROM mails WHERE entry_id=$id;";
        command.Parameters.AddWithValue("$id", entryId);
        command.ExecuteNonQuery();
    }

    static void SetAttachmentDeleted(TestDatabase db, long attachmentId)
    {
        using var command = db.Connection.CreateCommand();
        command.CommandText = "UPDATE mail_attachments SET is_deleted=1 WHERE id=$id;";
        command.Parameters.AddWithValue("$id", attachmentId);
        command.ExecuteNonQuery();
    }

    static string TemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
}
