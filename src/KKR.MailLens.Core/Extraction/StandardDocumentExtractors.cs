using DocumentFormat.OpenXml.Packaging;
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
        using var stream = new MemoryStream(file.Content, writable: false);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(stream, false);
        WorkbookPart workbook = document.WorkbookPart
            ?? throw new InvalidDataException("Skoroszyt XLSX nie zawiera części głównej.");
        S.Workbook workbookRoot = workbook.Workbook
            ?? throw new InvalidDataException("Skoroszyt XLSX nie zawiera definicji arkuszy.");
        var segments = new List<SegmentDraft>();
        foreach (S.Sheet sheet in workbookRoot.Sheets?.Elements<S.Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not string relationshipId) continue;
            if (workbook.GetPartById(relationshipId) is not WorksheetPart worksheet) continue;
            var rows = new List<string>();
            foreach (S.Row row in worksheet.Worksheet?.Descendants<S.Row>() ?? [])
            {
                string text = string.Join(" | ", row.Elements<S.Cell>()
                    .Select(cell => FormatCell(cell, workbook))
                    .Where(value => value.Length > 0));
                if (text.Length > 0) rows.Add(text);
            }
            if (rows.Count > 0) segments.Add(new SegmentDraft(string.Join('\n', rows), SheetName: sheet.Name?.Value ?? ""));
        }
        return ExtractionResultBuilder.Build(MimeType, segments, options);
    }

    static string FormatCell(S.Cell cell, WorkbookPart workbook)
    {
        S.CellValues? dataType = cell.DataType?.Value;
        string value;
        if (dataType == S.CellValues.SharedString && int.TryParse(cell.InnerText, out int index))
        {
            value = workbook.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>()
                .ElementAtOrDefault(index)?.InnerText ?? "";
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
            if (presentation.GetPartById(relationshipId) is not SlidePart slide) continue;
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

static class ExtractionResultBuilder
{
    public static ExtractionResult Build(string mimeType, IEnumerable<SegmentDraft> drafts, TextExtractionOptions options)
    {
        int remaining = options.MaxCharacters;
        bool truncated = false;
        var segments = new List<ExtractedSegment>();
        foreach (SegmentDraft draft in drafts)
        {
            string clean = TextNormalizer.Normalize(draft.RawText);
            if (clean.Length == 0) continue;
            if (remaining == 0) { truncated = true; break; }
            if (clean.Length > remaining)
            {
                clean = clean[..remaining];
                truncated = true;
            }
            string raw = draft.RawText.Length > remaining ? draft.RawText[..remaining] : draft.RawText;
            segments.Add(new ExtractedSegment(segments.Count, raw, clean, draft.PageNumber,
                draft.SlideNumber, draft.SheetName, draft.Heading));
            remaining -= clean.Length;
        }

        string rawText = string.Join("\n\n", segments.Select(segment => segment.RawText));
        string cleanText = string.Join("\n\n", segments.Select(segment => segment.CleanText));
        return new ExtractionResult(mimeType, rawText, cleanText, truncated, segments);
    }
}
