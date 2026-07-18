using System.Security.Cryptography;

namespace KKR.MailLens;

sealed record DownloadedAttachment(byte[] Bytes, string Sha256, string DetectedMimeType);

static class GmailAttachmentDownloader
{
    public const long DefaultMaximumBytes = 50L * 1024 * 1024;
    const long MinimumSizeToleranceBytes = 4 * 1024;

    public static async Task<DownloadedAttachment> DownloadAsync(IGmailApiClient api, string messageId,
        GmailAttachmentRecord attachment, long maximumBytes = DefaultMaximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (attachment.SizeBytes > maximumBytes)
            throw new InvalidDataException("Załącznik przekracza dozwolony limit rozmiaru.");

        byte[] bytes = !string.IsNullOrWhiteSpace(attachment.InlineBase64Data)
            ? DecodeBase64Url(attachment.InlineBase64Data)
            : !string.IsNullOrWhiteSpace(attachment.GmailAttachmentId)
                ? await api.GetAttachmentBytesAsync(messageId, attachment.GmailAttachmentId, cancellationToken).ConfigureAwait(false)
                : throw new InvalidDataException("Załącznik Gmail nie zawiera danych ani identyfikatora pobrania.");

        try
        {
            if (bytes.Length == 0) throw new InvalidDataException("Załącznik Gmail jest pusty.");
            if (bytes.LongLength > maximumBytes) throw new InvalidDataException("Pobrany załącznik przekracza dozwolony limit rozmiaru.");
            if (attachment.SizeBytes > 0)
            {
                long tolerance = Math.Max(MinimumSizeToleranceBytes, attachment.SizeBytes / 100);
                long difference = Math.Abs(bytes.LongLength - attachment.SizeBytes);
                if (difference > tolerance)
                    throw new InvalidDataException("Rozmiar pobranego załącznika istotnie różni się od metadanych Gmail.");
            }

            string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string detectedMimeType = FileTypeDetector.Detect(attachment.Filename, attachment.MimeType, bytes).MimeType;
            return new DownloadedAttachment(bytes, sha256, detectedMimeType);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    public static byte[] DecodeBase64Url(string? value)
        => Base64Url.Decode(value, "Nieprawidłowe dane Base64URL załącznika Gmail.");

}
