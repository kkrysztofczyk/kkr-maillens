using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace KKR.MailLens;

static partial class GmailMessageMapper
{
    static GmailMessageMapper() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static GmailStoredMessage Map(GmailApiMessage source, long accountId)
    {
        if (string.IsNullOrWhiteSpace(source.Id)) throw new FormatException("Brak identyfikatora wiadomosci Gmail.");

        var plain = new List<string>();
        var html = new List<string>();
        var attachments = new List<GmailAttachmentRecord>();
        Visit(source.Payload, plain, html, attachments);

        string bodyHtml = JoinBodies(html);
        string bodyText = JoinBodies(plain);
        if (bodyText.Length == 0 && bodyHtml.Length > 0) bodyText = HtmlToText(bodyHtml);

        string sender = Header(source.Payload, "From");
        string sentAt = ParseHeaderDate(Header(source.Payload, "Date"));
        string internalDate = UnixDate(source.InternalDateUnixMs);

        return new GmailStoredMessage
        {
            AccountId = accountId,
            GmailMessageId = source.Id,
            GmailThreadId = source.ThreadId ?? "",
            RfcMessageId = Header(source.Payload, "Message-ID"),
            InternalDate = internalDate,
            SentAt = sentAt.Length == 0 ? internalDate : sentAt,
            Sender = sender,
            Recipients = Header(source.Payload, "To"),
            Cc = Header(source.Payload, "Cc"),
            Bcc = Header(source.Payload, "Bcc"),
            Subject = Header(source.Payload, "Subject"),
            BodyText = bodyText,
            BodyHtml = bodyHtml,
            IsUnread = source.LabelIds.Contains("UNREAD", StringComparer.Ordinal),
            IsTrashed = source.LabelIds.Contains("TRASH", StringComparer.Ordinal),
            IsSpam = source.LabelIds.Contains("SPAM", StringComparer.Ordinal),
            SizeBytes = Math.Max(0, source.SizeEstimate),
            LabelIds = source.LabelIds.Distinct(StringComparer.Ordinal).ToArray(),
            Attachments = attachments,
        };
    }

    public static HarvestedMail ToHarvested(GmailStoredMessage message, IReadOnlyDictionary<string, string>? labelNames = null)
    {
        var (name, email) = SplitSender(message.Sender);
        string categories = string.Join("; ", message.LabelIds.Select(id =>
            labelNames != null && labelNames.TryGetValue(id, out var label) ? label : id));

        return new HarvestedMail
        {
            EntryId = message.EntryId,
            StoreId = $"gmail:{message.AccountId}",
            FolderPath = $"gmail://{message.AccountId}",
            FolderLeaf = "gmail",
            ConversationId = message.GmailThreadId,
            Received = message.InternalDate,
            Sent = message.SentAt,
            SenderName = name,
            SenderEmail = email,
            ToRecips = message.Recipients,
            CcRecips = message.Cc,
            Subject = message.Subject,
            Body = message.BodyText,
            HasAttachments = message.Attachments.Count > 0,
            AttachmentNames = string.Join("; ", message.Attachments.Select(x => x.Filename).Where(x => x.Length > 0)),
            Size = message.SizeBytes > int.MaxValue ? int.MaxValue : (int)message.SizeBytes,
            Unread = message.IsUnread,
            Categories = categories,
        };
    }

