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
        else if (extension is ".wav" or ".mp3" or ".m4a" or ".aac" or ".flac" or ".ogg" or ".opus")
            mimeType = extension switch
            {
                ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".flac" => "audio/flac",
                ".ogg" or ".opus" => "audio/ogg", _ => "audio/mp4",
            };
        else if (extension is ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi")
            mimeType = extension switch
            {
                ".webm" => "video/webm", ".mkv" => "video/x-matroska", ".avi" => "video/x-msvideo",
                _ => "video/mp4",
            };
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
        if (content.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (content.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        if (content.AsSpan().StartsWith("II*\0"u8) || content.AsSpan().StartsWith("MM\0*"u8)) return "image/tiff";
        if (content.AsSpan().StartsWith("BM"u8)) return "image/bmp";
        if (content.Length >= 12 && content.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && content.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return "audio/wav";
        if (content.AsSpan().StartsWith("fLaC"u8)) return "audio/flac";
        if (content.AsSpan().StartsWith("OggS"u8)) return "audio/ogg";
        if (content.AsSpan().StartsWith("ID3"u8)
            || LooksLikeMpegAudioFrame(content)) return "audio/mpeg";
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

    static bool LooksLikeMpegAudioFrame(byte[] content)
    {
        if (content.Length < 4 || content[0] != 0xFF || (content[1] & 0xE0) != 0xE0) return false;
        if ((content[1] & 0x18) == 0x08 || (content[1] & 0x06) == 0) return false;
        int bitrateIndex = content[2] >> 4;
        int sampleRateIndex = (content[2] >> 2) & 0x03;
        return bitrateIndex is > 0 and < 15 && sampleRateIndex < 3;
    }
}
