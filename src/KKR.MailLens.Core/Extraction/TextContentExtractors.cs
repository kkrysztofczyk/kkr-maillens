using System.Text;

namespace KKR.MailLens;

sealed class PlainTextExtractor : IContentExtractor
{
    static readonly HashSet<string> MimeTypes = new(StringComparer.Ordinal)
    {
        "text/plain", "text/csv", "application/json", "application/xml", "text/xml",
    };

    public bool CanExtract(DetectedFile file) => MimeTypes.Contains(file.MimeType);

    public ExtractionResult Extract(DetectedFile file, TextExtractionOptions options)
    {
        DecodedText decoded = TextDecoder.Decode(file.Content, options);
        string clean = TextNormalizer.Normalize(decoded.Text);
        bool truncated = decoded.WasTruncated || clean.Length > options.MaxCharacters;
        clean = TextLimit.Take(clean, options.MaxCharacters);
        return new ExtractionResult(file.MimeType, decoded.Text, clean, truncated,
            [new ExtractedSegment(0, decoded.Text, clean)]);
    }
}

sealed class HtmlContentExtractor : IContentExtractor
{
    public bool CanExtract(DetectedFile file) => file.MimeType == "text/html";

    public ExtractionResult Extract(DetectedFile file, TextExtractionOptions options)
    {
        DecodedText decoded = TextDecoder.Decode(file.Content, options);
        string text = GmailMessageMapper.HtmlToText(decoded.Text);
        bool truncated = decoded.WasTruncated;
        if (text.Length > options.MaxCharacters)
        {
            text = TextLimit.Take(text, options.MaxCharacters);
            truncated = true;
        }
        string clean = TextNormalizer.Normalize(text);
        if (clean.Length > options.MaxCharacters)
        {
            clean = TextLimit.Take(clean, options.MaxCharacters);
            truncated = true;
        }
        return new ExtractionResult("text/html", text, clean, truncated,
            [new ExtractedSegment(0, text, clean)]);
    }
}

readonly record struct DecodedText(string Text, bool WasTruncated);

static class TextDecoder
{
    static TextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static DecodedText Decode(byte[] content, TextExtractionOptions options)
    {
        options.Validate();
        if (content.Length > options.MaxBytes)
            throw new InvalidDataException($"Plik przekracza limit {options.MaxBytes} bajtów.");

        (Encoding encoding, int preambleLength) = DetectEncoding(content);
        string text = encoding.GetString(content, preambleLength, content.Length - preambleLength);
        bool truncated = text.Length > options.MaxCharacters;
        if (truncated) text = TextLimit.Take(text, options.MaxCharacters);
        return new DecodedText(text, truncated);
    }

    static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] content)
    {
        if (content.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())) return (new UTF8Encoding(false), 3);
        if (content.AsSpan().StartsWith(Encoding.Unicode.GetPreamble())) return (Encoding.Unicode, 2);
        if (content.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble())) return (Encoding.BigEndianUnicode, 2);
        if (DetectBomlessUtf16(content) is { } utf16) return (utf16, 0);

        try
        {
            _ = new UTF8Encoding(false, true).GetString(content);
            return (new UTF8Encoding(false), 0);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(1250), 0);
        }
    }

    static Encoding? DetectBomlessUtf16(byte[] content)
    {
        // Bez BOM mostly-ASCII UTF-16 przechodzi ścisłą walidację UTF-8 (bajty zerowe są
        // poprawnym UTF-8), więc rozkład zer na parzystych/nieparzystych pozycjach musi
        // rozstrzygnąć przed próbą UTF-8.
        if (content.Length < 4 || content.Length % 2 != 0) return null;
        int zeroEven = 0, zeroOdd = 0;
        for (int index = 0; index + 1 < content.Length; index += 2)
        {
            if (content[index] == 0) zeroEven++;
            if (content[index + 1] == 0) zeroOdd++;
        }
        int pairs = content.Length / 2;
        if (zeroOdd * 2 >= pairs && zeroEven * 10 <= pairs) return Encoding.Unicode;
        if (zeroEven * 2 >= pairs && zeroOdd * 10 <= pairs) return Encoding.BigEndianUnicode;
        return null;
    }
}

static class TextNormalizer
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string normalized = value.Normalize(NormalizationForm.FormKC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00A0', ' ')
            .Replace("\0", "", StringComparison.Ordinal);

        var lines = normalized.Split('\n').Select(line => line.Trim()).ToList();
        var result = new StringBuilder(normalized.Length);
        int emptyLines = 0;
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                emptyLines++;
                if (emptyLines > 1) continue;
            }
            else
            {
                emptyLines = 0;
            }

            if (result.Length > 0) result.Append('\n');
            result.Append(line);
        }
        return result.ToString().Trim();
    }
}
