namespace KKR.MailLens;

sealed record GmailProfile(string Email, string HistoryId);

sealed record GmailApiLabel(string Id, string Name, string Type);

sealed record GmailMessagePage(IReadOnlyList<string> MessageIds, string? NextPageToken);

sealed record GmailHistoryPage(
    IReadOnlyList<string> UpsertMessageIds,
    IReadOnlyList<string> DeletedMessageIds,
    string HistoryId,
    string? NextPageToken);

sealed record GmailHeader(string Name, string Value);

sealed class GmailApiPart
{
    public string PartId { get; init; } = "";
    public string MimeType { get; init; } = "application/octet-stream";
    public string Filename { get; init; } = "";
    public string? Data { get; init; }
    public string? AttachmentId { get; init; }
    public long Size { get; init; }
    public IReadOnlyList<GmailHeader> Headers { get; init; } = Array.Empty<GmailHeader>();
    public IReadOnlyList<GmailApiPart> Parts { get; init; } = Array.Empty<GmailApiPart>();
}

sealed class GmailApiMessage
{
    public string Id { get; init; } = "";
    public string ThreadId { get; init; } = "";
    public string HistoryId { get; init; } = "";
    public long InternalDateUnixMs { get; init; }
    public long SizeEstimate { get; init; }
    public IReadOnlyList<string> LabelIds { get; init; } = Array.Empty<string>();
    public GmailApiPart Payload { get; init; } = new();
}

sealed record GmailAttachmentRecord(
    string PartId,
    string GmailAttachmentId,
    string? InlineBase64Data,
    string Filename,
    string MimeType,
    long SizeBytes,
    string ContentId,
    bool IsInline)
{
    public string ProviderKey => !string.IsNullOrWhiteSpace(GmailAttachmentId)
        ? GmailAttachmentId
        : $"part:{PartId}";
}

sealed class GmailStoredMessage
{
    public long AccountId { get; init; }
    public string GmailMessageId { get; init; } = "";
    public string GmailThreadId { get; init; } = "";
    public string RfcMessageId { get; init; } = "";
    public string InternalDate { get; init; } = "";
    public string SentAt { get; init; } = "";
    public string Sender { get; init; } = "";
    public string Recipients { get; init; } = "";
    public string Cc { get; init; } = "";
    public string Bcc { get; init; } = "";
    public string Subject { get; init; } = "";
    public string BodyText { get; init; } = "";
    public string BodyHtml { get; init; } = "";
    public bool IsUnread { get; init; }
    public bool IsTrashed { get; init; }
    public bool IsSpam { get; init; }
    public long SizeBytes { get; init; }
    public IReadOnlyList<string> LabelIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<GmailAttachmentRecord> Attachments { get; init; } = Array.Empty<GmailAttachmentRecord>();

    public string EntryId => $"gmail:{AccountId}:{GmailMessageId}";
}

sealed record GmailAccountRecord(
    long Id,
    string Email,
    string DisplayName,
    string TokenKey,
    string? LastHistoryId,
    string? InitialPageToken,
    bool InitialSyncCompleted,
    string? LastSyncAt,
    long SyncGeneration,
    int LastErrorCount,
    string? CurrentOperation,
    string? OperationStartedAt);

sealed record GmailSyncProgress(string Phase, long Processed, long Errors, string? Detail = null);

sealed record GmailSyncResult(long Processed, long Inserted, long Updated, long Deleted, long Errors, bool WasFullSync);

sealed record GmailSaveBatchResult(
    long Inserted,
    long Updated,
    IReadOnlyList<GmailStoredMessage> Saved,
    IReadOnlyList<string> FailedMessageIds);

interface IGmailApiClient : IDisposable
{
    Task<GmailProfile> GetProfileAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<GmailApiLabel>> GetLabelsAsync(CancellationToken cancellationToken);
    Task<GmailMessagePage> ListMessageIdsAsync(string? pageToken, CancellationToken cancellationToken);
    Task<GmailApiMessage> GetMessageAsync(string messageId, CancellationToken cancellationToken);
    Task<GmailHistoryPage> ListHistoryAsync(string startHistoryId, string? pageToken, CancellationToken cancellationToken);
}

sealed class GmailHistoryExpiredException : Exception
{
    public GmailHistoryExpiredException() : base("Zapisany stan synchronizacji Gmail wygasl.") { }
}

sealed class GmailMessageNotFoundException : Exception
{
    public GmailMessageNotFoundException(string messageId) : base($"Wiadomosc Gmail nie istnieje: {messageId}") { }
}

sealed class GmailPageTokenExpiredException : Exception
{
    public GmailPageTokenExpiredException() : base("Checkpoint pelnej synchronizacji Gmail wygasl.") { }
}

sealed class GmailAuthorizationException : Exception
{
    public GmailAuthorizationException() : base("Autoryzacja Gmail wygasla lub zostala cofnieta. Polacz konto ponownie.") { }
}
