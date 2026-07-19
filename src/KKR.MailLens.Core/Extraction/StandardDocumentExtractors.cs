using DocumentFormat.OpenXml.Packaging;
using System.IO.Compression;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace KKR.MailLens;

sealed class PdfTextExtractor : IContentExtractor
{
    public bool CanExtract(DetectedFile file) => file.MimeType == "application/pdf";

    public ExtractionResult Extract(DetectedFile file, TextExtractionOptions options)
    {
        ExtractionLimits.Validate(file, options);
        using PdfDocument document = PdfDocument.Open(file.Content);
        var pages = document.GetPages()
            .Select(page => (page.Number, page.Text))
            .ToArray();
        ExtractionResult result = ExtractionResultBuilder.Build(file.MimeType,
            pages.Select(page => new SegmentDraft(page.Text, PageNumber: page.Number)), options);
        return result with
        {
            OcrPageNumbers = pages
                .Where(page => PdfTextQuality.NeedsOcr(page.Text))
                .Select(page => page.Number)
                .ToArray(),
        };
    }
}

static class PdfTextQuality
{
    const int MinimumAlphaNumericCharacters = 30;

    public static bool NeedsOcr(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        string clean = TextNormalizer.Normalize(text);
        int alphaNumeric = clean.Count(char.IsLetterOrDigit);
        int replacementCharacters = clean.Count(character => character == '\uFFFD');
        return alphaNumeric < MinimumAlphaNumericCharacters
            || replacementCharacters > Math.Max(2, clean.Length / 20);
    }
}

sealed class WordExtractor : IContentExtractor
{
    const string MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public bool CanExtract(DetectedFile file) => file.MimeType == MimeType;

    public ExtractionResult Extract(DetectedFile file, TextExtractionOptions options)
    {
        ExtractionLimits.Validate(file, options);
        OpenXmlArchiveSafety.Validate(file.Content, options);
        using var stream = new MemoryStream(file.Content, writable: false);
        using WordprocessingDocument document = WordprocessingDocument.Open(stream, false);
        MainDocumentPart main = document.MainDocumentPart
            ?? throw new InvalidDataException("Dokument DOCX nie zawiera części głównej.");
        var segments = new List<SegmentDraft>();
        AddParagraphs(main.Document?.Body?.Descendants<W.Paragraph>() ?? [], segments);
        foreach (HeaderPart header in main.HeaderParts) AddParagraphs(header.RootElement?.Descendants<W.Paragraph>() ?? [], segments);
        foreach (FooterPart footer in main.FooterParts) AddParagraphs(footer.RootElement?.Descendants<W.Paragraph>() ?? [], segments);
        return ExtractionResultBuilder.Build(MimeType, segments, options);
    }

    static void AddParagraphs(IEnumerable<W.Paragraph> paragraphs, ICollection<SegmentDraft> target)
    {
        foreach (W.Paragraph paragraph in paragraphs)
        {
            string text = string.Concat(paragraph.Descendants<W.Text>().Select(value => value.Text));
            if (string.IsNullOrWhiteSpace(text)) continue;
            string? style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            string? heading = style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true ? text : null;
            target.Add(new SegmentDraft(text, Heading: heading));
        }
    }
}

sealed class ExcelExtractor : IContentExtractor
{
    const string MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public bool CanExtract(DetectedFile file) => file.MimeType == MimeType;

