using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed record AttachmentExtractionOutcome(string Status, string DetectedMimeType);

static class AttachmentExtractionProcessor
{
    public static AttachmentExtractionOutcome Process(SqliteConnection connection, EncryptedBlobStore store,
        long attachmentId, long documentId)
    {
        MailAttachmentRepository.Item item = MailAttachmentRepository.Get(connection, attachmentId);
        StoredBlob blob = EncryptedBlobStore.Get(connection,
            item.BlobId ?? throw new InvalidOperationException("Załącznik nie ma pobranego blobu."));
        byte[] plaintext = store.Read(blob);
        try
        {
            DetectedFile detected = FileTypeDetector.Detect(item.Filename, item.MimeType, plaintext);
            ExtractionResult result;
            try
            {
                result = detected.MimeType.StartsWith("image/", StringComparison.Ordinal)
                    ? new ExtractionResult(detected.MimeType, "", "", false, [])
                    : new ContentExtractionDispatcher().Extract(item.Filename, item.MimeType, plaintext);
            }
            catch (NotSupportedException ex)
            {
                ContentDocumentRepository.MarkSkipped(connection, documentId, "unsupported-type", ex.Message);
                return new AttachmentExtractionOutcome("skipped", detected.MimeType);
            }
            string status = ContentDocumentRepository.SaveExtraction(
                connection, documentId, result, ExtractorName(result), "1");
            return new AttachmentExtractionOutcome(status, result.DetectedMimeType);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    static string ExtractorName(ExtractionResult result) => result.DetectedMimeType switch
    {
        "application/pdf" => "pdfpig",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "openxml-word",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "openxml-excel",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "openxml-powerpoint",
        "text/html" => "html",
        _ => "plain-text",
    };
}
