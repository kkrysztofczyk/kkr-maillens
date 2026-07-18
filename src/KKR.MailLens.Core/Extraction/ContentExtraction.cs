using System.Text;

namespace KKR.MailLens;

sealed record DetectedFile(string Filename, string MimeType, string Extension, byte[] Content);

sealed record ExtractedSegment(
    int Ordinal,
    string RawText,
    string CleanText,
    int? PageNumber = null,
    int? SlideNumber = null,
    string? SheetName = null,
    string? Heading = null,
    double? Confidence = null,
    string? MetadataJson = null,
    long? StartMs = null,
    long? EndMs = null);

sealed record ExtractionResult(
    string DetectedMimeType,
    string RawText,
    string CleanText,
    bool WasTruncated,
    IReadOnlyList<ExtractedSegment> Segments)
{
    public IReadOnlyList<int> OcrPageNumbers { get; init; } = [];
    public string? DetectedLanguage { get; init; }
}

sealed record TextExtractionOptions(
    int MaxBytes = 25 * 1024 * 1024,
    int MaxCharacters = 2_000_000,
    int MaxArchiveEntries = 4_096,
    long MaxArchiveExpandedBytes = 100L * 1024 * 1024,
    int MaxArchiveCompressionRatio = 1_000)
{
    public void Validate()
    {
        if (MaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxBytes));
        if (MaxCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(MaxCharacters));
        if (MaxArchiveEntries <= 0) throw new ArgumentOutOfRangeException(nameof(MaxArchiveEntries));
        if (MaxArchiveExpandedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxArchiveExpandedBytes));
        if (MaxArchiveCompressionRatio <= 0) throw new ArgumentOutOfRangeException(nameof(MaxArchiveCompressionRatio));
    }
}

interface IContentExtractor
{
    bool CanExtract(DetectedFile file);
    ExtractionResult Extract(DetectedFile file, TextExtractionOptions options);
}

static class TextLimit
{
    public static string Take(string value, int maximumCharacters)
    {
        if (maximumCharacters < 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        if (value.Length <= maximumCharacters) return value;
        int length = maximumCharacters;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])
            && length < value.Length && char.IsLowSurrogate(value[length]))
            length--;
        return value[..length];
    }
}

sealed class ContentExtractionDispatcher
{
    readonly IReadOnlyList<IContentExtractor> extractors;
    readonly TextExtractionOptions options;

    public ContentExtractionDispatcher(
        IEnumerable<IContentExtractor>? extractors = null,
        TextExtractionOptions? options = null)
    {
        this.extractors = (extractors ??
        [
            new PlainTextExtractor(), new HtmlContentExtractor(), new PdfTextExtractor(),
            new WordExtractor(), new ExcelExtractor(), new PowerPointExtractor(),
        ]).ToArray();
        this.options = options ?? new TextExtractionOptions();
        this.options.Validate();
    }

    public ExtractionResult Extract(string filename, string? declaredMimeType, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        DetectedFile file = FileTypeDetector.Detect(filename, declaredMimeType, content);
        IContentExtractor extractor = extractors.FirstOrDefault(candidate => candidate.CanExtract(file))
            ?? throw new NotSupportedException($"Nieobsługiwany typ pliku: {file.MimeType}.");
        return extractor.Extract(file, options);
    }
}