    public ExtractionResult Extract(DetectedFile file, TextExtractionOptions options)
    {
        ExtractionLimits.Validate(file, options);
        OpenXmlArchiveSafety.Validate(file.Content, options);
        using var stream = new MemoryStream(file.Content, writable: false);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(stream, false);
        WorkbookPart workbook = document.WorkbookPart
            ?? throw new InvalidDataException("Skoroszyt XLSX nie zawiera części głównej.");
        S.Workbook workbookRoot = workbook.Workbook
            ?? throw new InvalidDataException("Skoroszyt XLSX nie zawiera definicji arkuszy.");
        string[] sharedStrings = workbook.SharedStringTablePart?.SharedStringTable?
            .Elements<S.SharedStringItem>().Select(item => item.InnerText).ToArray() ?? [];
        var segments = new List<SegmentDraft>();
        foreach (S.Sheet sheet in workbookRoot.Sheets?.Elements<S.Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not string relationshipId) continue;
            if (!workbook.TryGetPartById(relationshipId, out OpenXmlPart? part)) continue;
            if (part is not WorksheetPart worksheet) continue;
            var rows = new List<string>();
            foreach (S.Row row in worksheet.Worksheet?.Descendants<S.Row>() ?? [])
            {
                string text = string.Join(" | ", row.Elements<S.Cell>()
                    .Select(cell => FormatCell(cell, sharedStrings))
                    .Where(value => value.Length > 0));
                if (text.Length > 0) rows.Add(text);
            }
            if (rows.Count > 0) segments.Add(new SegmentDraft(string.Join('\n', rows), SheetName: sheet.Name?.Value ?? ""));
        }
        return ExtractionResultBuilder.Build(MimeType, segments, options);
    }

    static string FormatCell(S.Cell cell, string[] sharedStrings)
    {
        S.CellValues? dataType = cell.DataType?.Value;
        string value;
        if (dataType == S.CellValues.SharedString && int.TryParse(cell.InnerText, out int index))
        {
            value = index >= 0 && index < sharedStrings.Length ? sharedStrings[index] : "";
        }
        else if (dataType == S.CellValues.InlineString)
        {
            value = string.Concat(cell.InlineString?.Descendants<S.Text>().Select(text => text.Text) ?? []);
        }
        else
        {
            value = cell.CellValue?.Text ?? cell.InnerText;
        }
        if (string.IsNullOrWhiteSpace(value)) return "";
        string reference = cell.CellReference?.Value ?? "";
        return reference.Length == 0 ? value : $"{reference}: {value}";
    }
}

sealed class PowerPointExtractor : IContentExtractor
{
    const string MimeType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    public bool CanExtract(DetectedFile file) => file.MimeType == MimeType;

    public ExtractionResult Extract(DetectedFile file, TextExtractionOptions options)
    {
        ExtractionLimits.Validate(file, options);
        OpenXmlArchiveSafety.Validate(file.Content, options);
        using var stream = new MemoryStream(file.Content, writable: false);
        using PresentationDocument document = PresentationDocument.Open(stream, false);
        PresentationPart presentation = document.PresentationPart
            ?? throw new InvalidDataException("Prezentacja PPTX nie zawiera części głównej.");
        P.Presentation presentationRoot = presentation.Presentation
            ?? throw new InvalidDataException("Prezentacja PPTX nie zawiera definicji slajdów.");
        var segments = new List<SegmentDraft>();
        int slideNumber = 0;
        foreach (P.SlideId slideId in presentationRoot.SlideIdList?.Elements<P.SlideId>() ?? [])
        {
            slideNumber++;
            if (slideId.RelationshipId?.Value is not string relationshipId) continue;
            if (!presentation.TryGetPartById(relationshipId, out OpenXmlPart? part)) continue;
            if (part is not SlidePart slide) continue;
            var text = (slide.Slide?.Descendants<A.Text>() ?? []).Select(value => value.Text).Where(value => value.Length > 0).ToList();
            if (slide.NotesSlidePart?.NotesSlide is { } notes)
                text.AddRange(notes.Descendants<A.Text>().Select(value => value.Text).Where(value => value.Length > 0));
            if (text.Count > 0) segments.Add(new SegmentDraft(string.Join('\n', text), SlideNumber: slideNumber));
        }
        return ExtractionResultBuilder.Build(MimeType, segments, options);
    }
}

readonly record struct SegmentDraft(
    string RawText,
    int? PageNumber = null,
    int? SlideNumber = null,
    string? SheetName = null,
    string? Heading = null);

static class ExtractionLimits
{
    public static void Validate(DetectedFile file, TextExtractionOptions options)
    {
        options.Validate();
        if (file.Content.Length > options.MaxBytes)
            throw new InvalidDataException($"Plik przekracza limit {options.MaxBytes} bajtów.");
    }
}

