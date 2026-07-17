using System.Text;
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
    public void Extract_UnknownBinaryType_IsRejected()
    {
        var dispatcher = new ContentExtractionDispatcher();

        Assert.ThrowsExactly<NotSupportedException>(() =>
            dispatcher.Extract("record.bin", "application/octet-stream", [0, 1, 2, 3]));
    }
}
