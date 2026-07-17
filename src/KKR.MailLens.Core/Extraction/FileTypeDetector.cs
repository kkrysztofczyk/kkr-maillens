using System.Text;
using System.IO.Compression;

namespace KKR.MailLens;

static class FileTypeDetector
{
    static readonly HashSet<string> HtmlMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html", "application/xhtml+xml",
    };

    public static DetectedFile Detect(string filename, string? declaredMimeType, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string safeFilename = Path.GetFileName(filename ?? "");
        string extension = Path.GetExtension(safeFilename).ToLowerInvariant();
        string mimeType = NormalizeMimeType(declaredMimeType);

        string? signatureMimeType = DetectSignature(content);
        if (signatureMimeType is not null)
            mimeType = signatureMimeType;
        else if (LooksLikeHtml(content) || HtmlMimeTypes.Contains(mimeType) || extension is ".html" or ".htm" or ".xhtml")
            mimeType = "text/html";
        else if (extension == ".docx") mimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        else if (extension == ".xlsx") mimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        else if (extension == ".pptx") mimeType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
        else if (extension == ".pdf") mimeType = "application/pdf";
        else if (mimeType == "text/plain" || extension is ".txt" or ".text" or ".log") mimeType = "text/plain";
        else if (mimeType is "application/json" or "application/xml" || extension is ".json" or ".xml" or ".csv")
            mimeType = extension switch { ".json" => "application/json", ".xml" => "application/xml", ".csv" => "text/csv", _ => mimeType };

        return new DetectedFile(safeFilename, mimeType, extension, content);
    }

    static string NormalizeMimeType(string? value)
    {
        string mimeType = (value ?? "application/octet-stream").Split(';', 2)[0].Trim().ToLowerInvariant();
        return mimeType.Length == 0 ? "application/octet-stream" : mimeType;
    }

    static bool LooksLikeHtml(byte[] content)
    {
        if (content.Length == 0) return false;
        int length = Math.Min(content.Length, 512);
        string prefix = Encoding.UTF8.GetString(content, 0, length).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return prefix.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    static string? DetectSignature(byte[] content)
    {
        if (content.AsSpan().StartsWith("%PDF-"u8)) return "application/pdf";
        if (!content.AsSpan().StartsWith("PK\x03\x04"u8)) return null;

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Any(entry => entry.FullName.StartsWith("word/", StringComparison.OrdinalIgnoreCase)))
                return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            if (archive.Entries.Any(entry => entry.FullName.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)))
                return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            if (archive.Entries.Any(entry => entry.FullName.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase)))
                return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
        }
        catch (InvalidDataException)
        {
            return null;
        }
        return null;
    }
}
