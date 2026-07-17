using System.Text;
using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed record ContentSearchHit(
    long SegmentId,
    string Received,
    string Subject,
    string Sender,
    string Filename,
    string DetectedMimeType,
    int? PageNumber,
    int? SlideNumber,
    string? SheetName,
    string Snippet,
    double Rank);

static class ContentSearch
{
    public static IReadOnlyList<ContentSearchHit> Search(SqliteConnection connection, string query,
        int limit = 25, bool raw = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,m.received,m.subject,COALESCE(NULLIF(m.sender_name,''),m.sender_email),
                COALESCE(a.filename,''),COALESCE(d.detected_mime_type,''),s.page_number,s.slide_number,
                s.sheet_name,snippet(content_fts,4,'[',']','…',24),
                bm25(content_fts,10.0,5.0,2.0,8.0,1.0)
            FROM content_fts
            JOIN content_segments s ON s.id=content_fts.rowid
            JOIN content_documents d ON d.id=s.document_id
            JOIN mails m ON m.entry_id=d.mail_entry_id
            LEFT JOIN mail_attachments a ON a.id=d.attachment_id
            WHERE content_fts MATCH $query
            ORDER BY bm25(content_fts,10.0,5.0,2.0,8.0,1.0),m.received DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", raw ? query : Sanitize(query));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var hits = new List<ContentSearchHit>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new ContentSearchHit(reader.GetInt64(0), Text(reader, 1), Text(reader, 2), Text(reader, 3),
                Text(reader, 4), Text(reader, 5), NullableInt(reader, 6), NullableInt(reader, 7),
                reader.IsDBNull(8) ? null : reader.GetString(8), Text(reader, 9), reader.GetDouble(10)));
        }
        return hits;
    }

    public static int Rebuild(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM content_fts;
            INSERT INTO content_fts(rowid,subject,sender,recipients,filename,text)
            SELECT s.id,COALESCE(m.subject,''),COALESCE(m.sender_name,'') || ' ' || COALESCE(m.sender_email,''),
                COALESCE(m.to_recips,'') || ' ' || COALESCE(m.cc_recips,''),COALESCE(a.filename,''),s.clean_text
            FROM content_segments s
            JOIN content_documents d ON d.id=s.document_id
            JOIN mails m ON m.entry_id=d.mail_entry_id
            LEFT JOIN mail_attachments a ON a.id=d.attachment_id;
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT count(*) FROM content_fts;";
        int count = Convert.ToInt32(command.ExecuteScalar());
        transaction.Commit();
        return count;
    }

    public static int Run(string keyHex, string[] args)
    {
        string query = FirstPositional(args);
        if (query.Length == 0)
        {
            Console.Error.WriteLine("Podaj szukaną frazę.");
            return 1;
        }
        using var connection = Db.Open(keyHex, create: false);
        Db.EnsureSchema(connection);
        try
        {
            IReadOnlyList<ContentSearchHit> hits = Search(connection, query,
                Args.Int(args, "--limit", 25), Args.Flag(args, "--raw"));
            foreach (ContentSearchHit hit in hits)
            {
                Console.WriteLine($"{hit.Received}  {hit.Sender}");
                Console.WriteLine($"    {hit.Subject}");
                Console.WriteLine($"    {Location(hit)}{hit.Filename}");
                if (hit.Snippet.Length > 0) Console.WriteLine($"    | {hit.Snippet}");
            }
            Console.WriteLine(hits.Count == 0 ? "(brak trafień)" : $"-- {hits.Count} trafień");
            return 0;
        }
        catch (SqliteException ex)
        {
            Console.Error.WriteLine($"Błąd zapytania: {ex.Message}");
            return 1;
        }
    }

    static void IndexDocument(SqliteConnection connection, SqliteTransaction transaction, long documentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO content_fts(rowid,subject,sender,recipients,filename,text)
            SELECT s.id,COALESCE(m.subject,''),COALESCE(m.sender_name,'') || ' ' || COALESCE(m.sender_email,''),
                COALESCE(m.to_recips,'') || ' ' || COALESCE(m.cc_recips,''),COALESCE(a.filename,''),s.clean_text
            FROM content_segments s
            JOIN content_documents d ON d.id=s.document_id
            JOIN mails m ON m.entry_id=d.mail_entry_id
            LEFT JOIN mail_attachments a ON a.id=d.attachment_id
            WHERE d.id=$document;
            """;
        command.Parameters.AddWithValue("$document", documentId);
        command.ExecuteNonQuery();
    }

    static string Sanitize(string query)
    {
        var result = new StringBuilder();
        foreach (string token in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Length > 0) result.Append(' ');
            result.Append('"').Append(token.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        return result.ToString();
    }

    static string FirstPositional(string[] args)
    {
        for (int index = 1; index < args.Length; index++)
        {
            if (args[index] == "--raw") continue;
            if (args[index] == "--limit") { index++; continue; }
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) return args[index];
        }
        return "";
    }

    static string Location(ContentSearchHit hit)
    {
        if (hit.PageNumber is not null) return $"strona {hit.PageNumber}: ";
        if (hit.SlideNumber is not null) return $"slajd {hit.SlideNumber}: ";
        if (!string.IsNullOrWhiteSpace(hit.SheetName)) return $"arkusz {hit.SheetName}: ";
        return "";
    }

    static string Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
    static int? NullableInt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    internal static void IndexSavedDocument(SqliteConnection connection, SqliteTransaction transaction, long documentId) =>
        IndexDocument(connection, transaction, documentId);
}
