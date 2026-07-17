using System.Text.RegularExpressions;
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
    public static int Harvest(ImapAccount acct, DateTime? from, int maxPerFolder,
        Action<string> onFolder, Action<int, int>? onProgress, Action<List<HarvestedMail>> flush, int batchSize = 500)
    {
        using var client = new ImapClient();
        client.Connect(acct.Host, acct.Port, acct.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
        client.Authenticate(acct.User, acct.GetPassword());

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

    static HarvestedMail MapSummary(IMessageSummary s, string acctName, IMailFolder f, string body, string storeId)
    {
        var env = s.Envelope;
        string entryId = !string.IsNullOrWhiteSpace(env?.MessageId)
            ? env!.MessageId!
            : $"imap:{acctName}:{f.FullName}:{s.UniqueId.Id}";

        var fromBox = env != null ? FirstMailbox(env.From) : null;

        // nazwy zalacznikow z BodyStructure (bez pobierania bajtow)
        var atts = new List<string>();
        foreach (var a in s.Attachments)
        {
            string? fn = a.ContentDisposition?.FileName ?? a.ContentType?.Name;
            if (!string.IsNullOrWhiteSpace(fn)) atts.Add(fn!);
        }

        var when = env?.Date ?? s.InternalDate;
        string recv = when is { } w ? w.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "";
        return new HarvestedMail
        {
            EntryId = entryId,
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
            HasAttachments = atts.Count > 0,
            AttachmentNames = string.Join("; ", atts),
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
