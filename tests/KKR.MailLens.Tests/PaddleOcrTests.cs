using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class PaddleOcrTests
{
    static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [TestMethod]
    public async Task OcrPipeline_UsesPaddleOnlyWhenTesseractReturnsNoText()
    {
        using var db = new TestDatabase();
        string directory = CreateDirectory();
        try
        {
            string tesseract = WriteCommand(directory, "blank-tesseract.cmd", "more >nul");
            string paddle = WriteCommand(directory, "fake-python.cmd",
                "echo {\"text\":\"Neutralny tekst PaddleOCR\"}\r\nmore >nul");
            string runner = Path.Combine(directory, "runner.py");
            File.WriteAllText(runner, "# neutral test adapter placeholder");
            (long attachmentId, long documentId, EncryptedBlobStore store) = AddImage(db, directory);

            await OcrAttachmentProcessor.ProcessAsync(db.Connection, store, attachmentId, documentId,
                new TesseractOptions(tesseract, Timeout: TimeSpan.FromSeconds(5)), CancellationToken.None,
                fallbackOptions: new PaddleOcrOptions(paddle, runner, Timeout: TimeSpan.FromSeconds(5)));

            Assert.AreEqual("paddleocr-fallback",
                db.ScalarText("SELECT extractor_name FROM content_documents;"));
            Assert.AreEqual("PP-OCRv6:pl:cpu", db.ScalarText("SELECT model_name FROM content_documents;"));
            Assert.AreEqual("Neutralny tekst PaddleOCR",
                db.ScalarText("SELECT clean_text FROM content_segments;"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task OcrPipeline_DoesNotStartPaddleWhenTesseractHasText()
    {
        using var db = new TestDatabase();
        string directory = CreateDirectory();
        try
        {
            string tesseract = WriteCommand(directory, "tesseract.cmd",
                "more >nul\r\necho Test Record");
            string runner = Path.Combine(directory, "runner.py");
            File.WriteAllText(runner, "# must not be started");
            (long attachmentId, long documentId, EncryptedBlobStore store) = AddImage(db, directory);

            await OcrAttachmentProcessor.ProcessAsync(db.Connection, store, attachmentId, documentId,
                new TesseractOptions(tesseract, Timeout: TimeSpan.FromSeconds(5)), CancellationToken.None,
                fallbackOptions: new PaddleOcrOptions(
                    Path.Combine(directory, "missing-python.exe"), runner, Timeout: TimeSpan.FromSeconds(5)));

            Assert.AreEqual("tesseract", db.ScalarText("SELECT extractor_name FROM content_documents;"));
            Assert.AreEqual("Test Record", db.ScalarText("SELECT clean_text FROM content_segments;"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Engine_RejectsInvalidAdapterJson()
    {
        string directory = CreateDirectory();
        try
        {
            string paddle = WriteCommand(directory, "invalid-python.cmd",
                "echo invalid-json\r\nmore >nul");
            string runner = Path.Combine(directory, "runner.py");
            File.WriteAllText(runner, "# neutral test adapter placeholder");
            using var engine = new PaddleOcrEngine(new PaddleOcrOptions(
                paddle, runner, Timeout: TimeSpan.FromSeconds(5)));

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => engine.ExtractAsync(
                PngHeader, "image/png"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Engine_ReusesRunnerProcessAcrossPages()
    {
        string directory = CreateDirectory();
        try
        {
            string paddle = WriteCommand(directory, "fake-python.cmd",
                "echo started>>\"%~dp0starts.log\"\r\n"
                + "echo {\"text\":\"Strona testowa\"}\r\n"
                + "echo {\"text\":\"Strona testowa\"}\r\n"
                + "more >nul");
            string runner = Path.Combine(directory, "runner.py");
            File.WriteAllText(runner, "# neutral test adapter placeholder");
            using var engine = new PaddleOcrEngine(new PaddleOcrOptions(
                paddle, runner, Timeout: TimeSpan.FromSeconds(5)));

            ExtractionResult first = await engine.ExtractAsync(PngHeader, "image/png");
            ExtractionResult second = await engine.ExtractAsync(PngHeader, "image/png");

            Assert.AreEqual("Strona testowa", first.CleanText);
            Assert.AreEqual("Strona testowa", second.CleanText);
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(directory, "starts.log")).Length,
                "Dwie strony powinny użyć jednego procesu adaptera.");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Engine_RestartsRunnerWhenItDiesWithoutResponse()
    {
        string directory = CreateDirectory();
        try
        {
            string paddle = WriteCommand(directory, "fake-python.cmd",
                "echo started>>\"%~dp0starts.log\"\r\n"
                + "if exist \"%~dp0crashed.flag\" (\r\n"
                + "echo {\"text\":\"Restart dziala\"}\r\n"
                + "more >nul\r\n"
                + ") else (\r\n"
                + "echo flag>\"%~dp0crashed.flag\"\r\n"
                + ")");
            string runner = Path.Combine(directory, "runner.py");
            File.WriteAllText(runner, "# neutral test adapter placeholder");
            using var engine = new PaddleOcrEngine(new PaddleOcrOptions(
                paddle, runner, Timeout: TimeSpan.FromSeconds(10)));

            ExtractionResult result = await engine.ExtractAsync(PngHeader, "image/png");

            Assert.AreEqual("Restart dziala", result.CleanText);
            Assert.AreEqual(2, File.ReadAllLines(Path.Combine(directory, "starts.log")).Length,
                "Po padzie adaptera strona powinna zostać ponowiona w nowym procesie.");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Engine_TimesOutWhenRunnerStaysSilent()
    {
        string directory = CreateDirectory();
        try
        {
            string paddle = WriteCommand(directory, "silent-python.cmd", "more >nul");
            string runner = Path.Combine(directory, "runner.py");
            File.WriteAllText(runner, "# neutral test adapter placeholder");
            using var engine = new PaddleOcrEngine(new PaddleOcrOptions(
                paddle, runner, Timeout: TimeSpan.FromSeconds(2)));

            await Assert.ThrowsExactlyAsync<TimeoutException>(() => engine.ExtractAsync(
                PngHeader, "image/png"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    static (long AttachmentId, long DocumentId, EncryptedBlobStore Store) AddImage(
        TestDatabase db, string directory)
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
            GmailTestMessage.Create("m-paddle", extraParts: [part]), account.Id);
        Corpus.Upsert(db.Connection, [GmailMessageMapper.ToHarvested(message)], "2026-01-01 00:00:00");
        MailAttachmentRepository.UpsertGmail(db.Connection, 1, [message]);
        long attachmentId = db.ScalarLong("SELECT id FROM mail_attachments;");
        var store = new EncryptedBlobStore(Path.Combine(directory, "blobs"), new string('A', 64));
        byte[] image = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
        StoredBlob blob = store.Put(db.Connection, image);
        MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "image/png");
        long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
            db.Connection, attachmentId, blob.Sha256, "image/png");
        AttachmentExtractionProcessor.Process(db.Connection, store, attachmentId, documentId);
        return (attachmentId, documentId, store);
    }

    static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    static string WriteCommand(string directory, string filename, string body)
    {
        string path = Path.Combine(directory, filename);
        File.WriteAllText(path, $"@echo off\r\n{body}\r\n");
        return path;
    }
}
