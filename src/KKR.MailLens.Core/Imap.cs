using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace KKR.MailLens;

/// <summary>
/// Harvest przez IMAP (MailKit) - zrodlo niezalezne od klienta desktopowego.
/// Mapuje MimeMessage -> HarvestedMail; entry_id = Message-Id (globalny, dedup cross-source).
/// Pomija foldery Trash/Junk/Drafts/All-Mail. Streaming: flush(batch) co batchSize.
/// </summary>
static class Imap
{
    public static int Harvest(ImapAccount acct, string sessionKeyHex, DateTime? from, int maxPerFolder,
        Action<string> onFolder, Action<int, int>? onProgress, Action<List<HarvestedMail>> flush, int batchSize = 500)
    {
        using var client = new ImapClient();
        client.Connect(acct.Host, acct.Port, acct.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
        client.Authenticate(acct.User, acct.GetPassword(sessionKeyHex));

        // foldery-cele (Inbox + reszta z personal namespace), pomijajac systemowe/dublujace
        var targets = new List<IMailFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(IMailFolder f) { if (!Skip(f) && seen.Add(f.FullName)) targets.Add(f); }
        try { Add(client.Inbox); } catch { }
        foreach (var ns in client.PersonalNamespaces)
        {
            IList<IMailFolder> roots;
            try { roots = client.GetFolders(ns); } catch { continue; }
            foreach (var root in roots) CollectRec(root, Add);
        }

        // total (dla %) - otwarcie readonly daje Count
        int total = 0;
        var counts = new Dictionary<string, int>();
        foreach (var f in targets)
        {
            try { f.Open(FolderAccess.ReadOnly); int cnt = Math.Min(f.Count, maxPerFolder); counts[f.FullName] = cnt; total += cnt; }
            catch { counts[f.FullName] = 0; }
        }

        var acc = new List<HarvestedMail>();
        int done = 0, totalMails = 0;
        string storeId = "imap:" + acct.Name;

        foreach (var f in targets)
        {
            onFolder($"{acct.Name} / {f.FullName}");
            IList<UniqueId> uids;
            try
            {
                if (!f.IsOpen) f.Open(FolderAccess.ReadOnly); // count juz otworzyl wiekszosc; nie otwieramy dwa razy
                uids = from is { } d ? f.Search(SearchQuery.DeliveredAfter(d)) : f.Search(SearchQuery.All);
            }
            catch { continue; }

            // najnowsze najpierw, cap na folder
            var ordered = new List<UniqueId>(uids);
            ordered.Sort((a, b) => b.Id.CompareTo(a.Id));
            if (ordered.Count > maxPerFolder) ordered = ordered.GetRange(0, maxPerFolder);
            if (ordered.Count == 0) continue;

            // Metadane wsadowo (koperta + struktura), BEZ pobierania bajtow zalacznikow.
            IList<IMessageSummary> summaries;
            try { summaries = f.Fetch(ordered, MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure | MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.InternalDate); }
            catch { continue; }

            foreach (var s in summaries)
            {
                done++;
                if (onProgress != null && done % 100 == 0) onProgress(done, total);

                // pobierz TYLKO czesc tekstowa (nie caly MIME z zalacznikami)
                string body = "";
                try
                {
                    var part = s.TextBody ?? s.HtmlBody;
                    if (part != null && f.GetBodyPart(s.UniqueId, part) is TextPart tp)
                        body = part == s.HtmlBody ? StripHtml(tp.Text) : tp.Text;
                }
                catch { }

                acc.Add(MapSummary(s, acct.Name, f, body, storeId));
                totalMails++;
                if (acc.Count >= batchSize) { flush(acc); acc.Clear(); }
            }
        }
        if (acc.Count > 0) flush(acc);
        if (onProgress != null) onProgress(done, total);
        try { client.Disconnect(true); } catch { }
        return totalMails;
    }

    static void CollectRec(IMailFolder folder, Action<IMailFolder> add)
    {
        add(folder);
        IList<IMailFolder> subs;
        try { subs = folder.GetSubfolders(false); } catch { return; }
        foreach (var s in subs) CollectRec(s, add);
    }

    // Pomijamy kosz/spam/wersje-robocze i folder oznaczony \All (dubluje wszystko); dedup po Message-Id
    // i tak scala etykiety, ale All Mail to zbedne tysiace. NoSelect/NonExistent = nieotwieralne.
    static bool Skip(IMailFolder f)
    {
        var a = f.Attributes;
        return a.HasFlag(FolderAttributes.Trash) || a.HasFlag(FolderAttributes.Junk)
            || a.HasFlag(FolderAttributes.Drafts) || a.HasFlag(FolderAttributes.All)
            || a.HasFlag(FolderAttributes.NoSelect) || a.HasFlag(FolderAttributes.NonExistent);
    }

    // Korpus przechowuje received/sent w UTC (jak Gmail/Outlook) - jedna os czasu dla filtrow i ORDER BY.
    internal static string FormatReceivedUtc(DateTimeOffset? when) =>
        when is { } w ? w.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";

    static HarvestedMail MapSummary(IMessageSummary s, string acctName, IMailFolder f, string body, string storeId)
    {
        var env = s.Envelope;
        string legacyEntryId = !string.IsNullOrWhiteSpace(env?.MessageId)
            ? env!.MessageId!
            : $"imap:{acctName}:{f.FullName}:{s.UniqueId.Id}";
        string providerMessageKey = new ImapMessageLocator(acctName, f.FullName, f.UidValidity,
            s.UniqueId.Id).Encode();
        string sourceIdentity = MailSourceIdentity.Create("imap", providerMessageKey);

        var fromBox = env != null ? FirstMailbox(env.From) : null;

        // Metadane i stabilne identyfikatory czesci MIME, bez pobierania bajtow zalacznikow.
        var attachments = new List<HarvestedAttachment>();
        int attachmentOrdinal = 0;
        foreach (var a in s.Attachments)
        {
            attachmentOrdinal++;
            string partSpecifier = a.PartSpecifier ?? "";
            if (partSpecifier.Length == 0) continue;
            string? fn = a.ContentDisposition?.FileName ?? a.ContentType?.Name;
            string filename = string.IsNullOrWhiteSpace(fn) ? $"attachment-{attachmentOrdinal}" : fn;
            string mimeType = a.ContentType?.MimeType ?? "application/octet-stream";
            string partLocator = new ImapPartLocator(partSpecifier, a.ContentTransferEncoding ?? "default").Encode();
            attachments.Add(new HarvestedAttachment(partSpecifier, partLocator, filename, mimeType,
                a.Octets, a.ContentId ?? "", string.Equals(a.ContentDisposition?.Disposition, "inline",
                    StringComparison.OrdinalIgnoreCase)));
        }

        string recv = FormatReceivedUtc(env?.Date ?? s.InternalDate);
        return new HarvestedMail
        {
            EntryId = sourceIdentity,
            SourceIdentity = sourceIdentity,
            LegacyEntryId = legacyEntryId,
            StoreId = storeId,
            FolderPath = $"imap://{acctName}/{f.FullName}",
            FolderLeaf = f.Name,
            ConversationId = "",
            Received = recv,
            Sent = recv,
            SenderName = fromBox?.Name ?? "",
            SenderEmail = fromBox?.Address ?? "",
            ToRecips = env != null ? JoinAddrs(env.To) : "",
            CcRecips = env != null ? JoinAddrs(env.Cc) : "",
            Subject = env?.Subject ?? "",
            Body = body,
            Categories = "",
            AttachmentProvider = "imap",
            ProviderMessageKey = providerMessageKey,
            Attachments = attachments,
            HasAttachments = attachments.Count > 0,
            AttachmentNames = string.Join("; ", attachments.Select(attachment => attachment.Filename)),
            Size = (int)(s.Size ?? 0),
            Unread = false,
        };
    }

    static MailboxAddress? FirstMailbox(InternetAddressList list)
    {
        foreach (var a in list) if (a is MailboxAddress m) return m;
        return null;
    }

    static string JoinAddrs(InternetAddressList list)
    {
        var parts = new List<string>();
        foreach (var a in list)
            if (a is MailboxAddress m)
                parts.Add(string.IsNullOrEmpty(m.Name) ? m.Address : $"{m.Name} <{m.Address}>");
        return string.Join("; ", parts);
    }

    static string StripHtml(string html)
    {
        string t = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        t = Regex.Replace(t, "<[^>]+>", " ");
        t = System.Net.WebUtility.HtmlDecode(t);
        return Regex.Replace(t, "\\s+", " ").Trim();
    }
}

sealed record ImapMessageLocator(string AccountName, string FolderFullName, uint UidValidity, uint Uid)
{
    const int CurrentVersion = 1;
    sealed record Payload(int Version, string AccountName, string FolderFullName, uint UidValidity, uint Uid);

