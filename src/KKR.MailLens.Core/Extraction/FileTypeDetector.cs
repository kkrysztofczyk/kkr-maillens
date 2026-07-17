using System.Text;

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

        if (LooksLikeHtml(content) || HtmlMimeTypes.Contains(mimeType) || extension is ".html" or ".htm" or ".xhtml")
            mimeType = "text/html";
        else if (mimeType == "text/plain" || extension is ".txt" or ".text" or ".log")
            mimeType = "text/plain";

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
}
