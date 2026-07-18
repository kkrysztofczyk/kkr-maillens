using System.Text;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class ContentExtractionTests
{
    [TestMethod]
    public void Extract_TxtUtf8_NormalizesText()
    {
        byte[] content = Encoding.UTF8.GetBytes("  Neutralny tekst\r\n\r\n\r\n\r\n  Test Record  ");

        ExtractionResult result = new ContentExtractionDispatcher().Extract("record.txt", "text/plain", content);

        Assert.AreEqual("text/plain", result.DetectedMimeType);
        Assert.AreEqual("Neutralny tekst\n\nTest Record", result.CleanText);
        Assert.IsFalse(result.WasTruncated);
    }

    [TestMethod]
    public void Extract_TxtUtf16Bom_DecodesText()
    {
        byte[] content = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("Neutralny tekst wiadomości")).ToArray();

        ExtractionResult result = new ContentExtractionDispatcher().Extract("record.txt", null, content);

        Assert.AreEqual("Neutralny tekst wiadomości", result.CleanText);
    }

    [TestMethod]
    public void Extract_TxtWindows1250_DecodesFallbackText()
    {
        byte[] content = [.. Encoding.ASCII.GetBytes("Neutralny tekst "), 0xB9];

        ExtractionResult result = new ContentExtractionDispatcher().Extract("record.txt", "text/plain", content);

        Assert.AreEqual("Neutralny tekst ą", result.CleanText);
    }

    [TestMethod]
    public void Extract_Html_RemovesMarkupScriptsAndStyles()
    {
        byte[] content = Encoding.UTF8.GetBytes(
            "<!doctype html><html><style>.x{display:none}</style><script>ignore()</script><body><p>Neutralny <b>tekst</b></p></body></html>");

        ExtractionResult result = new ContentExtractionDispatcher().Extract("record.bin", "application/octet-stream", content);

        Assert.AreEqual("text/html", result.DetectedMimeType);
        Assert.AreEqual("Neutralny tekst", result.CleanText);
        Assert.IsFalse(result.CleanText.Contains("ignore", StringComparison.Ordinal));
        Assert.IsFalse(result.RawText.Contains("<script", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Extract_TruncatesAtConfiguredCharacterLimit()
    {
        var dispatcher = new ContentExtractionDispatcher(options: new TextExtractionOptions(MaxCharacters: 8));

        ExtractionResult result = dispatcher.Extract("record.txt", "text/plain", Encoding.UTF8.GetBytes("neutralny tekst"));

        Assert.AreEqual("neutraln", result.CleanText);
        Assert.IsTrue(result.WasTruncated);
    }

    [TestMethod]
    public void Extract_TruncationDoesNotSplitUnicodeSurrogatePair()
    {
        var dispatcher = new ContentExtractionDispatcher(options: new TextExtractionOptions(MaxCharacters: 2));

        ExtractionResult result = dispatcher.Extract(
            "record.txt", "text/plain", Encoding.UTF8.GetBytes("A😀B"));

        Assert.AreEqual("A", result.CleanText);
        Assert.IsTrue(result.WasTruncated);
        Assert.IsFalse(result.RawText.Any(char.IsSurrogate));
    }

    [TestMethod]
    public void ResultBuilderAppliesIndependentRawAndCleanBudgets()
    {
        ExtractionResult result = ExtractionResultBuilder.Build("text/plain",
            [new SegmentDraft(" A "), new SegmentDraft(" B ")],
            new TextExtractionOptions(MaxCharacters: 5));

        Assert.IsLessThanOrEqualTo(5, result.RawText.Length);
        Assert.IsLessThanOrEqualTo(5, result.CleanText.Length);
        Assert.IsTrue(result.WasTruncated);
    }

    [TestMethod]
    public void Extract_UnknownBinaryType_IsRejected()
    {
        var dispatcher = new ContentExtractionDispatcher();

        Assert.ThrowsExactly<NotSupportedException>(() =>
            dispatcher.Extract("record.bin", "application/octet-stream", [0, 1, 2, 3]));
    }

    [TestMethod]
    public void Extract_PdfBeyondConfiguredByteLimit_IsRejectedBeforeParsing()
    {
        var dispatcher = new ContentExtractionDispatcher(options: new TextExtractionOptions(MaxBytes: 8));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            dispatcher.Extract("record.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4 neutral")));
    }

    [TestMethod]
    public void Extract_OpenXmlBeyondExpandedLimit_IsRejectedBeforePackageParsing()
    {
        byte[] archive;
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), Encoding.UTF8);
                writer.Write(new string('A', 2_048));
            }
            archive = stream.ToArray();
        }
        var dispatcher = new ContentExtractionDispatcher(options:
            new TextExtractionOptions(MaxArchiveExpandedBytes: 1_024));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            dispatcher.Extract("record.docx", "application/octet-stream", archive));
    }
}
