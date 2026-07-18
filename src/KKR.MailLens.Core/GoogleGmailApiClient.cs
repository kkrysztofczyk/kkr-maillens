using System.Globalization;
using System.Net;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;

namespace KKR.MailLens;

sealed class GoogleGmailApiClient : IGmailApiClient
{
    readonly GmailService _service;
    readonly IDisposable _authorizationFlow;
    readonly IDisposable _tokenStore;

    public GoogleGmailApiClient(GmailService service, IDisposable authorizationFlow,
        IDisposable tokenStore)
    {
        _service = service;
        _authorizationFlow = authorizationFlow;
        _tokenStore = tokenStore;
    }

    public async Task<GmailProfile> GetProfileAsync(CancellationToken cancellationToken)
    {
        var profile = await Retry(() => _service.Users.GetProfile("me").ExecuteAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        return new GmailProfile(profile.EmailAddress ?? "", Number(profile.HistoryId));
    }

    public async Task<IReadOnlyList<GmailApiLabel>> GetLabelsAsync(CancellationToken cancellationToken)
    {
        var response = await Retry(() => _service.Users.Labels.List("me").ExecuteAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        return (response.Labels ?? Array.Empty<Label>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .Select(x => new GmailApiLabel(x.Id, x.Name ?? x.Id, x.Type ?? "unknown"))
            .ToArray();
    }

    public async Task<GmailMessagePage> ListMessageIdsAsync(string? pageToken, CancellationToken cancellationToken)
    {
        try
        {
            var request = _service.Users.Messages.List("me");
            request.IncludeSpamTrash = true;
            request.MaxResults = 500;
            request.PageToken = pageToken;
            var response = await Retry(() => request.ExecuteAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            string[] ids = (response.Messages ?? Array.Empty<Message>())
                .Select(x => x.Id).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            return new GmailMessagePage(ids, response.NextPageToken);
        }
        catch (GoogleApiException ex) when (pageToken != null && ex.HttpStatusCode == HttpStatusCode.BadRequest)
        {
            throw new GmailPageTokenExpiredException();
        }
    }

    public async Task<GmailApiMessage> GetMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        try
        {
            var request = _service.Users.Messages.Get("me", messageId);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
            Message message = await Retry(() => request.ExecuteAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            GmailApiPart payload = message.Payload is null
                ? new GmailApiPart()
                : await MapPart(messageId, message.Payload, cancellationToken).ConfigureAwait(false);
            return new GmailApiMessage
            {
                Id = message.Id ?? messageId,
                ThreadId = message.ThreadId ?? "",
                HistoryId = Number(message.HistoryId),
                InternalDateUnixMs = Convert.ToInt64(message.InternalDate ?? 0, CultureInfo.InvariantCulture),
                SizeEstimate = message.SizeEstimate ?? 0,
                LabelIds = (message.LabelIds ?? Array.Empty<string>()).ToArray(),
                Payload = payload,
            };
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw new GmailMessageNotFoundException(messageId);
        }
    }

    public async Task<GmailHistoryPage> ListHistoryAsync(string startHistoryId, string? pageToken, CancellationToken cancellationToken)
    {
        try
        {
            if (!ulong.TryParse(startHistoryId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong start))
                throw new GmailHistoryExpiredException();
            var request = _service.Users.History.List("me");
            request.StartHistoryId = start;
            request.MaxResults = 500;
            request.PageToken = pageToken;
            ListHistoryResponse response = await Retry(() => request.ExecuteAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

            var upsert = new HashSet<string>(StringComparer.Ordinal);
            var deleted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var history in response.History ?? Array.Empty<History>())
            {
                foreach (var item in history.MessagesAdded ?? Array.Empty<HistoryMessageAdded>()) Add(item.Message?.Id, upsert);
                foreach (var item in history.LabelsAdded ?? Array.Empty<HistoryLabelAdded>()) Add(item.Message?.Id, upsert);
                foreach (var item in history.LabelsRemoved ?? Array.Empty<HistoryLabelRemoved>()) Add(item.Message?.Id, upsert);
                foreach (var item in history.MessagesDeleted ?? Array.Empty<HistoryMessageDeleted>()) Add(item.Message?.Id, deleted);
            }
            upsert.ExceptWith(deleted);
            return new GmailHistoryPage(upsert.ToArray(), deleted.ToArray(), Number(response.HistoryId), response.NextPageToken);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw new GmailHistoryExpiredException();
        }
    }

    public async Task<byte[]> GetAttachmentBytesAsync(string messageId, string attachmentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(attachmentId))
            throw new ArgumentException("Brak identyfikatora wiadomości lub załącznika Gmail.");
        var request = _service.Users.Messages.Attachments.Get("me", messageId, attachmentId);
        MessagePartBody body = await Retry(() => request.ExecuteAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        return GmailAttachmentDownloader.DecodeBase64Url(body.Data);
    }

    async Task<GmailApiPart> MapPart(string messageId, MessagePart part, CancellationToken cancellationToken)
    {
        string? data = part.Body?.Data;
        string? attachmentId = part.Body?.AttachmentId;
        string mime = part.MimeType ?? "application/octet-stream";
        if (string.IsNullOrEmpty(data) && !string.IsNullOrEmpty(attachmentId) && mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            var request = _service.Users.Messages.Attachments.Get("me", messageId, attachmentId);
            MessagePartBody body = await Retry(() => request.ExecuteAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            data = body.Data;
        }

        var children = new List<GmailApiPart>();
        foreach (var child in part.Parts ?? Array.Empty<MessagePart>())
            children.Add(await MapPart(messageId, child, cancellationToken).ConfigureAwait(false));

        return new GmailApiPart
        {
            PartId = part.PartId ?? "",
            MimeType = mime,
            Filename = part.Filename ?? "",
            Data = data,
            AttachmentId = attachmentId,
            Size = part.Body?.Size ?? 0,
            Headers = (part.Headers ?? Array.Empty<MessagePartHeader>())
                .Select(x => new GmailHeader(x.Name ?? "", x.Value ?? "")).ToArray(),
            Parts = children,
        };
    }

    static async Task<T> Retry<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await operation().ConfigureAwait(false); }
            catch (TokenResponseException) { throw new GmailAuthorizationException(); }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Unauthorized)
            { throw new GmailAuthorizationException(); }
            catch (Exception ex) when (attempt < 5 && IsTransient(ex))
            {
                int milliseconds = Math.Min(30_000, (1 << attempt) * 1_000) + Random.Shared.Next(100, 500);
                await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException => true,
        TaskCanceledException => true,
        GoogleApiException api => api.HttpStatusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
            || api.HttpStatusCode == HttpStatusCode.Forbidden && IsRateLimit(api),
        _ => false,
    };

    static bool IsRateLimit(GoogleApiException exception) =>
        exception.Error?.Errors?.Any(error => error.Reason is "rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded" or "backendError") == true;

    static void Add(string? value, HashSet<string> target)
    { if (!string.IsNullOrWhiteSpace(value)) target.Add(value); }

    static string Number(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    public void Dispose()
    {
        try { _service.Dispose(); }
        finally
        {
            try { _authorizationFlow.Dispose(); }
            finally { _tokenStore.Dispose(); }
        }
    }
}
