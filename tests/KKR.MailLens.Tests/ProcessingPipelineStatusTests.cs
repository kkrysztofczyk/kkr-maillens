using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class ProcessingPipelineStatusTests
{
    [TestMethod]
    public void Read_SeparatesCurrentRunAndMailboxSource()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord firstSource = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Gmail,
            "first@example.invalid");
        MailboxSourceRecord secondSource = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Imap,
            "imap-first");
        long firstAttachment = ProcessingTestData.AddAttachment(
            db.Connection,
            firstSource.Id,
            "first");
        long secondAttachment = ProcessingTestData.AddAttachment(
            db.Connection,
            secondSource.Id,
            "second");

        ProcessingJobRepository.Enqueue(db.Connection, "extract", firstAttachment);
        MailboxImportRunRecord run =
            MailboxImportRunRepository.Create(db.Connection, [firstSource.Id]);
        Assert.IsTrue(run.ProcessingJobBaselineId > 0);
        ProcessingJobRepository.Enqueue(db.Connection, "ocr", firstAttachment);
        ProcessingJobRepository.Enqueue(db.Connection, "transcribe", secondAttachment);

        ProcessingPipelineSnapshot first = ProcessingPipelineStatus.Read(
            db.Connection,
            run.ProcessingJobBaselineId,
            firstSource.Id);
        ProcessingPipelineSnapshot all = ProcessingPipelineStatus.Read(
            db.Connection,
            run.ProcessingJobBaselineId);
        ProcessingPipelineSnapshot currentRun = ProcessingPipelineStatus.Read(
            db.Connection,
            run.ProcessingJobBaselineId,
            mailboxImportRunId: run.Id);

        Assert.AreEqual(1, first.Total);
        Assert.AreEqual(1, first.Ready);
        Assert.AreEqual(1, first.Stages.Single(stage =>
            stage.Stage == ProcessingStageKind.Ocr).Pending);
        Assert.AreEqual(0, first.Stages.Single(stage =>
            stage.Stage == ProcessingStageKind.Extract).Pending);
        Assert.AreEqual(2, all.Total);
        Assert.AreEqual(1, currentRun.Total);
    }

    [TestMethod]
    public void Read_ReportsFutureRetryAndRunningLeaseSeparately()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Gmail,
            "first@example.invalid");
        long attachment = ProcessingTestData.AddAttachment(
            db.Connection,
            source.Id,
            "first");
        MailboxImportRunRecord run =
            MailboxImportRunRepository.Create(db.Connection, [source.Id]);
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset future = now.AddMinutes(2);
        ProcessingJobRepository.Enqueue(
            db.Connection,
            "download",
            attachment,
            availableAt: future);

        ProcessingPipelineSnapshot pending = ProcessingPipelineStatus.Read(
            db.Connection,
            run.ProcessingJobBaselineId,
            clock: now);

        Assert.AreEqual(1, pending.Pending);
        Assert.AreEqual(0, pending.Ready);
        Assert.AreEqual(future, pending.NextPendingAt);
        Assert.AreEqual(future, pending.NextActionAt);

        ProcessingJob leased = ProcessingJobRepository.LeaseNext(
            db.Connection,
            "test-worker",
            TimeSpan.FromMinutes(5),
            future)!;
        ProcessingPipelineSnapshot running = ProcessingPipelineStatus.Read(
            db.Connection,
            run.ProcessingJobBaselineId,
            clock: future);

        Assert.AreEqual(leased.Id, db.ScalarLong(
            "SELECT id FROM processing_jobs WHERE status='running';"));
        Assert.AreEqual(1, running.Running);
        Assert.IsNull(running.NextPendingAt);
        Assert.AreEqual(future.AddMinutes(5), running.NextLeaseExpiryAt);
        Assert.AreEqual(future.AddMinutes(5), running.NextActionAt);
    }
}

static class ProcessingTestData
{
    public static MailboxSourceRecord AddSource(
        SqliteConnection connection,
        MailboxProvider provider,
        string externalKey)
        => MailboxSourceRepository.Upsert(
            connection,
            new MailboxSourceDefinition(
                provider,
                externalKey,
                "Test mailbox",
                provider == MailboxProvider.Gmail
                    ? $"gmail-account:{externalKey}"
                    : null));

    public static long AddAttachment(
        SqliteConnection connection,
        long mailboxSourceId,
        string key)
    {
        string entryId = $"test:{key}";
        using (var mail = connection.CreateCommand())
        {
            mail.CommandText = """
                INSERT INTO mails(
                    entry_id,mailbox_source_id,subject,body,sender_email,to_recips,
                    has_attachments,harvested_at)
                VALUES(
                    $entry,$source,'Test Record',
                    'Neutralny tekst wiadomości używany do testowania indeksu',
                    'sender@example.invalid','recipient@example.invalid',1,$now);
                """;
            mail.Parameters.AddWithValue("$entry", entryId);
            mail.Parameters.AddWithValue("$source", mailboxSourceId);
            mail.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            mail.ExecuteNonQuery();
        }

        using (var attachment = connection.CreateCommand())
        {
            attachment.CommandText = """
                INSERT INTO mail_attachments(
                    mail_entry_id,provider,provider_message_key,
                    provider_attachment_key,filename,created_at,updated_at)
                VALUES(
                    $entry,'test',$message,$attachment,'record.txt',$now,$now);
                """;
            attachment.Parameters.AddWithValue("$entry", entryId);
            attachment.Parameters.AddWithValue("$message", $"message-{key}");
            attachment.Parameters.AddWithValue("$attachment", $"attachment-{key}");
            attachment.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            attachment.ExecuteNonQuery();
        }

        using var id = connection.CreateCommand();
        id.CommandText = """
            SELECT id FROM mail_attachments
            WHERE mail_entry_id=$entry;
            """;
        id.Parameters.AddWithValue("$entry", entryId);
        return Convert.ToInt64(id.ExecuteScalar());
    }

    public static MailboxImportRunRecord StartProcessingRun(
        SqliteConnection connection,
        MailboxSourceRecord source)
    {
        MailboxImportRunRecord run =
            MailboxImportRunRepository.Create(connection, [source.Id]);
        Assert.IsTrue(MailboxImportRunRepository.Start(connection, run.Id));
        MailboxImportSourceRecord sourceRun =
            MailboxImportRunRepository.StartNextSource(connection, run.Id)!;
        MailboxImportRunRepository.MarkSourceImported(
            connection,
            sourceRun.Id,
            new MailboxImportProgress("imported", 1, 1, Inserted: 1));
        Assert.AreEqual(
            MailboxImportRunStatus.Processing,
            MailboxImportRunRepository.FinishImportPhase(connection, run.Id));
        return MailboxImportRunRepository.Find(connection, run.Id)!;
    }
}
