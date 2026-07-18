using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class GmailAttachmentDownloaderTests
{
    [TestMethod]
    public async Task InlineData_IsDecodedValidatedAndHashed()
    {
        byte[] content = Encoding.UTF8.GetBytes("Neutralny tekst");
        var attachment = new GmailAttachmentRecord("2", "", Base64Url(content), "record.txt",
            "text/plain", content.Length, "", false);

        DownloadedAttachment result = await GmailAttachmentDownloader.DownloadAsync(
            new FakeGmailApiClient(), "m1", attachment);

        CollectionAssert.AreEqual(content, result.Bytes);
        Assert.AreEqual(64, result.Sha256.Length);
        Assert.AreEqual("text/plain", result.DetectedMimeType);
    }

    [TestMethod]
    public async Task AttachmentId_UsesApiAndDetectsPdfSignature()
    {
        byte[] content = "%PDF-1.7\nNeutralny tekst"u8.ToArray();
        using var api = new FakeGmailApiClient();
        api.AttachmentBytes["m1\na1"] = content;
        var attachment = new GmailAttachmentRecord("2", "a1", null, "record.bin",
            "application/octet-stream", content.Length, "", false);

        DownloadedAttachment result = await GmailAttachmentDownloader.DownloadAsync(api, "m1", attachment);

        CollectionAssert.AreEqual(content, result.Bytes);
        Assert.AreEqual("application/pdf", result.DetectedMimeType);
    }

    [TestMethod]
    public async Task ApproximateSizeIsAcceptedButGrossMismatchAndLimitAreRejected()
    {
        byte[] content = Encoding.UTF8.GetBytes("Neutralny tekst");
        var wrongSize = new GmailAttachmentRecord("2", "", Base64Url(content), "record.txt",
            "text/plain", content.Length + 1, "", false);
        DownloadedAttachment approximate = await GmailAttachmentDownloader.DownloadAsync(
            new FakeGmailApiClient(), "m1", wrongSize);
        CollectionAssert.AreEqual(content, approximate.Bytes);

        var grossMismatch = wrongSize with { SizeBytes = content.Length + 5_000 };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            GmailAttachmentDownloader.DownloadAsync(new FakeGmailApiClient(), "m1", grossMismatch));

        var tooLarge = wrongSize with { SizeBytes = 100 };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            GmailAttachmentDownloader.DownloadAsync(new FakeGmailApiClient(), "m1", tooLarge, maximumBytes: 10));
    }

    static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
