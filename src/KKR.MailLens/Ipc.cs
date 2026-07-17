using System.IO.Pipes;

namespace KKR.MailLens;

/// <summary>
/// Lokalny kanal miedzy CLI a agentem (GUI). Named pipe per-user. GUI trzyma klucz w RAM i na
/// zadanie GETKEY oddaje go klientowi tego samego uzytkownika; CLI dzieki temu korzysta z sesji
/// bez ponownego PINu. Bez dzialajacego/odblokowanego GUI klienci dostaja LOCKED/null.
/// </summary>
static class Ipc
{
    public static string PipeName => $"kkr-maillens.{Environment.UserName}";

    /// <summary>Wysyla jedno-liniowe zadanie do agenta i zwraca odpowiedz (null gdy brak agenta).</summary>
    public static string? Request(string verb, int timeoutMs = 500)
    {
        try
        {
            // CurrentUserOnly: klient weryfikuje, ze serwer pipe nalezy do TEGO SAMEGO uzytkownika -
            // broni przed name-squattingiem (obcy proces podszywajacy sie pod agenta gdy GUI nie dziala).
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            c.Connect(timeoutMs);
            var w = new StreamWriter(c) { AutoFlush = true };
            var r = new StreamReader(c);
            w.WriteLine(verb);
            return r.ReadLine();
        }
        catch { return null; }
    }
}
