using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class PdfOcrTests
{
    const string TextPage = "Neutralny tekst wiadomosci uzywany do testowania indeksu dokumentu PDF";

    [TestMethod]
    public void Extract_MixedPdf_IdentifiesOnlyBlankPageForOcr()
    {
        ExtractionResult result = new ContentExtractionDispatcher().Extract(
            "record.pdf", "application/pdf", CreatePdf(TextPage, ""));

        Assert.HasCount(1, result.Segments);
        Assert.AreEqual(1, result.Segments[0].PageNumber);
        CollectionAssert.AreEqual(new[] { 2 }, result.OcrPageNumbers.ToArray());
    }

    [TestMethod]
    public async Task Renderer_RendersOnlySelectedPageToPngInMemory()
    {
        var renderer = new PdfiumPageRenderer();
        IReadOnlyList<RenderedPdfPage> pages = await renderer.RenderAsync(
            CreatePdf(TextPage, ""), [2], new PdfRenderOptions(Dpi: 72, MaxPages: 1,
                Timeout: TimeSpan.FromSeconds(10)));

        try
        {
            Assert.HasCount(1, pages);
            Assert.AreEqual(2, pages[0].PageNumber);
            CollectionAssert.AreEqual(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                pages[0].PngBytes.Take(8).ToArray());
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(pages[0].PngBytes);
        }
    }

    [TestMethod]
    public async Task OcrPipeline_MergesTextAndScannedPagesAndIndexesBoth()
    {
        using var db = new TestDatabase();
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string executable = CreateFakeTesseract(directory);
            long attachmentId = AddPdfAttachment(db);
            var store = new EncryptedBlobStore(Path.Combine(directory, "blobs"), new string('F', 64));
            byte[] pdf = CreatePdf(TextPage, "");
            StoredBlob blob = store.Put(db.Connection, pdf);
            MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "application/pdf");
            long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
                db.Connection, attachmentId, blob.Sha256, "application/pdf");

            AttachmentExtractionOutcome outcome = AttachmentExtractionProcessor.Process(
                db.Connection, store, attachmentId, documentId);
            Assert.AreEqual("needs-ocr", outcome.Status);

            var renderer = new FakeRenderer();
            int heartbeats = 0;
            await OcrAttachmentProcessor.ProcessAsync(db.Connection, store, attachmentId, documentId,
                new TesseractOptions(executable, "pol+eng", TimeSpan.FromSeconds(5)),
                CancellationToken.None, renderer, new PdfRenderOptions(72, 5, TimeSpan.FromSeconds(5)),
                () => heartbeats++);

            Assert.AreEqual("completed", db.ScalarText("SELECT status FROM content_documents;"));
            Assert.AreEqual("attachment", db.ScalarText("SELECT document_kind FROM content_documents;"));
            Assert.AreEqual("pdfpig+tesseract", db.ScalarText("SELECT extractor_name FROM content_documents;"));
            Assert.AreEqual(2, db.ScalarLong("SELECT count(*) FROM content_segments;"));
            Assert.AreEqual(TextPage, db.ScalarText("SELECT clean_text FROM content_segments WHERE page_number=1;"));
            Assert.AreEqual("Neutralny tekst OCR strony", db.ScalarText(
                "SELECT clean_text FROM content_segments WHERE page_number=2;"));
            CollectionAssert.AreEqual(new[] { 2 }, renderer.RequestedPages.ToArray());
            Assert.IsGreaterThanOrEqualTo(2, heartbeats);
            Assert.HasCount(1, ContentSearch.Search(db.Connection, "testowania indeksu"));
            Assert.HasCount(1, ContentSearch.Search(db.Connection, "OCR strony"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task OcrPipeline_BlankScannedPageKeepsTextPagesAndCompletes()
    {
        using var db = new TestDatabase();
        string directory = Path.Combine(Path.GetTempPath(), "kkr-maillens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string executable = Path.Combine(directory, "blank-pdf-tesseract.cmd");
            File.WriteAllText(executable, "@echo off\r\nmore >nul\r\n");
            long attachmentId = AddPdfAttachment(db);
            var store = new EncryptedBlobStore(Path.Combine(directory, "blobs"), new string('F', 64));
            StoredBlob blob = store.Put(db.Connection, CreatePdf(TextPage, ""));
            MailAttachmentRepository.MarkDownloaded(db.Connection, attachmentId, blob, "application/pdf");
            long documentId = ContentDocumentRepository.EnsureAttachmentDocument(
                db.Connection, attachmentId, blob.Sha256, "application/pdf");
            AttachmentExtractionProcessor.Process(db.Connection, store, attachmentId, documentId);

            await OcrAttachmentProcessor.ProcessAsync(db.Connection, store, attachmentId, documentId,
                new TesseractOptions(executable, "pol+eng", TimeSpan.FromSeconds(5)),
                CancellationToken.None, new FakeRenderer(),
                new PdfRenderOptions(72, 5, TimeSpan.FromSeconds(5)));

            Assert.AreEqual("completed", db.ScalarText("SELECT status FROM content_documents;"));
            Assert.AreEqual(1, db.ScalarLong("SELECT count(*) FROM content_segments;"));
            Assert.AreEqual(TextPage, db.ScalarText("SELECT clean_text FROM content_segments WHERE page_number=1;"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    static string CreateFakeTesseract(string directory)
    {
        string path = Path.Combine(directory, "fake-pdf-tesseract.cmd");
        File.WriteAllText(path, "@echo off\r\nmore >nul\r\necho Neutralny tekst OCR strony\r\n");
        return path;
    }

    static long AddPdfAttachment(TestDatabase db)
    {
        GmailAccountRecord account = db.AddAccount();
        var part = new GmailApiPart
        {
            PartId = "2",
            MimeType = "application/pdf",
            Filename = "record.pdf",
            AttachmentId = "attachment-pdf",
            Size = 128,
            Headers = [new GmailHeader("Content-Disposition", "attachment")],
        };
        GmailStoredMessage message = GmailMessageMapper.Map(
            GmailTestMessage.Create("m-pdf", extraParts: [part]), account.Id);
        Corpus.Upsert(db.Connection, [GmailMessageMapper.ToHarvested(message)], "2026-01-01 00:00:00");
        MailAttachmentRepository.UpsertGmail(db.Connection, 1, [message]);
        return db.ScalarLong("SELECT id FROM mail_attachments;");
    }

    static byte[] CreatePdf(params string[] pageTexts)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(3, pageTexts.Length).Select(number => $"{number} 0 R"))}] /Count {pageTexts.Length} >>",
        };
        int firstContent = 3 + pageTexts.Length;
        int font = firstContent + pageTexts.Length;
        for (int index = 0; index < pageTexts.Length; index++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + $"/Resources << /Font << /F1 {font} 0 R >> >> /Contents {firstContent + index} 0 R >>");
        }
        foreach (string text in pageTexts)
        {
            string escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
            string content = text.Length == 0 ? "" : $"BT /F1 12 Tf 72 720 Td ({escaped}) Tj ET";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    sealed class FakeRenderer : IPdfPageRenderer
    {
        public List<int> RequestedPages { get; } = [];

        public Task<IReadOnlyList<RenderedPdfPage>> RenderAsync(byte[] pdf,
            IReadOnlyList<int> pageNumbers, PdfRenderOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedPages.AddRange(pageNumbers);
            IReadOnlyList<RenderedPdfPage> result = pageNumbers
                .Select(page => new RenderedPdfPage(page,
                    [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
                .ToArray();
            return Task.FromResult(result);
        }
    }
}
