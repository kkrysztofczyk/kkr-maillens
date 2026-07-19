using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace KKR.MailLens;

static partial class GmailMessageMapper
{
    // Limit glebokosci zagniezdzenia czesci MIME chroniacy stos przed wrogimi wiadomosciami.
    internal const int MaxMimePartDepth = 100;
    const int MaxHtmlLength = 2_000_000;
    const string DepthTruncationNotice = "[Pominięto zbyt głęboko zagnieżdżone części wiadomości.]";

    static GmailMessageMapper() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static GmailStoredMessage Map(GmailApiMessage source, long accountId)
    {
        if (string.IsNullOrWhiteSpace(source.Id)) throw new FormatException("Brak identyfikatora wiadomosci Gmail.");

        var plain = new List<string>();
        var html = new List<string>();
        var attachments = new List<GmailAttachmentRecord>();
        var decodeErrors = new List<string>();
        bool depthExceeded = Visit(source.Payload, plain, html, attachments, decodeErrors, depth: 0);

        string bodyHtml = JoinBodies(html);
        string bodyText = JoinBodies(plain);
        if (bodyText.Length == 0 && bodyHtml.Length > 0) bodyText = HtmlToText(bodyHtml);
        if (depthExceeded) bodyText = JoinBodies([bodyText, DepthTruncationNotice]);

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
            BodyDecodeErrors = decodeErrors,
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
            SourceIdentity = message.EntryId,
            LegacyEntryId = message.EntryId,
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
        => Base64Url.Decode(value, "Nieprawidłowa treść Base64URL wiadomości Gmail.");

    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        if (html.Length > MaxHtmlLength) html = TextLimit.Take(html, MaxHtmlLength);
        string text = StripMarkup(html);
        text = WebUtility.HtmlDecode(text);
        try
        {
            text = HorizontalWhitespaceRegex().Replace(text, " ");
            text = NewlineWhitespaceRegex().Replace(text, "\n");
            text = ExcessNewlinesRegex().Replace(text, "\n\n");
        }
        catch (RegexMatchTimeoutException) { }
        return text.Trim();
    }

    // Tolerancyjny skaner znacznikow zamiast regexow: dziala liniowo na wrogim HTML,
    // usuwa bloki script/style/komentarze takze bez domkniecia (do konca wejscia)
    // i respektuje '>' wewnatrz cytowanych wartosci atrybutow.
    static string StripMarkup(string html)
    {
        var text = new StringBuilder(html.Length);
        int index = 0;
        while (index < html.Length)
        {
            char current = html[index];
            if (current != '<') { text.Append(current); index++; continue; }

            if (index + 3 < html.Length && html[index + 1] == '!' && html[index + 2] == '-' && html[index + 3] == '-')
            {
                int end = html.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = end < 0 ? html.Length : end + 3;
                text.Append(' ');
                continue;
            }

            char marker = index + 1 < html.Length ? html[index + 1] : '\0';
            if (!char.IsAsciiLetter(marker) && marker != '/' && marker != '!' && marker != '?')
            { text.Append('<'); index++; continue; }

            bool closing = marker == '/';
            int nameStart = closing ? index + 2 : index + 1;
            int nameEnd = nameStart;
            while (nameEnd < html.Length && char.IsAsciiLetterOrDigit(html[nameEnd])) nameEnd++;
            ReadOnlySpan<char> name = html.AsSpan(nameStart, nameEnd - nameStart);

            text.Append(IsBreakTag(name, closing) ? '\n' : ' ');
            int close = FindTagClose(html, nameEnd);
            if (close < 0) break;

            index = close + 1;
            bool selfClosing = close - 1 >= nameEnd && html[close - 1] == '/';
            if (!closing && !selfClosing && IsRawTextElement(name))
                index = SkipRawTextElement(html, index, name);
        }
        return text.ToString();
    }

    static bool IsRawTextElement(ReadOnlySpan<char> name)
        => name.Equals("script", StringComparison.OrdinalIgnoreCase)
        || name.Equals("style", StringComparison.OrdinalIgnoreCase);

