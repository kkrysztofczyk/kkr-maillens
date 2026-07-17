namespace KKR.MailLens.Gui;

/// <summary>
/// Klucz sesji trzymany WYLACZNIE w RAM tego procesu (nic na dysku). Wygasniecie liczone
/// zegarem MONOTONICZNYM (Environment.TickCount64 = ms od startu systemu) - cofniecie zegara
/// sciennego go nie tyka. Zamkniecie procesu = utrata klucza = lock. Zeroizacja best-effort
/// (string zarzadzany nie da sie wyzerowac twardo w .NET - to znana granica managed memory).
/// </summary>
static class RamSession
{
    static string? _keyHex;
    static long _expiryTick;

    public static void Set(string keyHex, TimeSpan ttl)
    {
        _keyHex = keyHex;
        _expiryTick = Environment.TickCount64 + (long)ttl.TotalMilliseconds;
    }

    public static void Clear() { _keyHex = null; _expiryTick = 0; }

    /// <summary>Klucz albo null (brak/wygasl). Auto-czysci po wygasnieciu.</summary>
    public static string? Key
    {
        get
        {
            if (_keyHex != null && Environment.TickCount64 >= _expiryTick) Clear();
            return _keyHex;
        }
    }

    public static bool Unlocked => Key != null;

    public static TimeSpan Remaining =>
        Unlocked ? TimeSpan.FromMilliseconds(_expiryTick - Environment.TickCount64) : TimeSpan.Zero;
}
