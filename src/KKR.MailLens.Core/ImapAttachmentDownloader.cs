using System.Security.Cryptography;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace KKR.MailLens;

static class ImapAttachmentDownloader
{
    /// <summary>Seam testowy: pozwala testom podstawić klienta akceptującego lokalny certyfikat.</summary>
    internal static Func<ImapClient>? ClientFactory { get; set; }

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

        using ImapClient client = ClientFactory?.Invoke() ?? new ImapClient();
        await client.ConnectAsync(account.Host, account.Port,
            account.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
            cancellationToken).ConfigureAwait(false);
        await client.AuthenticateAsync(account.User, account.GetPassword(sessionKeyHex), cancellationToken)
            .ConfigureAwait(false);

        using Stream encoded = await OpenPartStreamAsync(client, message, part,
            MessageIdCandidate(attachment), cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Otwiera strumien czesci MIME najpierw z zapisanego lokatora. Lokator bywa nieaktualny:
    /// ta sama wiadomosc w wielu folderach dzieli wiersz zalacznika, a klucz konfliktu zachowuje
    /// tylko OSTATNIO zebrana kopie; folder mogl tez zostac usuniety/przemianowany albo zmienilo
    /// sie UIDVALIDITY. Zamiast trwale odrzucac pobranie, szukamy wtedy innej kopii po Message-Id
    /// (ta sama tresc MIME => ten sam part specifier). Swiadomy kompromis: zero zmian schematu
    /// i zero duplikacji magazynu, kosztem dodatkowych SEARCH przy chybieniu; nieaktualny lokator
    /// w bazie odswiezy dopiero kolejny harvest.
    /// </summary>
    static async Task<Stream> OpenPartStreamAsync(ImapClient client, ImapMessageLocator message,
        ImapPartLocator part, string? messageId, CancellationToken cancellationToken)
    {
        // Po dryfie UIDVALIDITY NIE pobieramy tresci starym UID (moglby wskazywac inna wiadomosc);
        // zamiast tego probujemy odnalezc kopie po Message-Id. Zapamietujemy przyczyne, by komunikat
        // bledu przy nieudanej odbudowie nadal wskazywal dryf (diagnostyka + ponowny import metadanych).
        bool uidValidityDrifted = false;
        try
        {
            IMailFolder folder = await client.GetFolderAsync(message.FolderFullName, cancellationToken)
                .ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            if (folder.UidValidity == message.UidValidity)
                return await folder.GetStreamAsync(new UniqueId(message.Uid), part.PartSpecifier,
                    cancellationToken).ConfigureAwait(false);
            uidValidityDrifted = true;
        }
        catch (FolderNotFoundException) { }
        catch (MessageNotFoundException) { }

        if (string.IsNullOrEmpty(messageId))
            throw new InvalidDataException(uidValidityDrifted
                ? "UIDVALIDITY folderu IMAP uległo zmianie, a brak Message-Id uniemożliwia znalezienie innej kopii; wymagany jest ponowny import metadanych."
                : "Wiadomość IMAP nie istnieje pod zapisanym lokatorem, a brak Message-Id uniemożliwia znalezienie innej kopii.");
        foreach (IMailFolder folder in Imap.TargetFolders(client))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IList<UniqueId> uids;
            try
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
                uids = await folder.SearchAsync(SearchQuery.HeaderContains("Message-Id", messageId),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            for (int i = uids.Count - 1; i >= 0; i--)
            {
                try
                {
                    return await folder.GetStreamAsync(uids[i], part.PartSpecifier, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (MessageNotFoundException) { }
            }
        }
        throw new InvalidDataException(uidValidityDrifted
            ? "UIDVALIDITY folderu IMAP uległo zmianie i nie znaleziono innej kopii wiadomości; wymagany jest ponowny import metadanych."
            : "Wiadomość IMAP nie istnieje pod zapisanym lokatorem ani w żadnym innym folderze konta.");
    }

    // entry_id wiadomosci bywa: surowym Message-Id (wiersze sprzed migracji identyfikatorow),
    // syntetycznym "imap:{konto}:{folder}:{uid}" (koperta bez Message-Id) albo haszem
    // "imap:<sha256>" (nowe wiersze) - tylko pierwsza forma nadaje sie do SEARCH HEADER.
    internal static string? MessageIdCandidate(MailAttachmentRepository.Item attachment)
    {
        string id = attachment.MailEntryId.Trim().Trim('<', '>').Trim();
        return id.Length == 0 || id.StartsWith("imap:", StringComparison.OrdinalIgnoreCase) ? null : id;
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
