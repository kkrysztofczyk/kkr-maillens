using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class CorpusCancellationTests
{
    [TestMethod]
    public void Upsert_CancellationRollsBackCurrentBatch()
    {
        using var db = new TestDatabase();
        GmailAccountRecord account = db.AddAccount();
        HarvestedMail first = GmailMessageMapper.ToHarvested(
            GmailMessageMapper.Map(GmailTestMessage.Create("cancel-1"), account.Id));
        HarvestedMail second = GmailMessageMapper.ToHarvested(
            GmailMessageMapper.Map(GmailTestMessage.Create("cancel-2"), account.Id));
        using var cancellation = new CancellationTokenSource();

        IEnumerable<HarvestedMail> Batch()
        {
            yield return first;
            cancellation.Cancel();
            yield return second;
        }

        Assert.Throws<OperationCanceledException>(() => Corpus.Upsert(
            db.Connection, Batch(), "2026-01-01 00:00:00", cancellation.Token));

        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM mails;"));
        Assert.AreEqual(0, db.ScalarLong("SELECT count(*) FROM mails_fts;"));
    }
}
