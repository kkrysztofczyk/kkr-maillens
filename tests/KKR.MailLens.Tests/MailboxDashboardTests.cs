using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class MailboxDashboardTests
{
    [TestMethod]
    public void Read_CombinesConfiguredSourcesMessagesAndActivePipeline()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord first = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Gmail,
            "first@example.invalid");
        MailboxSourceRecord second = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Imap,
            "imap-first");
        long firstAttachment = ProcessingTestData.AddAttachment(
            db.Connection,
            first.Id,
            "first");
        ProcessingTestData.AddAttachment(
            db.Connection,
            first.Id,
            "second");
        MailboxImportRunRecord run =
            ProcessingTestData.StartProcessingRun(db.Connection, first);
        ProcessingJobRepository.Enqueue(
            db.Connection,
            "extract",
            firstAttachment);

        MailboxDashboardSnapshot dashboard =
            MailboxDashboard.Read(db.Connection);

        Assert.AreEqual(2, dashboard.Sources.Count);
        Assert.AreEqual(2, dashboard.EnabledSources);
        Assert.AreEqual(2, dashboard.MessageCount);
        Assert.AreEqual(run.Id, dashboard.ActiveRun?.Id);
        Assert.AreEqual(1, dashboard.ActiveRunSources.Count);
        Assert.AreEqual(1, dashboard.Processing?.Pending);
        Assert.AreEqual(
            MailboxImportSourceStatus.Imported,
            dashboard.Sources.Single(item =>
                item.Source.Id == first.Id).LatestImport?.Status);
        Assert.AreEqual(
            0,
            dashboard.Sources.Single(item =>
                item.Source.Id == second.Id).MessageCount);
    }

    [TestMethod]
    public void Read_ReturnsLatestImportForEachSourceWithoutActiveRun()
    {
        using var db = new TestDatabase();
        MailboxSourceRecord source = ProcessingTestData.AddSource(
            db.Connection,
            MailboxProvider.Gmail,
            "first@example.invalid");
        MailboxImportRunRecord run =
            ProcessingTestData.StartProcessingRun(db.Connection, source);
        MailboxImportRunRepository.CompleteProcessing(db.Connection, run.Id);

        MailboxDashboardSnapshot dashboard =
            MailboxDashboard.Read(db.Connection);

        Assert.IsNull(dashboard.ActiveRun);
        Assert.HasCount(0, dashboard.ActiveRunSources);
        Assert.IsNull(dashboard.Processing);
        Assert.AreEqual(
            MailboxImportSourceStatus.Imported,
            dashboard.Sources.Single().LatestImport?.Status);
    }
}