    static bool IsBreakTag(ReadOnlySpan<char> name, bool closing)
    {
        if (!closing) return name.Equals("br", StringComparison.OrdinalIgnoreCase);
        if (name.Equals("p", StringComparison.OrdinalIgnoreCase)
            || name.Equals("div", StringComparison.OrdinalIgnoreCase)
            || name.Equals("li", StringComparison.OrdinalIgnoreCase)
            || name.Equals("tr", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Length == 2 && (name[0] == 'h' || name[0] == 'H') && name[1] is >= '1' and <= '6';
    }

    static int FindTagClose(string html, int start)
    {
        char quote = '\0';
        for (int i = start; i < html.Length; i++)
        {
            char c = html[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c == '"' || c == '\'') quote = c;
            else if (c == '>') return i;
        }
        return -1;
    }

    static int SkipRawTextElement(string html, int start, ReadOnlySpan<char> name)
    {
        int search = start;
        while (search < html.Length)
        {
            int open = html.IndexOf("</", search, StringComparison.Ordinal);
            if (open < 0) break;
            int afterName = open + 2 + name.Length;
            if (afterName <= html.Length
                && html.AsSpan(open + 2, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase)
                && (afterName == html.Length || html[afterName] == '>' || html[afterName] == '/' || char.IsWhiteSpace(html[afterName])))
            {
                int close = FindTagClose(html, afterName);
                return close < 0 ? html.Length : close + 1;
            }
            search = open + 2;
        }
        return html.Length;
    }

    static bool Visit(GmailApiPart part, List<string> plain, List<string> html, List<GmailAttachmentRecord> attachments,
        List<string> decodeErrors, int depth)
    {
        string mime = (part.MimeType ?? "application/octet-stream").Trim().ToLowerInvariant();
        bool namedAttachment = !string.IsNullOrWhiteSpace(part.Filename);
        bool binaryAttachment = !string.IsNullOrWhiteSpace(part.AttachmentId) && !mime.StartsWith("text/", StringComparison.Ordinal);

        if (namedAttachment || binaryAttachment)
        {
            attachments.Add(new GmailAttachmentRecord(
                part.PartId ?? "",
                part.AttachmentId ?? "",
                part.Data,
                DecodeHeader(part.Filename ?? ""),
                mime,
                Math.Max(0, part.Size),
                Header(part, "Content-ID").Trim().Trim('<', '>'),
                Header(part, "Content-Disposition").Contains("inline", StringComparison.OrdinalIgnoreCase)));
        }

        if (!namedAttachment && mime == "text/plain" && !string.IsNullOrEmpty(part.Data))
        {
            if (DecodePartText(part) is { } text) { if (text.Length > 0) plain.Add(text); }
            else decodeErrors.Add(part.PartId ?? "");
        }
        else if (!namedAttachment && mime == "text/html" && !string.IsNullOrEmpty(part.Data))
        {
            if (DecodePartText(part) is { } text) { if (text.Length > 0) html.Add(text); }
            else decodeErrors.Add(part.PartId ?? "");
        }

        if (depth >= MaxMimePartDepth) return part.Parts.Count > 0;
        bool truncated = false;
        foreach (var child in part.Parts) truncated |= Visit(child, plain, html, attachments, decodeErrors, depth + 1);
        return truncated;
    }

    // null = treść nie do odzyskania (np. uszkodzone Base64URL); wywołujący raportuje utratę.
    static string? DecodePartText(GmailApiPart part)
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
        catch { return null; }
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
        try
        {
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
        catch (RegexMatchTimeoutException) { return value.Trim(); }
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
        try
        {
            var angle = SenderAngleRegex().Match(sender);
            if (angle.Success)
                return (angle.Groups[1].Value.Trim().Trim('"'), angle.Groups[2].Value.Trim());
            var email = EmailRegex().Match(sender);
            return email.Success ? (sender == email.Value ? "" : sender.Replace(email.Value, "").Trim(), email.Value) : (sender, "");
        }
        catch (RegexMatchTimeoutException) { return (sender, ""); }
    }

    static string UnixDate(long milliseconds)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
        catch { return ""; }
    }

    // AssumeUniversal: nagłówek Date bez strefy nie może zależeć od strefy hosta.
    static string ParseHeaderDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var date)
            ? date.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "";

    static string JoinBodies(IEnumerable<string> bodies) => string.Join("\n\n", bodies.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

    [GeneratedRegex("[\\p{Zs}\\t\\f\\v ]+", RegexOptions.None, 1_000)]
    private static partial Regex HorizontalWhitespaceRegex();
    [GeneratedRegex(" *\\r?\\n *", RegexOptions.None, 1_000)]
    private static partial Regex NewlineWhitespaceRegex();
    [GeneratedRegex("\\n{3,}", RegexOptions.None, 1_000)]
    private static partial Regex ExcessNewlinesRegex();
    [GeneratedRegex("charset\\s*=\\s*([^;\\s]+)", RegexOptions.IgnoreCase, 1_000)]
    private static partial Regex CharsetRegex();
    [GeneratedRegex("=\\?([^?]+)\\?([bBqQ])\\?([^?]+)\\?=", RegexOptions.None, 1_000)]
    private static partial Regex EncodedWordRegex();
    [GeneratedRegex("^\\s*(.*?)\\s*<\\s*([^<>]+@[^<>]+)\\s*>\\s*$", RegexOptions.None, 1_000)]
    private static partial Regex SenderAngleRegex();
    [GeneratedRegex("[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9.-]+", RegexOptions.None, 1_000)]
    private static partial Regex EmailRegex();
}
