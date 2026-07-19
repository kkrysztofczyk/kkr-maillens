namespace KKR.MailLens;

static class GmailCancellation
{
    public static void Request(long accountId)
    {
        Directory.CreateDirectory(Paths.Base);
        File.WriteAllText(Paths.GmailCancelFile(accountId), "cancel");
    }

    public static void Clear(long accountId)
    {
        try { if (File.Exists(Paths.GmailCancelFile(accountId))) File.Delete(Paths.GmailCancelFile(accountId)); } catch { }
    }

    public static void ClearAll()
    {
        try
        {
            if (!Directory.Exists(Paths.Base)) return;
            // obejmuje takze historyczny globalny znacznik "gmail-sync.cancel"
            foreach (string file in Directory.GetFiles(Paths.Base, "gmail-sync*.cancel"))
                try { File.Delete(file); } catch { }
        }
        catch { }
    }

    public static bool IsRequested(long accountId) => File.Exists(Paths.GmailCancelFile(accountId));

    public static void ThrowIfRequested(long accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRequested(accountId)) throw new OperationCanceledException("Synchronizacja anulowana.", cancellationToken);
    }
}