static class OpenXmlArchiveSafety
{
    public static void Validate(byte[] content, TextExtractionOptions options)
    {
        // Ograniczony odczyt katalogu centralnego zanim ZipArchive zmaterializuje wszystkie wpisy.
        if (ZipCentralDirectory.TryReadEntryNames(content, options.MaxArchiveEntries, out bool exceedsLimit) is null)
            throw new InvalidDataException("Archiwum OpenXML ma niepoprawny katalog centralny.");
        if (exceedsLimit)
            throw new InvalidDataException($"Archiwum OpenXML przekracza limit {options.MaxArchiveEntries} wpisów.");

        using var stream = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > options.MaxArchiveEntries)
            throw new InvalidDataException($"Archiwum OpenXML przekracza limit {options.MaxArchiveEntries} wpisów.");

        // Zadeklarowane rozmiary (ZipArchiveEntry.Length/CompressedLength) pochodza z katalogu
        // centralnego kontrolowanego przez nadawce, wiec limity egzekwujemy na RZECZYWISTYCH
        // bajtach dekompresji — czytajac kazdy wpis przez bufor, zanim SDK OpenXML sam otworzy
        // czesci pakietu. Bajty sa odrzucane w locie, wiec straznik nie alokuje rozprezonej tresci.
        long expandedTotal = 0;
        byte[] buffer = new byte[81_920];
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
                throw new InvalidDataException("Archiwum OpenXML zawiera niebezpieczną ścieżkę wpisu.");

            long compressed = Math.Max(1, entry.CompressedLength);
            expandedTotal += ReadExpandedWithLimits(entry, buffer, compressed, expandedTotal, options);
        }
    }

    static long ReadExpandedWithLimits(ZipArchiveEntry entry, byte[] buffer, long compressed,
        long expandedTotal, TextExtractionOptions options)
    {
        long budgetRemaining = options.MaxArchiveExpandedBytes - expandedTotal;
        long ratioLimit = compressed * (long)options.MaxArchiveCompressionRatio;
        long actual = 0;
        using Stream source = entry.Open();
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            actual += read;
            if (actual > budgetRemaining)
                throw new InvalidDataException(
                    $"Archiwum OpenXML przekracza limit {options.MaxArchiveExpandedBytes} bajtów po rozwinięciu.");
            if (actual > ratioLimit)
                throw new InvalidDataException(
                    $"Wpis archiwum OpenXML przekracza limit kompresji {options.MaxArchiveCompressionRatio}:1.");
        }
        return actual;
    }
}

static class ExtractionResultBuilder
{
    public static ExtractionResult Build(string mimeType, IEnumerable<SegmentDraft> drafts, TextExtractionOptions options)
    {
        int remainingClean = options.MaxCharacters;
        int remainingRaw = options.MaxCharacters;
        bool truncated = false;
        var segments = new List<ExtractedSegment>();
        foreach (SegmentDraft draft in drafts)
        {
            string clean = TextNormalizer.Normalize(draft.RawText);
            if (clean.Length == 0) continue;
            int separator = segments.Count == 0 ? 0 : 2;
            if (remainingClean <= separator || remainingRaw <= separator) { truncated = true; break; }
            remainingClean -= separator;
            remainingRaw -= separator;
            string raw = TextLimit.Take(draft.RawText, remainingRaw);
            string limitedClean = TextLimit.Take(clean, remainingClean);
            if (raw.Length != draft.RawText.Length || limitedClean.Length != clean.Length) truncated = true;
            clean = limitedClean;
            if (clean.Length == 0) break;
            segments.Add(new ExtractedSegment(segments.Count, raw, clean, draft.PageNumber,
                draft.SlideNumber, draft.SheetName, draft.Heading));
            remainingClean -= clean.Length;
            remainingRaw -= raw.Length;
        }

        string rawText = string.Join("\n\n", segments.Select(segment => segment.RawText));
        string cleanText = string.Join("\n\n", segments.Select(segment => segment.CleanText));
        return new ExtractionResult(mimeType, rawText, cleanText, truncated, segments);
    }
}
