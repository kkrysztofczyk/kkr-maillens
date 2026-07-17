using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class GmailMessageMapperTests
{
    [TestMethod]
    public void DecodeBase64Url_DecodesUtf8()
    {
        byte[] decoded = GmailMessageMapper.DecodeBase64Url(GmailTestMessage.Base64Url("Neutralny tekst ąć"));
        Assert.AreEqual("Neutralny tekst ąć", Encoding.UTF8.GetString(decoded));
    }

    [TestMethod]
    public void Map_TextMessage_MapsHeadersAndFlags()
    {
        var source = GmailTestMessage.Create("m1", labels: ["INBOX", "UNREAD", "Label_Test"]);
        GmailStoredMessage result = GmailMessageMapper.Map(source, 7);

        Assert.AreEqual("gmail:7:m1", result.EntryId);
        Assert.AreEqual("<m1@example.invalid>", result.RfcMessageId);
        Assert.AreEqual("Neutral Sender <sender@example.invalid>", result.Sender);
        Assert.AreEqual("recipient@example.invalid", result.Recipients);
        Assert.AreEqual("copy@example.invalid", result.Cc);
        Assert.AreEqual("Test Record", result.Subject);
        Assert.AreEqual("Neutralny tekst", result.BodyText);
        Assert.IsTrue(result.IsUnread);
        CollectionAssert.AreEquivalent(new[] { "INBOX", "UNREAD", "Label_Test" }, result.LabelIds.ToArray());
    }

    [TestMethod]
    public void Map_HtmlOnly_StripsScriptsStylesAndMarkupForIndex()
    {
        string html = "<style>.x{display:none}</style><script>ignore()</script><p>Neutralny <b>tekst</b>&nbsp;HTML</p>";
        GmailStoredMessage result = GmailMessageMapper.Map(GmailTestMessage.Create("m2", body: html, html: true), 1);

        Assert.AreEqual(html, result.BodyHtml);
        Assert.AreEqual("Neutralny tekst HTML", result.BodyText);
        Assert.IsFalse(result.BodyText.Contains("ignore", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Map_MultipartAlternative_PrefersPlainText()
    {
        var source = GmailTestMessage.Create("m3", body: "Preferowany tekst", extraParts:
        [
            new GmailApiPart
            {
                MimeType = "text/html",
                Data = GmailTestMessage.Base64Url("<p>Tekst HTML</p>"),
                Headers = [new("Content-Type", "text/html; charset=utf-8")],
            },
        ]);
        GmailStoredMessage result = GmailMessageMapper.Map(source, 1);

        Assert.AreEqual("Preferowany tekst", result.BodyText);
        Assert.AreEqual("<p>Tekst HTML</p>", result.BodyHtml);
    }

    [TestMethod]
    public void Map_AttachmentAndNestedPart_PreservesMetadataAndText()
    {
        var nested = new GmailApiPart
        {
            MimeType = "message/rfc822",
            Parts =
            [
                new GmailApiPart
                {
                    MimeType = "text/plain",
                    Data = GmailTestMessage.Base64Url("Tekst zagniezdzony"),
                    Headers = [new("Content-Type", "text/plain; charset=utf-8")],
                },
            ],
        };
        var attachment = new GmailApiPart
        {
            MimeType = "application/octet-stream",
            Filename = "record.bin",
            AttachmentId = "att-1",
            Size = 123,
        };
        GmailStoredMessage result = GmailMessageMapper.Map(
            GmailTestMessage.Create("m4", extraParts: [nested, attachment]), 1);

        StringAssert.Contains(result.BodyText, "Tekst zagniezdzony");
        Assert.HasCount(1, result.Attachments);
        Assert.AreEqual("att-1", result.Attachments[0].GmailAttachmentId);
        Assert.AreEqual("record.bin", result.Attachments[0].Filename);
        Assert.AreEqual(123, result.Attachments[0].SizeBytes);
    }

    [TestMethod]
    public void Map_MissingMessageId_RemainsImportableByGmailId()
    {
        GmailApiMessage original = GmailTestMessage.Create("m5");
        var source = new GmailApiMessage
        {
            Id = original.Id,
            ThreadId = original.ThreadId,
            InternalDateUnixMs = original.InternalDateUnixMs,
            SizeEstimate = original.SizeEstimate,
            LabelIds = original.LabelIds,
            Payload = new GmailApiPart
            {
                MimeType = original.Payload.MimeType,
                Headers = original.Payload.Headers.Where(x => !x.Name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase)).ToArray(),
                Parts = original.Payload.Parts,
            },
        };

        GmailStoredMessage result = GmailMessageMapper.Map(source, 3);
        Assert.AreEqual("", result.RfcMessageId);
        Assert.AreEqual("gmail:3:m5", result.EntryId);
    }

    [TestMethod]
    public void Map_DecodesEncodedSubject()
    {
        GmailApiMessage original = GmailTestMessage.Create("m6");
        var source = new GmailApiMessage
        {
            Id = original.Id,
            ThreadId = original.ThreadId,
            InternalDateUnixMs = original.InternalDateUnixMs,
            SizeEstimate = original.SizeEstimate,
            LabelIds = original.LabelIds,
            Payload = new GmailApiPart
            {
                MimeType = original.Payload.MimeType,
                Headers = original.Payload.Headers.Select(x => x.Name == "Subject"
                    ? new GmailHeader("Subject", "=?UTF-8?B?VGVzdCBSZWNvcmQ=?=") : x).ToArray(),
                Parts = original.Payload.Parts,
            },
        };
        Assert.AreEqual("Test Record", GmailMessageMapper.Map(source, 1).Subject);
    }

    [TestMethod]
    public void Map_MessageWithoutTextBody_IsStillSaved()
    {
        GmailApiMessage original = GmailTestMessage.Create("m7");
        var source = new GmailApiMessage
        {
            Id = original.Id,
            ThreadId = original.ThreadId,
            InternalDateUnixMs = original.InternalDateUnixMs,
            SizeEstimate = original.SizeEstimate,
            LabelIds = Array.Empty<string>(), // brak INBOX oznacza m.in. wiadomosc zarchiwizowana
            Payload = new GmailApiPart
            {
                MimeType = "multipart/mixed",
                Headers = original.Payload.Headers,
                Parts =
                [
                    new GmailApiPart
                    {
                        MimeType = "application/octet-stream",
                        Filename = "record.bin",
                        AttachmentId = "att-7",
                        Size = 10,
                    },
                ],
            },
        };

        GmailStoredMessage result = GmailMessageMapper.Map(source, 1);
        Assert.AreEqual("", result.BodyText);
        Assert.HasCount(1, result.Attachments);
        Assert.IsFalse(result.IsTrashed);
        Assert.IsFalse(result.IsSpam);
    }
}
