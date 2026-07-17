namespace KKR.MailLens;

/// <summary>
/// Model sesji to WYLACZNIE RAM (GUI trzyma klucz w [[RamSession]], CLI bierze go po named-pipe).
/// Klucz NIGDY nie dotyka dysku. Ta klasa zostala tylko po to, by skasowac ewentualny stary
/// `session.key` (DPAPI) z poprzednich buildow - defensywne sprzatanie, wolane przy init/unlock/lock/start.
/// Dawne Unlock/TryGetKey/Status (klucz na dysku) usuniete - nie chcemy ich reintrodukowac.
/// </summary>
static class Session
{
    /// <summary>Kasuje legacy session.key jesli zostal po starszej wersji (klucz-na-dysku).</summary>
    public static void Lock() { try { if (File.Exists(Paths.SessionKey)) File.Delete(Paths.SessionKey); } catch { } }
}
