using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using KKR.MailLens;

namespace KKR.MailLens.Gui;

/// <summary>
/// Serwer named-pipe w GUI. Wystawia klucz z RAM (RamSession) klientom TEGO SAMEGO uzytkownika
/// (ACL ograniczone do biezacego SID). Dzieki temu CLI korzysta z sesji trzymanej przez GUI.
/// Klucz nadal nie dotyka dysku - podroz tylko RAM GUI -> pipe -> proces CLI.
/// </summary>
sealed class Agent : IDisposable
{
    volatile bool _stop;
    Thread? _thread;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "kkr-maillens-agent" };
        _thread.Start();
    }

    void Loop()
    {
        while (!_stop)
        {
            NamedPipeServerStream? s = null;
            try
            {
                var id = WindowsIdentity.GetCurrent().User!;
                var sec = new PipeSecurity();
                sec.AddAccessRule(new PipeAccessRule(id, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
                s = NamedPipeServerStreamAcl.Create(Ipc.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, sec);
                s.WaitForConnection();
                if (_stop) break;

                var r = new StreamReader(s);
                var w = new StreamWriter(s) { AutoFlush = true };
                string? verb = r.ReadLine();
                w.WriteLine(Handle(verb));
                try { s.WaitForPipeDrain(); } catch { }
            }
            catch
            {
                // np. nazwa zajeta przez inny egzemplarz GUI - nie kreci sie w kolko
                Thread.Sleep(1000);
            }
            finally { try { s?.Dispose(); } catch { } }
        }
    }

    static string Handle(string? verb) => verb switch
    {
        "GETKEY" => RamSession.Key ?? "LOCKED",
        "STATUS" => RamSession.Unlocked
            ? $"UNLOCKED {(int)RamSession.Remaining.TotalSeconds} {Mode.Read()}"
            : $"LOCKED {Mode.Read()}",
        "LOCK" => Lock(),
        _ => "ERR",
    };

    static string Lock() { RamSession.Clear(); return "OK"; }

    public void Dispose()
    {
        _stop = true;
        try { using var c = new NamedPipeClientStream(".", Ipc.PipeName, PipeDirection.InOut); c.Connect(200); } catch { }
    }
}
