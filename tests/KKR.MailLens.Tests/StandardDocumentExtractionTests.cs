using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class StandardDocumentExtractionTests
{
    [TestMethod]
    public void Extract_Pdf_CreatesPageSegment()
    {
        ExtractionResult result = new ContentExtractionDispatcher().Extract(
            "record.bin", "application/octet-stream", CreatePdf("Neutral text"));

        Assert.AreEqual("application/pdf", result.DetectedMimeType);
        StringAssert.Contains(result.CleanText, "Neutral text");
        Assert.HasCount(1, result.Segments);
        Assert.AreEqual(1, result.Segments[0].PageNumber);
    }

    [TestMethod]
    public void Extract_Docx_CreatesParagraphSegments()
    {
        ExtractionResult result = new ContentExtractionDispatcher().Extract(
            "record.bin", "application/octet-stream", CreateDocx());

        StringAssert.Contains(result.CleanText, "Test Record");
        StringAssert.Contains(result.CleanText, "Neutralny tekst wiadomości");
        Assert.HasCount(2, result.Segments);
        Assert.AreEqual("Test Record", result.Segments[0].Heading);
    }

    [TestMethod]
    public void Extract_Xlsx_CreatesSheetSegmentWithCellReferences()
    {
        ExtractionResult result = new ContentExtractionDispatcher().Extract(
            "record.bin", "application/octet-stream", CreateXlsx());

        Assert.HasCount(1, result.Segments);
        Assert.AreEqual("Test Sheet", result.Segments[0].SheetName);
        StringAssert.Contains(result.CleanText, "A1: Test Record");
        StringAssert.Contains(result.CleanText, "B1: Neutralny tekst");
    }

    [TestMethod]
    public void Extract_Xlsx_SkipsSheetWithDanglingRelationship()
    {
        ExtractionResult result = new ContentExtractionDispatcher().Extract(
            "record.bin", "application/octet-stream", CreateXlsxWithDanglingSheetRelationship());

        Assert.HasCount(1, result.Segments);
        Assert.AreEqual("Test Sheet", result.Segments[0].SheetName);
        StringAssert.Contains(result.CleanText, "A1: Neutralny tekst");
    }

    [TestMethod]
    public void Extract_Xlsx_ResolvesSharedStringsAndSkipsOutOfRangeIndexes()
    {
        ExtractionResult result = new ContentExtractionDispatcher().Extract(
            "record.bin", "application/octet-stream", CreateXlsxWithSharedStrings());

        StringAssert.Contains(result.CleanText, "A1: Test Record");
        StringAssert.Contains(result.CleanText, "B1: Neutralny tekst");
        Assert.IsFalse(result.CleanText.Contains("C1", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Extract_Pptx_SkipsSlideWithDanglingRelationship()
    {
        ExtractionResult result = new ContentExtractionDispatcher().Extract(
            "record.bin", "application/octet-stream", CreatePptxWithDanglingSlideRelationship());

        Assert.HasCount(1, result.Segments);
        Assert.AreEqual(2, result.Segments[0].SlideNumber);
        StringAssert.Contains(result.CleanText, "Neutralny tekst prezentacji");
    }

    [TestMethod]
    public void Extract_Pptx_CreatesSlideSegment()
    {
        ExtractionResult result = new ContentExtractionDispatcher().Extract(
            "record.bin", "application/octet-stream", CreatePptx());

        Assert.HasCount(1, result.Segments);
        Assert.AreEqual(1, result.Segments[0].SlideNumber);
        StringAssert.Contains(result.CleanText, "Neutralny tekst prezentacji");
    }

    static byte[] CreateDocx()
    {
        using var stream = new MemoryStream();
        using (WordprocessingDocument document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            MainDocumentPart main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(
                    new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
                    new W.Run(new W.Text("Test Record"))),
                new W.Paragraph(new W.Run(new W.Text("Neutralny tekst wiadomości")))));
        }
        return stream.ToArray();
    }

    static byte[] CreateXlsx()
    {
        using var stream = new MemoryStream();
        using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            WorkbookPart workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            WorksheetPart worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(
                    InlineCell("A1", "Test Record"),
                    InlineCell("B1", "Neutralny tekst"))));
            var sheets = workbook.Workbook.AppendChild(new S.Sheets());
            sheets.Append(new S.Sheet
            {
                Id = workbook.GetIdOfPart(worksheet),
                SheetId = 1,
                Name = "Test Sheet",
            });
        }
        return stream.ToArray();
    }

    static byte[] CreateXlsxWithDanglingSheetRelationship()
    {
        using var stream = new MemoryStream();
        using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            WorkbookPart workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            WorksheetPart worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(InlineCell("A1", "Neutralny tekst"))));
            var sheets = workbook.Workbook.AppendChild(new S.Sheets());
            sheets.Append(new S.Sheet { Id = "rIdMissing", SheetId = 1, Name = "Broken Sheet" });
            sheets.Append(new S.Sheet
            {
                Id = workbook.GetIdOfPart(worksheet),
                SheetId = 2,
                Name = "Test Sheet",
            });
        }
        return stream.ToArray();
    }

    static byte[] CreateXlsxWithSharedStrings()
    {
        using var stream = new MemoryStream();
        using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            WorkbookPart workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            SharedStringTablePart sharedStrings = workbook.AddNewPart<SharedStringTablePart>();
            sharedStrings.SharedStringTable = new S.SharedStringTable(
                new S.SharedStringItem(new S.Text("Test Record")),
                new S.SharedStringItem(new S.Text("Neutralny tekst")));
            WorksheetPart worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(SharedCell("A1", 0), SharedCell("B1", 1), SharedCell("C1", 99))));
            var sheets = workbook.Workbook.AppendChild(new S.Sheets());
            sheets.Append(new S.Sheet
            {
                Id = workbook.GetIdOfPart(worksheet),
                SheetId = 1,
                Name = "Test Sheet",
            });
        }
        return stream.ToArray();
    }

    static S.Cell InlineCell(string reference, string value) => new()
    {
        CellReference = reference,
        DataType = S.CellValues.InlineString,
        InlineString = new S.InlineString(new S.Text(value)),
    };

    static S.Cell SharedCell(string reference, int index) => new()
    {
        CellReference = reference,
        DataType = S.CellValues.SharedString,
        CellValue = new S.CellValue(index.ToString()),
    };

    static byte[] CreatePptx()
    {
        using var stream = new MemoryStream();
        using (PresentationDocument document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            PresentationPart presentation = document.AddPresentationPart();
            presentation.Presentation = new P.Presentation();
            SlidePart slide = presentation.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.Shape(new P.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text("Neutralny tekst prezentacji"))))))));
            presentation.Presentation.SlideIdList = new P.SlideIdList(
                new P.SlideId { Id = 256, RelationshipId = presentation.GetIdOfPart(slide) });
        }
        return stream.ToArray();
    }

    static byte[] CreatePptxWithDanglingSlideRelationship()
    {
        using var stream = new MemoryStream();
        using (PresentationDocument document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            PresentationPart presentation = document.AddPresentationPart();
            presentation.Presentation = new P.Presentation();
            SlidePart slide = presentation.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.Shape(new P.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text("Neutralny tekst prezentacji"))))))));
            presentation.Presentation.SlideIdList = new P.SlideIdList(
                new P.SlideId { Id = 256, RelationshipId = "rIdMissing" },
                new P.SlideId { Id = 257, RelationshipId = presentation.GetIdOfPart(slide) });
        }
        return stream.ToArray();
    }

    static byte[] CreatePdf(string text)
    {
        string escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
        string content = $"BT /F1 12 Tf 72 720 Td ({escaped}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
        ];

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
