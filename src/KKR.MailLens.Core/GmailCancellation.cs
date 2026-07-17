namespace KKR.MailLens;

static class GmailCancellation
{
    public static void Request()
    {
        Directory.CreateDirectory(Paths.Base);
        File.WriteAllText(Paths.GmailCancelFile, "cancel");
    }

    public static void Clear()
    {
        try { if (File.Exists(Paths.GmailCancelFile)) File.Delete(Paths.GmailCancelFile); } catch { }
    }

    public static void ThrowIfRequested(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(Paths.GmailCancelFile)) throw new OperationCanceledException("Synchronizacja anulowana.", cancellationToken);
    }
}
