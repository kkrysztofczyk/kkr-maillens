using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

/// <summary>Wymusza spójność mail_attachments.blob_id ↔ stored_blobs.id przez triggery.
/// Pełny klucz obcy wymagałby przebudowy tabeli poza transakcją migracji (PRAGMA foreign_keys
/// jest ignorowane w transakcji, a DROP TABLE kaskadowo czyściłby processing_jobs).</summary>
sealed class Migration012MailAttachmentBlobIntegrity : IDatabaseMigration
{
    public int Version => 12;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            UPDATE mail_attachments
            SET blob_id=NULL,download_status='metadata-only',processing_status='pending',
                error_code=NULL,error_message=NULL,
                updated_at=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE blob_id IS NOT NULL
              AND NOT EXISTS(SELECT 1 FROM stored_blobs WHERE id=mail_attachments.blob_id);

            CREATE TRIGGER trg_mail_attachments_blob_fk_insert
            BEFORE INSERT ON mail_attachments
            WHEN NEW.blob_id IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM stored_blobs WHERE id=NEW.blob_id)
            BEGIN
                SELECT RAISE(ABORT,'mail_attachments.blob_id wskazuje nieistniejący blob');
            END;

            CREATE TRIGGER trg_mail_attachments_blob_fk_update
            BEFORE UPDATE OF blob_id ON mail_attachments
            WHEN NEW.blob_id IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM stored_blobs WHERE id=NEW.blob_id)
            BEGIN
                SELECT RAISE(ABORT,'mail_attachments.blob_id wskazuje nieistniejący blob');
            END;

            CREATE TRIGGER trg_stored_blobs_blob_fk_delete
            BEFORE DELETE ON stored_blobs
            WHEN EXISTS(SELECT 1 FROM mail_attachments WHERE blob_id=OLD.id)
            BEGIN
                SELECT RAISE(ABORT,'stored_blobs.id jest nadal wskazywany przez mail_attachments');
            END;
            """);
    }
}
