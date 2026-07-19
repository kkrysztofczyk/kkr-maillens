namespace KKR.MailLens;

/// <summary>
/// Deklarowany rozmiar załącznika bywa przybliżony (Gmail raportuje rozmiar transportowy,
/// Outlook PR_ATTACH_SIZE zawiera narzut magazynu MAPI, a SaveAsFile zapisuje zdekodowane bajty).
/// Akceptujemy odchyłkę max(4 KiB, 1%) i odrzucamy dopiero rażące niezgodności.
/// </summary>
static class AttachmentSizeTolerance
{
    const long MinimumToleranceBytes = 4 * 1024;

    public static bool IsGrossMismatch(long actualBytes, long declaredBytes)
    {
        if (declaredBytes <= 0) return false;
        long tolerance = Math.Max(MinimumToleranceBytes, declaredBytes / 100);
        return Math.Abs(actualBytes - declaredBytes) > tolerance;
    }
}
