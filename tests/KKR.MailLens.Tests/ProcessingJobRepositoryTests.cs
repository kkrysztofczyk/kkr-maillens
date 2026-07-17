using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class ProcessingJobRepositoryTests
{
    [TestMethod]
    public void Queue_LeasesOnceAndRejectsDuplicateActiveJob()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.IsTrue(ProcessingJobRepository.Enqueue(db.Connection, "download", attachmentId, availableAt: now));
        Assert.IsFalse(ProcessingJobRepository.Enqueue(db.Connection, "download", attachmentId, availableAt: now));

        ProcessingJob? leased = ProcessingJobRepository.LeaseNext(db.Connection, "worker-1", TimeSpan.FromMinutes(5), now);
        Assert.IsNotNull(leased);
        Assert.AreEqual("running", leased.Status);
        Assert.AreEqual(1, leased.Attempts);
        Assert.AreEqual("worker-1", leased.LockedBy);
        Assert.IsNull(ProcessingJobRepository.LeaseNext(db.Connection, "worker-2", TimeSpan.FromMinutes(5), now));
    }

    [TestMethod]
    public void ExpiredLease_ReturnsJobToQueue()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        ProcessingJobRepository.Enqueue(db.Connection, "download", attachmentId, maxAttempts: 3, availableAt: now);
        ProcessingJobRepository.LeaseNext(db.Connection, "worker-1", TimeSpan.FromMinutes(1), now);

        ProcessingJob? recovered = ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-2", TimeSpan.FromMinutes(1), now.AddMinutes(2));

        Assert.IsNotNull(recovered);
        Assert.AreEqual(2, recovered.Attempts);
        Assert.AreEqual("worker-2", recovered.LockedBy);
    }

    [TestMethod]
    public void ExpiredLease_AfterLastAttemptBecomesFailed()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        ProcessingJobRepository.Enqueue(db.Connection, "download", attachmentId, maxAttempts: 1, availableAt: now);
        ProcessingJobRepository.LeaseNext(db.Connection, "worker-1", TimeSpan.FromMinutes(1), now);

        Assert.IsNull(ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-2", TimeSpan.FromMinutes(1), now.AddMinutes(2)));
        Assert.AreEqual("failed", db.ScalarText("SELECT status FROM processing_jobs;"));
        Assert.AreEqual("lease-expired", db.ScalarText("SELECT error_code FROM processing_jobs;"));
    }

    static long AddAttachment(TestDatabase db)
    {
        GmailAccountRecord account = db.AddAccount();
        var part = new GmailApiPart
        {
            PartId = "2",
            MimeType = "application/pdf",
            Filename = "record.pdf",
            AttachmentId = "attachment-1",
            Size = 321,
        };
        GmailStoredMessage message = GmailMessageMapper.Map(GmailTestMessage.Create("m1", extraParts: [part]), account.Id);
        Corpus.Upsert(db.Connection, [GmailMessageMapper.ToHarvested(message)], "2026-01-01 00:00:00");
        MailAttachmentRepository.UpsertGmail(db.Connection, 1, [message]);
        return db.ScalarLong("SELECT id FROM mail_attachments;");
    }
}
