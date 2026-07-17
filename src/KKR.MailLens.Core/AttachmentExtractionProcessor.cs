using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

static class AttachmentExtractionProcessor
{
    public static void Process(SqliteConnection connection, EncryptedBlobStore store,
        long attachmentId, long documentId)
    {
        MailAttachmentRepository.Item item = MailAttachmentRepository.Get(connection, attachmentId);
        StoredBlob blob = EncryptedBlobStore.Get(connection,
            item.BlobId ?? throw new InvalidOperationException("Załącznik nie ma pobranego blobu."));
        byte[] plaintext = store.Read(blob);
        try
        {
            ExtractionResult result = new ContentExtractionDispatcher().Extract(item.Filename, item.MimeType, plaintext);
            ContentDocumentRepository.SaveExtraction(connection, documentId, result, ExtractorName(result), "1");
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
