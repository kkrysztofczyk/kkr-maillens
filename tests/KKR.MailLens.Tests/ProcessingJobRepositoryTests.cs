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

        Assert.IsTrue(ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId, priority: 10, availableAt: now));
        Assert.IsFalse(ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId, priority: 10, availableAt: now));

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
        ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId, priority: 10, maxAttempts: 3, availableAt: now);
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
        ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId, priority: 10, maxAttempts: 1, availableAt: now);
        ProcessingJobRepository.LeaseNext(db.Connection, "worker-1", TimeSpan.FromMinutes(1), now);

        Assert.IsNull(ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-2", TimeSpan.FromMinutes(1), now.AddMinutes(2)));
        Assert.AreEqual("failed", db.ScalarText("SELECT status FROM processing_jobs WHERE job_type='extract';"));
        Assert.AreEqual("lease-expired", db.ScalarText("SELECT error_code FROM processing_jobs WHERE job_type='extract';"));
    }

    [TestMethod]
    public void ExpiredLease_PreservesEarlierDiagnosticOnFinalAttempt()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId,
            priority: 10, maxAttempts: 1, availableAt: now);
        ProcessingJobRepository.LeaseNext(db.Connection, "worker-1", TimeSpan.FromMinutes(1), now);
        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE processing_jobs SET error_code='neutral-error',error_message='Neutralny błąd diagnostyczny'
                WHERE job_type='extract';
                """;
            command.ExecuteNonQuery();
        }

        _ = ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-2", TimeSpan.FromMinutes(1), now.AddMinutes(2));

        Assert.AreEqual("neutral-error", db.ScalarText(
            "SELECT error_code FROM processing_jobs WHERE job_type='extract';"));
    }

    [TestMethod]
    public void RenewLease_ExtendsOnlyLeaseOwnedByWorker()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        ProcessingJobRepository.Enqueue(db.Connection, "ocr", attachmentId, priority: 10, availableAt: now);
        ProcessingJobRepository.LeaseNext(db.Connection, "worker-1", TimeSpan.FromMinutes(1), now);

        Assert.IsFalse(ProcessingJobRepository.RenewLease(db.Connection,
            db.ScalarLong("SELECT id FROM processing_jobs WHERE job_type='ocr';"),
            "worker-2", TimeSpan.FromMinutes(5), now.AddSeconds(30)));
        Assert.IsTrue(ProcessingJobRepository.RenewLease(db.Connection,
            db.ScalarLong("SELECT id FROM processing_jobs WHERE job_type='ocr';"),
            "worker-1", TimeSpan.FromMinutes(5), now.AddSeconds(30)));

        Assert.IsNull(ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-2", TimeSpan.FromMinutes(1), now.AddMinutes(2)));
    }

    [TestMethod]
    public void RenewLease_MonitorCadenceOutlastsSequentialOcrFallback()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        // Regresja audytu workera: najgorszy przypadek pierwszej strony batcha PDF to sekwencyjnie
        // render + Tesseract + fallback PaddleOCR bez heartbeatu z wnętrza pipeline'u OCR.
        // Odnowienia w kadencji ProcessingLeaseMonitor muszą utrzymać własność przez cały ten czas.
        var config = new AppConfig();
        TimeSpan sequentialFallback = TimeSpan.FromSeconds(config.OcrPdfRenderTimeoutSeconds
            + config.OcrTimeoutSeconds + config.PaddleOcrTimeoutSeconds);
        TimeSpan leaseDuration = TimeSpan.FromMinutes(5);    // dzierżawa workera z Program.cs
        TimeSpan renewalInterval = TimeSpan.FromSeconds(30); // interwał ProcessingLeaseMonitor dla tej dzierżawy
        Assert.IsTrue(sequentialFallback > leaseDuration,
            "Scenariusz regresji wymaga przetwarzania dłuższego niż pojedyncza dzierżawa.");
        DateTimeOffset start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        ProcessingJobRepository.Enqueue(db.Connection, "ocr", attachmentId, availableAt: start);
        ProcessingJob job = ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-1", leaseDuration, start)!;

        for (DateTimeOffset now = start + renewalInterval; now <= start + sequentialFallback;
            now += renewalInterval)
        {
            Assert.IsNull(ProcessingJobRepository.LeaseNext(db.Connection, "worker-2", leaseDuration, now),
                $"Zadanie OCR zostało przejęte po {(now - start).TotalSeconds:0} s mimo odnowień.");
            Assert.IsTrue(ProcessingJobRepository.RenewLease(
                db.Connection, job.Id, "worker-1", leaseDuration, now));
        }

        Assert.IsTrue(ProcessingJobRepository.Complete(db.Connection, job.Id, "worker-1"));
        Assert.AreEqual("completed", db.ScalarText("SELECT status FROM processing_jobs WHERE id=" + job.Id + ";"));
    }

    [TestMethod]
    public void Complete_OnlyOwnerCanCompleteRunningJob()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId, availableAt: now);
        ProcessingJob job = ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-1", TimeSpan.FromMinutes(5), now)!;

        Assert.IsFalse(ProcessingJobRepository.Complete(db.Connection, job.Id, "worker-2"));
        Assert.AreEqual("running", db.ScalarText("SELECT status FROM processing_jobs WHERE id=" + job.Id + ";"));
        Assert.IsTrue(ProcessingJobRepository.Complete(db.Connection, job.Id, "worker-1"));
        Assert.AreEqual("completed", db.ScalarText("SELECT status FROM processing_jobs WHERE id=" + job.Id + ";"));
        Assert.IsFalse(ProcessingJobRepository.Complete(db.Connection, job.Id, "worker-1"));
    }

    [TestMethod]
    public void Fail_OnlyOwnerCanRetryAndFinishJob()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId,
            maxAttempts: 2, availableAt: now);
        ProcessingJob first = ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-1", TimeSpan.FromMinutes(5), now)!;

        Assert.IsFalse(ProcessingJobRepository.Fail(db.Connection, first.Id, "worker-2",
            "test-error", "Neutralny błąd", TimeSpan.Zero, now));
        Assert.IsTrue(ProcessingJobRepository.Fail(db.Connection, first.Id, "worker-1",
            "test-error", "Neutralny błąd", TimeSpan.Zero, now));
        Assert.AreEqual("pending", db.ScalarText("SELECT status FROM processing_jobs WHERE id=" + first.Id + ";"));

        ProcessingJob second = ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-2", TimeSpan.FromMinutes(5), now)!;
        Assert.IsTrue(ProcessingJobRepository.Fail(db.Connection, second.Id, "worker-2",
            "test-error", "Neutralny błąd", TimeSpan.Zero, now));
        Assert.AreEqual("failed", db.ScalarText("SELECT status FROM processing_jobs WHERE id=" + first.Id + ";"));
    }

    [TestMethod]
    public void StaleWorkerCannotCompleteOrFailReLeasedJob()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId,
            maxAttempts: 3, availableAt: now);
        ProcessingJob first = ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-1", TimeSpan.FromMinutes(1), now)!;
        ProcessingJob second = ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-2", TimeSpan.FromMinutes(5), now.AddMinutes(2))!;

        Assert.AreEqual(first.Id, second.Id);
        Assert.IsFalse(ProcessingJobRepository.Complete(db.Connection, first.Id, "worker-1"));
        Assert.IsFalse(ProcessingJobRepository.Fail(db.Connection, first.Id, "worker-1",
            "stale", "Neutralny błąd", TimeSpan.Zero, now.AddMinutes(2)));
        Assert.AreEqual("running", db.ScalarText("SELECT status FROM processing_jobs WHERE id=" + first.Id + ";"));
        Assert.AreEqual("worker-2", db.ScalarText("SELECT locked_by FROM processing_jobs WHERE id=" + first.Id + ";"));
    }

    [TestMethod]
    public void Abandon_ReturnsOwnedJobWithoutConsumingAttempt()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        DateTimeOffset now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId, availableAt: now);
        ProcessingJob job = ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-1", TimeSpan.FromMinutes(5), now)!;

        Assert.IsFalse(ProcessingJobRepository.Abandon(db.Connection, job.Id, "worker-2", now));
        Assert.IsTrue(ProcessingJobRepository.Abandon(db.Connection, job.Id, "worker-1", now));
        ProcessingJob again = ProcessingJobRepository.LeaseNext(
            db.Connection, "worker-2", TimeSpan.FromMinutes(5), now)!;
        Assert.AreEqual(1, again.Attempts);
    }

    [TestMethod]
    public void RetryFailed_ReturnsFailedJobsToQueueWithFreshAttempts()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId, maxAttempts: 1, availableAt: now);
        ProcessingJob job = ProcessingJobRepository.LeaseNext(db.Connection, "worker-1", TimeSpan.FromMinutes(5), now)!;
        ProcessingJobRepository.Fail(db.Connection, job.Id, "worker-1", "test-error", "Neutralny błąd", TimeSpan.Zero, now);

        Assert.AreEqual(1, ProcessingJobRepository.RetryFailed(db.Connection));
        Assert.AreEqual("pending", db.ScalarText("SELECT status FROM processing_jobs WHERE id=" + job.Id + ";"));
        Assert.AreEqual(0, db.ScalarLong("SELECT attempts FROM processing_jobs WHERE id=" + job.Id + ";"));
    }

    [TestMethod]
    public void RetryFailed_SkipsJobsCollidingWithActiveDuplicate()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId, maxAttempts: 1, availableAt: now);
        ProcessingJob extract = ProcessingJobRepository.LeaseNext(db.Connection, "worker-1", TimeSpan.FromMinutes(5), now)!;
        ProcessingJobRepository.Fail(db.Connection, extract.Id, "worker-1", "test-error", "Neutralny błąd", TimeSpan.Zero, now);
        ProcessingJobRepository.Enqueue(db.Connection, "ocr", attachmentId, maxAttempts: 1, availableAt: now);
        ProcessingJob ocr = ProcessingJobRepository.LeaseNext(db.Connection, "worker-1", TimeSpan.FromMinutes(5), now)!;
        ProcessingJobRepository.Fail(db.Connection, ocr.Id, "worker-1", "test-error", "Neutralny błąd", TimeSpan.Zero, now);
        // Świeży aktywny duplikat extract — powrót failed-extract do 'pending' złamałby indeks.
        Assert.IsTrue(ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId, availableAt: now));

        Assert.AreEqual(1, ProcessingJobRepository.RetryFailed(db.Connection));
        Assert.AreEqual("failed", db.ScalarText("SELECT status FROM processing_jobs WHERE id=" + extract.Id + ";"));
        Assert.AreEqual("pending", db.ScalarText("SELECT status FROM processing_jobs WHERE id=" + ocr.Id + ";"));
    }

    [TestMethod]
    public void RetryFailed_RestoresOnlyNewestOfDuplicateFailedJobs()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId, maxAttempts: 1, availableAt: now);
        ProcessingJob first = ProcessingJobRepository.LeaseNext(db.Connection, "worker-1", TimeSpan.FromMinutes(5), now)!;
        ProcessingJobRepository.Fail(db.Connection, first.Id, "worker-1", "test-error", "Neutralny błąd", TimeSpan.Zero, now);
        ProcessingJobRepository.Enqueue(db.Connection, "extract", attachmentId, maxAttempts: 1, availableAt: now);
        ProcessingJob second = ProcessingJobRepository.LeaseNext(db.Connection, "worker-1", TimeSpan.FromMinutes(5), now)!;
        ProcessingJobRepository.Fail(db.Connection, second.Id, "worker-1", "test-error", "Neutralny błąd", TimeSpan.Zero, now);

        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(1, ProcessingJobRepository.RetryFailed(db.Connection));
        Assert.AreEqual("failed", db.ScalarText("SELECT status FROM processing_jobs WHERE id=" + first.Id + ";"));
        Assert.AreEqual("pending", db.ScalarText("SELECT status FROM processing_jobs WHERE id=" + second.Id + ";"));
    }

    [TestMethod]
    public async Task LeaseMonitor_KeepsSlowJobLeasedUntilCompletion()
    {
        using var db = new TestDatabase();
        long attachmentId = AddAttachment(db);
        TimeSpan lease = TimeSpan.FromSeconds(1);
        ProcessingJobRepository.Enqueue(db.Connection, "download", attachmentId);
        ProcessingJob job = ProcessingJobRepository.LeaseNext(db.Connection, "worker-1", lease)!;

        // Tak jak w Workerze: heartbeat odnawia dzierżawę na osobnym połączeniu,
        // podczas gdy "pobieranie" trwa dłużej niż bazowa dzierżawa.
        await using var monitor = ProcessingLeaseMonitor.Start(() =>
        {
            using var heartbeat = db.OpenAnotherConnection();
            return ProcessingJobRepository.RenewLease(heartbeat, job.Id, "worker-1", lease);
        }, lease, heartbeatInterval: TimeSpan.FromMilliseconds(100));
        await Task.Delay(lease * 2.5);

        monitor.AssertActive();
        Assert.IsNull(ProcessingJobRepository.LeaseNext(db.Connection, "worker-2", TimeSpan.FromMinutes(5)));
        await monitor.StopAsync();
        Assert.IsTrue(ProcessingJobRepository.Complete(db.Connection, job.Id, "worker-1"));
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
