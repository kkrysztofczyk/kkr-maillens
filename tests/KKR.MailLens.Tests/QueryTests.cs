using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class QueryTests
{
    [TestMethod]
    public void SourceDateFormatters_WriteTheSameUtcInstant()
    {
        // Ten sam moment w trzech reprezentacjach zrodlowych (Gmail: UTC, IMAP: offset, Outlook: czas lokalny)
        // musi dac identyczny wpis received - inaczej filtry dat i ORDER BY przeplataja zrodla z przesunieciem.
        var instant = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        string expected = "2026-03-10 12:00:00";

        Assert.AreEqual(expected, Imap.FormatReceivedUtc(instant.ToOffset(TimeSpan.FromHours(2))));
        Assert.AreEqual(expected, Outlook.DateStr(() => instant.LocalDateTime));
        Assert.AreEqual("", Imap.FormatReceivedUtc(null));
        Assert.IsNull(Outlook.DateStr(() => "nie-data"));
    }

    [TestMethod]
    public void Run_OrdersMixedSourcesByUtcReceived()
    {
        using var db = new TestDatabase();
        var t0 = new DateTimeOffset(2026, 3, 10, 10, 0, 0, TimeSpan.Zero);
        string gmail = t0.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string imap = Imap.FormatReceivedUtc(t0.AddHours(1).ToOffset(TimeSpan.FromHours(5)));
        string outlook = Outlook.DateStr(() => t0.AddHours(2).LocalDateTime)!;
        Corpus.Upsert(db.Connection,
        [
            Mail("gmail-1", gmail, subject: "MailGmailowy"),
            Mail("imap-1", imap, subject: "MailImapowy"),
            Mail("outlook-1", outlook, subject: "MailOutlookowy"),
        ], "2026-03-10 13:00:00");

        var result = RunQuery(db, "query");

        Assert.AreEqual(0, result.Code);
        int posOutlook = result.Out.IndexOf("MailOutlookowy", StringComparison.Ordinal);
        int posImap = result.Out.IndexOf("MailImapowy", StringComparison.Ordinal);
        int posGmail = result.Out.IndexOf("MailGmailowy", StringComparison.Ordinal);
        Assert.IsTrue(posOutlook >= 0, "brak maila z Outlooka w wynikach");
        Assert.IsTrue(posImap > posOutlook, "IMAP (starszy) powinien byc za Outlookiem (najnowszy)");
        Assert.IsTrue(posGmail > posImap, "Gmail (najstarszy) powinien byc ostatni");
    }

    [TestMethod]
    public void Run_RejectsInvalidDates()
    {
        using var db = new TestDatabase();

        var badFrom = RunQuery(db, "query", "--from", "10.03.2026");
        Assert.AreEqual(1, badFrom.Code);
        StringAssert.Contains(badFrom.Err, "Nieprawidlowa data --from");

        var badTo = RunQuery(db, "query", "--to", "2026-3-1");
        Assert.AreEqual(1, badTo.Code);
        StringAssert.Contains(badTo.Err, "Nieprawidlowa data --to");
    }

    [TestMethod]
    public void Run_DateBoundsCoverWholeDayAndAcceptTime()
    {
        using var db = new TestDatabase();
        Corpus.Upsert(db.Connection,
        [
            Mail("m1", "2026-03-09 08:00:00", subject: "MailPoprzedni"),
            Mail("m2", "2026-03-10 09:30:00", subject: "MailPoranny"),
            Mail("m3", "2026-03-10 23:59:59", subject: "MailWieczorny"),
        ], "2026-03-11 00:00:00");

        var wholeDay = RunQuery(db, "query", "--from", "2026-03-10", "--to", "2026-03-10");
        Assert.AreEqual(0, wholeDay.Code);
        Assert.IsFalse(wholeDay.Out.Contains("MailPoprzedni"));
        Assert.IsTrue(wholeDay.Out.Contains("MailPoranny"));
        Assert.IsTrue(wholeDay.Out.Contains("MailWieczorny"), "--to yyyy-MM-dd ma obejmowac caly dzien (data+1, nie ' 99')");

        var morning = RunQuery(db, "query", "--from", "2026-03-10 00:00", "--to", "2026-03-10 12:00");
        Assert.AreEqual(0, morning.Code);
        Assert.IsTrue(morning.Out.Contains("MailPoranny"));
        Assert.IsFalse(morning.Out.Contains("MailWieczorny"));
    }

    [TestMethod]
    public void Run_SenderFilterTreatsUnderscoreAsLiteral()
    {
        using var db = new TestDatabase();
        Corpus.Upsert(db.Connection,
        [
            Mail("u1", "2026-03-10 10:00:00", sender: "john_doe@example.invalid", subject: "MailTrafiony"),
            Mail("u2", "2026-03-10 11:00:00", sender: "johnxdoe@example.invalid", subject: "MailInny"),
        ], "2026-03-11 00:00:00");

        var result = RunQuery(db, "query", "--sender", "john_doe");

        Assert.AreEqual(0, result.Code);
        Assert.IsTrue(result.Out.Contains("MailTrafiony"));
        Assert.IsFalse(result.Out.Contains("MailInny"), "'_' w --sender ma byc literalem, nie wildcardem LIKE");
    }

    [TestMethod]
    public void Run_SeparatorPassesDashPrefixedTermAndFlagsDoNotConsumeNext()
    {
        using var db = new TestDatabase();
        Corpus.Upsert(db.Connection,
        [
            Mail("d1", "2026-03-10 10:00:00", subject: "Temat --alerty produkcyjne"),
            Mail("d2", "2026-03-10 11:00:00", subject: "Temat zwykly"),
        ], "2026-03-11 00:00:00");

        // ksztalt argumentow z GUI: fraza za '--' nie jest flaga i nie zjada innych argumentow
        var separator = RunQuery(db, "query", "--limit", "50", "--", "--alerty");
        Assert.AreEqual(0, separator.Code);
        Assert.IsTrue(separator.Out.Contains("--alerty produkcyjne"));
        Assert.IsFalse(separator.Out.Contains("Temat zwykly"));

        // nieznana flaga nie konsumuje nastepnego argumentu - '--limit 1' nadal dziala
        var unknownFlag = RunQuery(db, "query", "--nieznana", "--limit", "1");
        Assert.AreEqual(0, unknownFlag.Code);
        StringAssert.Contains(unknownFlag.Out, "-- 1 trafien");
    }

    static HarvestedMail Mail(string id, string received, string sender = "sender@example.invalid",
        string subject = "Temat", string folder = "Inbox") => new()
    {
        EntryId = id,
        SourceIdentity = "test:" + id,
        FolderPath = "test://" + folder,
        FolderLeaf = folder,
        Received = received,
        Sent = received,
        SenderEmail = sender,
        Subject = subject,
        Body = "tresc " + id,
    };

    static (int Code, string Out, string Err) RunQuery(TestDatabase db, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        TextWriter prevOut = Console.Out, prevErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try { return (Query.Run(db.Connection, args), stdout.ToString(), stderr.ToString()); }
        finally { Console.SetOut(prevOut); Console.SetError(prevErr); }
    }
}
