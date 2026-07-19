using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

// Regresje utwardzenia sciezek przetwarzajacych wroga tresc pocztowa:
// HTML w tresci wiadomosci, archiwa OpenXML i zagniezdzenie czesci MIME.
[TestClass]
public sealed class HostileContentHardeningTests
{
    [TestMethod]
    public void HtmlToText_UnterminatedMarkupFloods_CompleteInBoundedTime()
    {
        string[] floods =
        [
            string.Concat(Enumerable.Repeat("<script>hostile ", 20_000)),
            string.Concat(Enumerable.Repeat("<!--hostile ", 25_000)),
            "<script>" + string.Concat(Enumerable.Repeat("</s", 100_000)),
            string.Concat(Enumerable.Repeat("<style>hostile ", 20_000)),
        ];

        foreach (string flood in floods)
        {
            var stopwatch = Stopwatch.StartNew();
            string result = GmailMessageMapper.HtmlToText(flood);
            stopwatch.Stop();
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"HtmlToText trwało {stopwatch.Elapsed} dla wejścia o długości {flood.Length}.");
            Assert.IsFalse(result.Contains("hostile", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void HtmlToText_UnclosedScriptAndStyle_DoNotLeakSource()
    {
        string result = GmailMessageMapper.HtmlToText(
            "<p>Widoczny tekst</p><script>var secret = 'tajna-wartosc';");
        StringAssert.Contains(result, "Widoczny tekst");
        Assert.IsFalse(result.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("tajna-wartosc", StringComparison.Ordinal));

        result = GmailMessageMapper.HtmlToText("<p>Widoczny tekst</p><style>.x{content:'tajny-css'}");
        StringAssert.Contains(result, "Widoczny tekst");
        Assert.IsFalse(result.Contains("tajny-css", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HtmlToText_AngleBracketInsideAttribute_KeepsFollowingText()
    {
        Assert.AreEqual("Neutralny link",
            GmailMessageMapper.HtmlToText("<a title=\"a > b\">Neutralny link</a>"));
    }

    [TestMethod]
    public void HtmlToText_ClosedBlocksAndBreaks_KeepExistingBehaviour()
    {
        string html = "<style>.x{display:none}</style><script>ignore()</script>"
            + "<p>Pierwsza linia</p><p>Druga <b>linia</b>&nbsp;tekstu</p>";
        string result = GmailMessageMapper.HtmlToText(html);
        Assert.AreEqual("Pierwsza linia\nDruga linia tekstu", result);
    }

    [TestMethod]
    public void Map_DeeplyNestedMimeParts_TruncatesAndRecordsNotice()
    {
        var current = new GmailApiPart
        {
            MimeType = "text/plain",
            Data = GmailTestMessage.Base64Url("Tekst z dna zagniezdzenia"),
            Headers = [new("Content-Type", "text/plain; charset=utf-8")],
        };
        for (int i = 0; i < 5_000; i++)
            current = new GmailApiPart { MimeType = "multipart/mixed", Parts = [current] };

        GmailApiMessage source = GmailTestMessage.Create("m-deep", extraParts: [current]);
        GmailStoredMessage result = GmailMessageMapper.Map(source, 1);

        StringAssert.Contains(result.BodyText, "Neutralny tekst");
        StringAssert.Contains(result.BodyText, "Pominięto zbyt głęboko zagnieżdżone części");
        Assert.IsFalse(result.BodyText.Contains("Tekst z dna zagniezdzenia", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Map_NestingWithinLimit_DoesNotRecordNotice()
    {
        var current = new GmailApiPart
        {
            MimeType = "text/plain",
            Data = GmailTestMessage.Base64Url("Tekst zagniezdzony plytko"),
            Headers = [new("Content-Type", "text/plain; charset=utf-8")],
        };
        for (int i = 0; i < 10; i++)
            current = new GmailApiPart { MimeType = "multipart/mixed", Parts = [current] };

        GmailStoredMessage result = GmailMessageMapper.Map(
            GmailTestMessage.Create("m-shallow", extraParts: [current]), 1);

        StringAssert.Contains(result.BodyText, "Tekst zagniezdzony plytko");
        Assert.IsFalse(result.BodyText.Contains("Pominięto", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_OversizedEntry_RejectedOnActualDecompressedBytes()
    {
        // Budzet egzekwowany jest na rzeczywistych bajtach dekompresji, nie na deklarowanym
        // Length z katalogu centralnego (ktory kontroluje nadawca).
        byte[] zip = CreateZip(("word/document.xml", new byte[200_000]));

        var options = new TextExtractionOptions(MaxArchiveExpandedBytes: 64 * 1024);
        var exception = Assert.ThrowsExactly<InvalidDataException>(
            () => OpenXmlArchiveSafety.Validate(zip, options));
        StringAssert.Contains(exception.Message, "po rozwinięciu");
    }

    [TestMethod]
    public void Validate_HighRatioEntry_RejectedOnActualDecompressedBytes()
    {
        // 200 kB zer kompresuje sie do ~0,2 kB (wspolczynnik ~950:1) — odrzucone przy limicie 10:1.
        byte[] zip = CreateZip(("word/document.xml", new byte[200_000]));

        var options = new TextExtractionOptions(MaxArchiveCompressionRatio: 10);
        var exception = Assert.ThrowsExactly<InvalidDataException>(
            () => OpenXmlArchiveSafety.Validate(zip, options));
        StringAssert.Contains(exception.Message, "limit kompresji");
    }

    [TestMethod]
    public void Validate_UnderstatedDeclaredSize_StaysBoundedByRuntimeCap()
    {
        // Wpis rozprezajacy sie do 200 kB klamie w naglowkach, deklarujac 16 bajtow.
        // Strumien odczytu ZipArchive w .NET obcina wynik do zadeklarowanej dlugosci,
        // wiec falszywie zanizony rozmiar nie moze dostarczyc bomby — walidacja przechodzi
        // bez wyjatku i szybko, bo do SDK trafia jedynie 16 bajtow.
        byte[] zip = CreateZip(("word/document.xml", new byte[200_000]));
        PatchDeclaredUncompressedSize(zip, 16);

        var stopwatch = Stopwatch.StartNew();
        OpenXmlArchiveSafety.Validate(zip, new TextExtractionOptions(MaxArchiveExpandedBytes: 64 * 1024));
        stopwatch.Stop();
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Walidacja trwała {stopwatch.Elapsed}.");
    }

    [TestMethod]
    public void Validate_LegitimateArchive_Passes()
    {
        OpenXmlArchiveSafety.Validate(
            CreateZip(("word/document.xml", "<w:document/>"u8.ToArray())),
            new TextExtractionOptions());
    }

    [TestMethod]
    public void Detect_DeclaredDenseCentralDirectory_ReturnsZipWithoutEnumeration()
    {
        byte[] zip = CreateZip(("data/entry.bin", new byte[16]));
        PatchEndOfCentralDirectoryEntryCount(zip, 60_000);

        var stopwatch = Stopwatch.StartNew();
        DetectedFile detected = FileTypeDetector.Detect("record.zip", null, zip);
        stopwatch.Stop();

        Assert.AreEqual("application/zip", detected.MimeType);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Rozpoznanie typu trwało {stopwatch.Elapsed}.");
    }

    [TestMethod]
    public void Detect_UnderstatedEntryCount_StopsAtBoundedEnumeration()
    {
        byte[] zip = CreateZip(Enumerable.Range(0, 5_000)
            .Select(i => ($"data/e{i:D5}.bin", Array.Empty<byte>())).ToArray());
        PatchEndOfCentralDirectoryEntryCount(zip, 1);

        DetectedFile detected = FileTypeDetector.Detect("record.zip", null, zip);
        Assert.AreEqual("application/zip", detected.MimeType);
    }

    [TestMethod]
    public void Validate_DeclaredDenseCentralDirectory_RejectsBeforeMaterialization()
    {
        byte[] zip = CreateZip(("word/document.xml", new byte[16]));
        PatchEndOfCentralDirectoryEntryCount(zip, 60_000);

        var exception = Assert.ThrowsExactly<InvalidDataException>(
            () => OpenXmlArchiveSafety.Validate(zip, new TextExtractionOptions()));
        StringAssert.Contains(exception.Message, "wpisów");
    }

    [TestMethod]
    public void Detect_RegularOpenXmlZip_StillRecognized()
    {
        Assert.AreEqual("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            FileTypeDetector.Detect("record.bin", null, CreateZip(("word/document.xml", new byte[16]))).MimeType);
        Assert.AreEqual("application/zip",
            FileTypeDetector.Detect("record.bin", null, CreateZip(("data/entry.bin", new byte[16]))).MimeType);
    }

    static byte[] CreateZip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                using Stream entry = archive.CreateEntry(name).Open();
                entry.Write(content);
            }
        }
        return stream.ToArray();
    }

    // Nadpisuje rozmiar po rozprezeniu w katalogu centralnym i naglowku lokalnym
    // pierwszego wpisu, symulujac archiwum klamiace w metadanych.
    static void PatchDeclaredUncompressedSize(byte[] zip, uint declared)
    {
        int endRecord = FindEndOfCentralDirectory(zip);
        int central = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(endRecord + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(central + 24), declared);
        int local = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(central + 42));
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(local + 22), declared);
    }

    static void PatchEndOfCentralDirectoryEntryCount(byte[] zip, ushort count)
    {
        int endRecord = FindEndOfCentralDirectory(zip);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(endRecord + 8), count);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(endRecord + 10), count);
    }

    static int FindEndOfCentralDirectory(byte[] zip)
    {
        for (int offset = zip.Length - 22; offset >= 0; offset--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(offset)) == 0x06054B50)
                return offset;
        throw new InvalidDataException("Brak rekordu końca katalogu centralnego w spreparowanym ZIP.");
    }
}