    public string Encode() => JsonSerializer.Serialize(
        new Payload(CurrentVersion, AccountName, FolderFullName, UidValidity, Uid));

    public static ImapMessageLocator Decode(string value)
    {
        Payload payload;
        try { payload = JsonSerializer.Deserialize<Payload>(value) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("Nieprawidłowy identyfikator wiadomości IMAP.", ex); }
        if (payload.Version != CurrentVersion || string.IsNullOrWhiteSpace(payload.AccountName)
            || string.IsNullOrWhiteSpace(payload.FolderFullName) || payload.UidValidity == 0 || payload.Uid == 0)
            throw new InvalidDataException("Nieprawidłowy identyfikator wiadomości IMAP.");
        return new ImapMessageLocator(payload.AccountName, payload.FolderFullName, payload.UidValidity, payload.Uid);
    }
}

sealed record ImapPartLocator(string PartSpecifier, string TransferEncoding)
{
    const int CurrentVersion = 1;
    sealed record Payload(int Version, string PartSpecifier, string TransferEncoding);

    public string Encode() => JsonSerializer.Serialize(
        new Payload(CurrentVersion, PartSpecifier, TransferEncoding));

    public static ImapPartLocator Decode(string value)
    {
        Payload payload;
        try { payload = JsonSerializer.Deserialize<Payload>(value) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("Nieprawidłowy identyfikator części MIME.", ex); }
        if (payload.Version != CurrentVersion || string.IsNullOrWhiteSpace(payload.PartSpecifier))
            throw new InvalidDataException("Nieprawidłowy identyfikator części MIME.");
        return new ImapPartLocator(payload.PartSpecifier,
            string.IsNullOrWhiteSpace(payload.TransferEncoding) ? "default" : payload.TransferEncoding);
    }
}