    public static byte[] DecodeBase64Url(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<byte>();
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }

    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        string text = ScriptStyleRegex().Replace(html, " ");
        text = HtmlCommentRegex().Replace(text, " ");
        text = BreakRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = HorizontalWhitespaceRegex().Replace(text, " ");
        text = NewlineWhitespaceRegex().Replace(text, "\n");
        return ExcessNewlinesRegex().Replace(text, "\n\n").Trim();
    }

    static void Visit(GmailApiPart part, List<string> plain, List<string> html, List<GmailAttachmentRecord> attachments)
    {
        string mime = (part.MimeType ?? "application/octet-stream").Trim().ToLowerInvariant();
        bool namedAttachment = !string.IsNullOrWhiteSpace(part.Filename);
        bool binaryAttachment = !string.IsNullOrWhiteSpace(part.AttachmentId) && !mime.StartsWith("text/", StringComparison.Ordinal);

        if (namedAttachment || binaryAttachment)
        {
            attachments.Add(new GmailAttachmentRecord(
                part.AttachmentId ?? "",
                DecodeHeader(part.Filename ?? ""),
                mime,
                Math.Max(0, part.Size)));
        }

        if (!namedAttachment && mime == "text/plain" && !string.IsNullOrEmpty(part.Data))
        {
            string text = DecodePartText(part);
            if (text.Length > 0) plain.Add(text);
        }
        else if (!namedAttachment && mime == "text/html" && !string.IsNullOrEmpty(part.Data))
        {
            string text = DecodePartText(part);
            if (text.Length > 0) html.Add(text);
        }

        foreach (var child in part.Parts) Visit(child, plain, html, attachments);
    }

    static string DecodePartText(GmailApiPart part)
    {
        try
        {
            byte[] bytes = DecodeBase64Url(part.Data ?? "");
            string contentType = part.Headers.FirstOrDefault(x => x.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            string charset = CharsetRegex().Match(contentType) is { Success: true } m ? m.Groups[1].Value.Trim('"', '\'') : "utf-8";
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(charset); }
            catch { encoding = Encoding.UTF8; }
            return encoding.GetString(bytes).Trim();
        }
        catch { return ""; }
    }

    static string Header(GmailApiPart payload, string name)
    {
        var values = payload.Headers
            .Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(x => DecodeHeader(x.Value))
            .Where(x => x.Length > 0);
        return string.Join("; ", values);
    }

    static string DecodeHeader(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return EncodedWordRegex().Replace(value, match =>
        {
            try
            {
                string charset = match.Groups[1].Value;
                bool base64 = match.Groups[2].Value.Equals("B", StringComparison.OrdinalIgnoreCase);
                string encoded = match.Groups[3].Value;
                byte[] bytes = base64 ? Convert.FromBase64String(encoded) : DecodeQ(encoded);
                return Encoding.GetEncoding(charset).GetString(bytes);
            }
            catch { return match.Value; }
        }).Trim();
    }

    static byte[] DecodeQ(string encoded)
    {
        using var ms = new MemoryStream();
        for (int i = 0; i < encoded.Length; i++)
        {
            char ch = encoded[i];
            if (ch == '_') { ms.WriteByte((byte)' '); continue; }
            if (ch == '=' && i + 2 < encoded.Length && byte.TryParse(encoded.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            { ms.WriteByte(b); i += 2; continue; }
            ms.WriteByte((byte)ch);
        }
        return ms.ToArray();
    }

    static (string Name, string Email) SplitSender(string sender)
    {
        var angle = SenderAngleRegex().Match(sender);
        if (angle.Success)
            return (angle.Groups[1].Value.Trim().Trim('"'), angle.Groups[2].Value.Trim());
        var email = EmailRegex().Match(sender);
        return email.Success ? (sender == email.Value ? "" : sender.Replace(email.Value, "").Trim(), email.Value) : (sender, "");
    }

    static string UnixDate(long milliseconds)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
        catch { return ""; }
    }

    static string ParseHeaderDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            ? date.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "";

    static string JoinBodies(IEnumerable<string> bodies) => string.Join("\n\n", bodies.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

    [GeneratedRegex("<\\s*(script|style)\\b[^>]*>.*?<\\s*/\\s*\\1\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();
    [GeneratedRegex("<!--[\\s\\S]*?-->")]
    private static partial Regex HtmlCommentRegex();
    [GeneratedRegex("<\\s*(br\\s*/?|/p|/div|/li|/tr|/h[1-6])\\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
    [GeneratedRegex("[\\p{Zs}\\t\\f\\v ]+")]
    private static partial Regex HorizontalWhitespaceRegex();
    [GeneratedRegex(" *\\r?\\n *")]
    private static partial Regex NewlineWhitespaceRegex();
    [GeneratedRegex("\\n{3,}")]
    private static partial Regex ExcessNewlinesRegex();
    [GeneratedRegex("charset\\s*=\\s*([^;\\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetRegex();
    [GeneratedRegex("=\\?([^?]+)\\?([bBqQ])\\?([^?]+)\\?=")]
    private static partial Regex EncodedWordRegex();
    [GeneratedRegex("^\\s*(.*?)\\s*<\\s*([^<>]+@[^<>]+)\\s*>\\s*$")]
    private static partial Regex SenderAngleRegex();
    [GeneratedRegex("[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9.-]+")]
    private static partial Regex EmailRegex();
}
