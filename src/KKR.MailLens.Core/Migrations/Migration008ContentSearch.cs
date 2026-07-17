using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed class Migration008ContentSearch : IDatabaseMigration
{
    public int Version => 8;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        MigrationSql.Execute(connection, transaction, """
            CREATE VIRTUAL TABLE content_fts USING fts5(
                subject,
                sender,
                recipients,
                filename,
                text,
                tokenize='unicode61 remove_diacritics 2'
            );
            CREATE TRIGGER content_segments_after_delete AFTER DELETE ON content_segments BEGIN
                DELETE FROM content_fts WHERE rowid=old.id;
            END;
            INSERT INTO content_fts(rowid,subject,sender,recipients,filename,text)
            SELECT s.id,COALESCE(m.subject,''),COALESCE(m.sender_name,'') || ' ' || COALESCE(m.sender_email,''),
                COALESCE(m.to_recips,'') || ' ' || COALESCE(m.cc_recips,''),COALESCE(a.filename,''),s.clean_text
            FROM content_segments s
            JOIN content_documents d ON d.id=s.document_id
            JOIN mails m ON m.entry_id=d.mail_entry_id
            LEFT JOIN mail_attachments a ON a.id=d.attachment_id;
            """);
    }
}
