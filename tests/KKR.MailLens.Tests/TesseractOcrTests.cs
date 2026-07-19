using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class TesseractOcrTests
{
    [TestMethod]
    public async Task OcrPipeline_StreamsImageAndIndexesNeutralText()
    {
        using var db = new TestDatabase();
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string executable = CreateFakeTesseract(directory, delay: false);
            long attachmentId = AddImageAttachment(db);
            var store = new EncryptedBlobStore(Path.Combine(directory, "blobs"), new string('E', 64));
            byte[] image = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
            StoredBlob blob = store.Put(db.Connection, image);
            MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "application/octet-stream");
            long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
                db.Connection, attachmentId, blob.Sha256, "application/octet-stream");

            AttachmentExtractionOutcome outcome = AttachmentExtractionProcessor.Process(
                db.Connection, store, attachmentId, documentId);
            Assert.AreEqual("needs-ocr", outcome.Status);
            Assert.AreEqual("image/png", outcome.DetectedMimeType);

            await OcrAttachmentProcessor.ProcessAsync(db.Connection, store, attachmentId, documentId,
                new TesseractOptions(executable, "pol+eng", TimeSpan.FromSeconds(5)), CancellationToken.None);

            Assert.AreEqual("completed", db.ScalarText("SELECT status FROM content_documents;"));
            Assert.AreEqual("ocr", db.ScalarText("SELECT document_kind FROM content_documents;"));
            Assert.AreEqual("tesseract", db.ScalarText("SELECT extractor_name FROM content_documents;"));
            Assert.AreEqual("pol+eng", db.ScalarText("SELECT model_name FROM content_documents;"));
            Assert.AreEqual("Neutralny tekst OCR", db.ScalarText("SELECT clean_text FROM content_segments;"));
            Assert.HasCount(1, ContentSearch.Search(db.Connection, "neutralny OCR"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task OcrPipeline_BlankImageCompletesWithoutSegments()
    {
        using var db = new TestDatabase();
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string executable = CreateBlankTesseract(directory);
            long attachmentId = AddImageAttachment(db);
            var store = new EncryptedBlobStore(Path.Combine(directory, "blobs"), new string('E', 64));
            byte[] image = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
            StoredBlob blob = store.Put(db.Connection, image);
            MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "image/png");
            long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
                db.Connection, attachmentId, blob.Sha256, "image/png");
            AttachmentExtractionProcessor.Process(db.Connection, store, attachmentId, documentId);

            await OcrAttachmentProcessor.ProcessAsync(db.Connection, store, attachmentId, documentId,
                new TesseractOptions(executable, "pol+eng", TimeSpan.FromSeconds(5)), CancellationToken.None);

            Assert.AreEqual("completed", db.ScalarText("SELECT status FROM content_documents;"));
            Assert.AreEqual("extracted", db.ScalarText("SELECT processing_status FROM mail_attachments;"));
            Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM content_segments;"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Engine_KillsProcessAfterTimeout()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string executable = CreateFakeTesseract(directory, delay: true);
            var engine = new TesseractOcrEngine(new TesseractOptions(
                executable, "pol+eng", TimeSpan.FromMilliseconds(200)));

            await Assert.ThrowsExactlyAsync<TimeoutException>(() => engine.ExtractAsync(
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Engine_PreservesStderrWhenChildClosesInputPipe()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string executable = Path.Combine(directory, "failed-tesseract.cmd");
            File.WriteAllText(executable,
                "@echo off\r\necho Neutralny blad procesu OCR 1>&2\r\nexit /b 7\r\n");
            var engine = new TesseractOcrEngine(new TesseractOptions(
                executable, "pol+eng", TimeSpan.FromSeconds(5)));
            byte[] image = new byte[8 * 1024 * 1024];

            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => engine.ExtractAsync(image, "image/png"));

            StringAssert.Contains(error.Message, "Neutralny blad procesu OCR");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Engine_PreservesUtf8TextWithPolishCharacters()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string text = "Zażółć gęślą jaźń — ćma";
            string payload = Path.Combine(directory, "payload.txt");
            File.WriteAllText(payload, text, new System.Text.UTF8Encoding(false));
            string executable = Path.Combine(directory, "utf8-tesseract.cmd");
            File.WriteAllText(executable, $"@echo off\r\nmore >nul\r\ntype \"{payload}\"\r\n");
            var engine = new TesseractOcrEngine(new TesseractOptions(
                executable, "pol+eng", TimeSpan.FromSeconds(5)));

            ExtractionResult result = await engine.ExtractAsync(
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png");

            StringAssert.Contains(result.RawText, text);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    static string CreateFakeTesseract(string directory, bool delay)
    {
        string path = Path.Combine(directory, delay ? "slow-tesseract.cmd" : "fake-tesseract.cmd");
        string pause = delay ? "ping 127.0.0.1 -n 6 >nul\r\n" : "";
        File.WriteAllText(path, $"@echo off\r\n{pause}more >nul\r\necho Neutralny tekst OCR\r\n");
        return path;
    }

    static string CreateBlankTesseract(string directory)
    {
        string path = Path.Combine(directory, "blank-tesseract.cmd");
        File.WriteAllText(path, "@echo off\r\nmore >nul\r\n");
        return path;
    }

    static long AddImageAttachment(TestDatabase db)
    {
        GmailAccountRecord account = db.AddAccount();
        var part = new GmailApiPart
        {
            PartId = "2",
            MimeType = "image/png",
            Filename = "record.png",
            AttachmentId = "attachment-image",
            Size = 12,
            Headers = [new GmailHeader("Content-Disposition", "attachment")],
        };
        GmailStoredMessage message = GmailMessageMapper.Map(
            GmailTestMessage.Create("m-image", extraParts: [part]), account.Id);
        Corpus.Upsert(db.Connection, [GmailMessageMapper.ToHarvested(message)], "2026-01-01 00:00:00");
        MailAttachmentRepository.UpsertGmail(db.Connection, 1, [message]);
        return db.ScalarLong("SELECT id FROM mail_attachments;");
    }
}
