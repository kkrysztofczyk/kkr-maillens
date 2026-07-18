using System.Security.Cryptography;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace KKR.MailLens;

static class ImapAttachmentDownloader
{
    public static async Task<DownloadedAttachment> DownloadAsync(ImapAccount account, string sessionKeyHex,
        MailAttachmentRepository.Item attachment, long maximumBytes = GmailAttachmentDownloader.DefaultMaximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (attachment.SizeBytes > maximumBytes)
            throw new InvalidDataException("Załącznik przekracza dozwolony limit rozmiaru.");

        ImapMessageLocator message = ImapMessageLocator.Decode(attachment.ProviderMessageKey);
        ImapPartLocator part = ImapPartLocator.Decode(attachment.PartId);
        if (!string.Equals(account.Name, message.AccountName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Konto IMAP nie zgadza się z identyfikatorem załącznika.");
        if (!MimeUtils.TryParse(part.TransferEncoding, out ContentEncoding encoding))
            encoding = ContentEncoding.Default;

        using var client = new ImapClient();
        await client.ConnectAsync(account.Host, account.Port,
            account.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
            cancellationToken).ConfigureAwait(false);
        await client.AuthenticateAsync(account.User, account.GetPassword(sessionKeyHex), cancellationToken)
            .ConfigureAwait(false);
        IMailFolder folder = await client.GetFolderAsync(message.FolderFullName, cancellationToken)
            .ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        if (folder.UidValidity != message.UidValidity)
            throw new InvalidDataException("UIDVALIDITY folderu IMAP uległo zmianie; wymagany jest ponowny import metadanych.");

        using Stream encoded = await folder.GetStreamAsync(new UniqueId(message.Uid), part.PartSpecifier,
            cancellationToken).ConfigureAwait(false);
        using var content = new MimeContent(encoded, encoding);
        using var plaintext = new BoundedMemoryStream(maximumBytes);
        await content.DecodeToAsync(plaintext, cancellationToken).ConfigureAwait(false);
        byte[] bytes = plaintext.ToArray();
        try
        {
            if (bytes.Length == 0) throw new InvalidDataException("Załącznik IMAP jest pusty.");
            string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string detectedMimeType = FileTypeDetector.Detect(attachment.Filename, attachment.MimeType, bytes).MimeType;
            try { await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false); }
            catch { }
            return new DownloadedAttachment(bytes, sha256, detectedMimeType);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }
}

sealed class BoundedMemoryStream(long maximumBytes) : MemoryStream
{
    void EnsureCapacityFor(int count)
    {
        if (count < 0 || Position > maximumBytes - count)
            throw new InvalidDataException("Pobrany załącznik przekracza dozwolony limit rozmiaru.");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureCapacityFor(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureCapacityFor(buffer.Length);
        base.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        EnsureCapacityFor(count);
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        EnsureCapacityFor(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }

    public override void WriteByte(byte value)
    {
        EnsureCapacityFor(1);
        base.WriteByte(value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && TryGetBuffer(out ArraySegment<byte> buffer) && buffer.Array is not null)
            CryptographicOperations.ZeroMemory(buffer.Array.AsSpan(buffer.Offset, (int)Length));
        base.Dispose(disposing);
    }
}
