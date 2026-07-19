using System.Security.AccessControl;
using System.Security.Principal;

namespace KKR.MailLens;

/// <summary>
/// Nazwane zdarzenie "zatrzymaj się" między launcherem (GUI/CLI) a Workerem. Launcher tworzy je
/// przed wznowieniem procesu i ustawia zamiast zabijać job object; Worker po odebraniu sygnału
/// oddaje zadanie (Abandon) i kończy się z własnej woli. Zabicie drzewa przez job object zostaje
/// wyłącznie awaryjnym domknięciem po upływie okresu łaski.
/// </summary>
static class WorkerStopSignal
{
    // Przestrzeń nazw sesyjna (bez "Global\") — zdarzenie widzą tylko procesy tej samej sesji.
    static string Name(int processId) => $"kkr-maillens.stop.{processId}";

    /// <summary>Tworzy zdarzenie dla procesu potomnego. ACL musi jawnie dopuszczać BUILTIN\Users,
    /// bo restricted token Workera przechodzi kontrolę dostępu wyłącznie przez ten SID.</summary>
    public static EventWaitHandle CreateForChild(int processId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Sygnał zatrzymania Workera wymaga Windows.");
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User!,
            EventWaitHandleRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(initialState: false, EventResetMode.ManualReset,
            Name(processId), out _, security);
    }

    /// <summary>Otwiera zdarzenie po stronie Workera; null gdy launcher go nie utworzył
    /// (np. Worker uruchomiony ręcznie) — wtedy pozostaje Ctrl+C i monitor sesji.</summary>
    public static EventWaitHandle? TryOpenForCurrentProcess() => TryOpen(Environment.ProcessId);

    public static EventWaitHandle? TryOpen(int processId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return EventWaitHandle.TryOpenExisting(Name(processId), out EventWaitHandle? handle)
            ? handle : null;
    }
}
