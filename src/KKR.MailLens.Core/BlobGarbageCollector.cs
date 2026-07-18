using Microsoft.Data.Sqlite;

namespace KKR.MailLens;

sealed record BlobGarbageCollectionResult(int Orphaned, int Deleted, long ReclaimedBytes, int Failed);

static class BlobGarbageCollector
{
    sealed record Candidate(long Id, string Sha256, string EncryptedPath, long OriginalSize);

    public static BlobGarbageCollectionResult Preview(SqliteConnection connection)
    {
        IReadOnlyList<Candidate> candidates = FindCandidates(connection, transaction: null);
        return new BlobGarbageCollectionResult(candidates.Count, 0,
            candidates.Sum(candidate => candidate.OriginalSize), 0);
    }

    public static BlobGarbageCollectionResult Collect(SqliteConnection connection, string root,
        Action<string>? warning = null)
    {
        IReadOnlyList<Candidate> candidates = FindCandidates(connection, transaction: null);
        int deleted = 0;
        int failed = 0;
        long reclaimedBytes = 0;

        foreach (Candidate candidate in candidates)
        {
            try
            {
                using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
                if (!IsCollectable(connection, transaction, candidate.Id))
                {
                    transaction.Commit();
                    continue;
                }

                string path = EncryptedBlobStore.ResolvePath(root, candidate.EncryptedPath);
                if (File.Exists(path)) File.Delete(path);

                DeleteObsoletePipelineState(connection, transaction, candidate.Id);
                int rows = Execute(connection, transaction, """
                    DELETE FROM stored_blobs
                    WHERE id=$id
                      AND NOT EXISTS(
                          SELECT 1 FROM mail_attachments
                          WHERE blob_id=$id AND is_deleted=0
                      );
                    """, ("$id", candidate.Id));
                if (rows != 1)
                    throw new InvalidOperationException("Blob odzyskał aktywną referencję podczas sprzątania.");

                transaction.Commit();
                deleted++;
                reclaimedBytes += candidate.OriginalSize;
                TryDeleteEmptyParents(root, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or SqliteException)
            {
                failed++;
                warning?.Invoke($"Nie usunięto osieroconego blobu {candidate.Sha256}: {ex.Message}");
            }
        }

        return new BlobGarbageCollectionResult(candidates.Count, deleted, reclaimedBytes, failed);
    }

    static IReadOnlyList<Candidate> FindCandidates(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT b.id,b.sha256,b.encrypted_path,b.original_size
            FROM stored_blobs b
            WHERE NOT EXISTS(
                    SELECT 1 FROM mail_attachments a
                    WHERE a.blob_id=b.id AND a.is_deleted=0
                )
              AND NOT EXISTS(
                    SELECT 1
                    FROM processing_jobs j
                    LEFT JOIN content_documents d ON d.id=j.document_id
                    JOIN mail_attachments a ON a.id=COALESCE(j.attachment_id,d.attachment_id)
                    WHERE a.blob_id=b.id AND j.status='running'
                )
            ORDER BY b.id;
            """);
        var candidates = new List<Candidate>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            candidates.Add(new Candidate(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        return candidates;
    }

    static bool IsCollectable(SqliteConnection connection, SqliteTransaction transaction, long blobId)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT NOT EXISTS(
                    SELECT 1 FROM mail_attachments
                    WHERE blob_id=$id AND is_deleted=0
                )
                AND NOT EXISTS(
                    SELECT 1
                    FROM processing_jobs j
                    LEFT JOIN content_documents d ON d.id=j.document_id
                    JOIN mail_attachments a ON a.id=COALESCE(j.attachment_id,d.attachment_id)
                    WHERE a.blob_id=$id AND j.status='running'
                );
            """, ("$id", blobId));
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    static void DeleteObsoletePipelineState(SqliteConnection connection, SqliteTransaction transaction, long blobId)
    {
        Execute(connection, transaction, """
            DELETE FROM processing_jobs
            WHERE attachment_id IN(
                    SELECT id FROM mail_attachments WHERE blob_id=$blob AND is_deleted=1
                )
               OR document_id IN(
                    SELECT d.id
                    FROM content_documents d
                    JOIN mail_attachments a ON a.id=d.attachment_id
                    WHERE a.blob_id=$blob AND a.is_deleted=1
                );

            DELETE FROM content_documents
            WHERE attachment_id IN(
                SELECT id FROM mail_attachments WHERE blob_id=$blob AND is_deleted=1
            );

            UPDATE mail_attachments
            SET blob_id=NULL,download_status='metadata-only',processing_status='pending',
                error_code=NULL,error_message=NULL,updated_at=$now
            WHERE blob_id=$blob AND is_deleted=1;
            """, ("$blob", blobId), ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }

    static void TryDeleteEmptyParents(string root, string path)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? directory = Path.GetDirectoryName(path);
        for (int depth = 0; depth < 2 && directory is not null
            && !directory.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase); depth++)
        {
            try { Directory.Delete(directory, recursive: false); }
            catch (IOException) { break; }
            catch (UnauthorizedAccessException) { break; }
            directory = Path.GetDirectoryName(directory);
        }
    }

    static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }
}
