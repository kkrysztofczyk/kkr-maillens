using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KKR.MailLens;

sealed class RestrictedWorkerProcess : IDisposable
{
    const uint DisableMaxPrivilege = 0x00000001;
    const uint TokenAssignPrimary = 0x0001;
    const uint TokenDuplicate = 0x0002;
    const uint TokenQuery = 0x0008;
    const uint CreateSuspended = 0x00000004;
    const uint CreateNoWindow = 0x08000000;
    const uint StartfUseStdHandles = 0x00000100;
    const int SecurityImpersonation = 2;
    const int TokenPrimary = 1;
    const int StdInputHandle = -10;
    const int StdOutputHandle = -11;
    const int StdErrorHandle = -12;

    readonly WorkerProcessLimit _limit;
    readonly EventWaitHandle _stopSignal;
    public Process Process { get; }

    RestrictedWorkerProcess(Process process, WorkerProcessLimit limit, EventWaitHandle stopSignal)
    { Process = process; _limit = limit; _stopSignal = stopSignal; }

    /// <summary>Prosi Workera o łagodne zakończenie (Abandon bieżącego zadania). Zamknięcie job
    /// object w Dispose pozostaje awaryjnym domknięciem, gdy Worker nie zdąży w okresie łaski.</summary>
    public void RequestStop() => _stopSignal.Set();

    public static RestrictedWorkerProcess Start(string executable, string arguments, long memoryBytes)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Ograniczony Worker wymaga Windows.");
        string application = Path.GetFullPath(executable);
        if (!File.Exists(application)) throw new FileNotFoundException("Brak pliku Workera.", application);
        if (memoryBytes <= 0) throw new ArgumentOutOfRangeException(nameof(memoryBytes));

        using SafeAccessTokenHandle token = OpenCurrentToken();
        using SafeAccessTokenHandle restricted = CreateRestricted(token);
        var startup = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>() };
        IntPtr stdin = GetStdHandle(StdInputHandle);
        IntPtr stdout = GetStdHandle(StdOutputHandle);
        IntPtr stderr = GetStdHandle(StdErrorHandle);
        bool inheritHandles = ValidHandle(stdin) && ValidHandle(stdout) && ValidHandle(stderr);
        if (inheritHandles)
        {
            startup.Flags = StartfUseStdHandles;
            startup.StdInput = stdin;
            startup.StdOutput = stdout;
            startup.StdError = stderr;
        }

        var commandLine = new StringBuilder($"\"{application}\"");
        if (!string.IsNullOrWhiteSpace(arguments)) commandLine.Append(' ').Append(arguments);
        if (!CreateProcessAsUser(restricted, application, commandLine, IntPtr.Zero, IntPtr.Zero,
            inheritHandles, CreateSuspended | CreateNoWindow, IntPtr.Zero,
            Path.GetDirectoryName(application), ref startup, out ProcessInformation information))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Nie udało się uruchomić Workera z ograniczonym tokenem.");

        Process? process = null;
        WorkerProcessLimit? limit = null;
        EventWaitHandle? stopSignal = null;
        bool transferred = false;
        try
        {
            process = Process.GetProcessById((int)information.ProcessId);
            _ = process.Handle;
            limit = WorkerProcessLimit.TryAttach(process, memoryBytes, out string? limitError)
                ?? throw new Win32Exception(limitError ?? "Nie ustawiono ograniczeń procesu Workera.");
            stopSignal = WorkerStopSignal.CreateForChild((int)information.ProcessId);
            if (ResumeThread(information.Thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Nie wznowiono procesu Workera.");
            var result = new RestrictedWorkerProcess(process, limit, stopSignal);
            transferred = true;
            process = null;
            limit = null;
            stopSignal = null;
            return result;
        }
        finally
        {
            stopSignal?.Dispose();
            limit?.Dispose();
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
            else if (!transferred) TerminateProcess(information.Process, 1);
            CloseHandle(information.Thread);
            CloseHandle(information.Process);
        }
    }

    public static bool IsCurrentProcessRestricted()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using SafeAccessTokenHandle token = OpenCurrentToken();
        return IsTokenRestricted(token);
    }

    internal static bool CanCreateRestrictedToken()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using SafeAccessTokenHandle token = OpenCurrentToken();
        using SafeAccessTokenHandle restricted = CreateRestricted(token);
        return IsTokenRestricted(restricted);
    }

    static SafeAccessTokenHandle OpenCurrentToken()
    {
        const uint access = TokenAssignPrimary | TokenDuplicate | TokenQuery;
        if (!OpenProcessToken(GetCurrentProcess(), access, out IntPtr tokenHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Nie odczytano tokenu procesu.");
        return new SafeAccessTokenHandle(tokenHandle);
    }

    static SafeAccessTokenHandle CreateRestricted(SafeAccessTokenHandle token)
    {
        if (IsTokenRestricted(token)) return DuplicatePrimaryToken(token);

        SecurityIdentifier[] restricting = RestrictingSids(token);
        var allocated = new List<IntPtr>(restricting.Length);
        try
        {
            var sids = new SidAndAttributes[restricting.Length];
            for (int i = 0; i < restricting.Length; i++)
            {
                byte[] binary = new byte[restricting[i].BinaryLength];
                restricting[i].GetBinaryForm(binary, 0);
                IntPtr native = Marshal.AllocHGlobal(binary.Length);
                allocated.Add(native);
                Marshal.Copy(binary, 0, native, binary.Length);
                sids[i] = new SidAndAttributes { Sid = native, Attributes = 0 };
            }

            if (!CreateRestrictedToken(token.DangerousGetHandle(), DisableMaxPrivilege, 0, IntPtr.Zero,
                0, IntPtr.Zero, (uint)sids.Length, sids, out IntPtr restrictedHandle))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"Nie utworzono ograniczonego tokenu Workera (Win32 {error}).");
            }
            return new SafeAccessTokenHandle(restrictedHandle);
        }
        finally
        {
            foreach (IntPtr native in allocated) Marshal.FreeHGlobal(native);
        }
    }

    /// <summary>
    /// Restricting SID-y tokenu Workera. Kazdy access check przechodzi DWUKROTNIE - raz przeciw SID-om
    /// uzytkownika, raz przeciw tej liscie - i wymaga zgody OBU. Lista musi wiec pokrywac KAZDA sciezke
    /// dostepu potrzebna przy starcie i pracy procesu; pominiecie dowolnej konczy sie 0xC0000022
    /// ACCESS_DENIED juz przy inicjalizacji procesu potomnego (a nie przy pierwszym uzyciu zasobu):
    ///   - SID uzytkownika  -> %LOCALAPPDATA% (corpus.db, blobs, oauth-tokens) i DPAPI CurrentUser,
    ///   - logon SID        -> DACL window station i pulpitu,
    ///   - BUILTIN\Users    -> pliki systemowe (System32, runtime .NET) - DACL przyznaje prawa grupie,
    ///                         nie SID-owi uzytkownika,
    ///   - Everyone         -> zasoby ladowania obrazu procesu przyznane przez World SID,
    ///   - Authenticated Users -> stos sieciowy i magazyn kluczy kryptograficznych (HTTPS do Gmail API).
    /// Zestaw ustalony empirycznie: {user, logon, Users} dawalo ACCESS_DENIED przy starcie; po dolozeniu
    /// Everyone proces startowal, ale kazde pobranie zalacznika konczylo sie HttpRequestException - dopiero
    /// Authenticated Users odblokowuje HTTPS. Oba SID-y nie oslabiaja izolacji w istotny sposob: obejmuja
    /// zasoby dostepne kazdemu zalogowanemu uzytkownikowi. Zawezenie polega na tym, ze odpada dostep
    /// przyznany wylacznie przez
    /// INNE grupy, przede wszystkim Administrators. Razem z DISABLE_MAX_PRIVILEGE (zdjecie przywilejow)
    /// i Job Objectem (limit pamieci, ograniczenia UI) daje to sensowna izolacje procesu, ktory parsuje
    /// wrogie dokumenty - zalaczniki, w tym ze spamu.
    /// </summary>
    static SecurityIdentifier[] RestrictingSids(SafeAccessTokenHandle token)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var sids = new List<SecurityIdentifier>(4);
        void Add(SecurityIdentifier? sid) { if (sid is not null && !sids.Contains(sid)) sids.Add(sid); }

        Add(identity.User);
        foreach (SecurityIdentifier logon in LogonSids(token)) Add(logon);
        Add(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
        Add(new SecurityIdentifier(WellKnownSidType.WorldSid, null));
        Add(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));

        if (sids.Count == 0)
            throw new InvalidOperationException("Nie ustalono SID-ow ograniczajacych dla tokenu Workera.");
        return sids.ToArray();
    }

    /// <summary>Logon SID (S-1-5-5-X-Y) z TOKEN_GROUPS po atrybucie SE_GROUP_LOGON_ID.
    /// WindowsIdentity.Groups go NIE zwraca - dlatego czytamy token bezposrednio.</summary>
    static List<SecurityIdentifier> LogonSids(SafeAccessTokenHandle token)
    {
        const int TokenGroups = 2;
        const uint SeGroupLogonId = 0xC0000000;
        var found = new List<SecurityIdentifier>(1);

        GetTokenInformation(token, TokenGroups, IntPtr.Zero, 0, out uint needed);
        if (needed == 0) return found;
        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, TokenGroups, buffer, needed, out _)) return found;
            int count = Marshal.ReadInt32(buffer);
            int stride = Marshal.SizeOf<SidAndAttributes>();
            for (int i = 0; i < count; i++)
            {
                IntPtr entry = buffer + IntPtr.Size + i * stride; // GroupCount + wyrownanie tablicy
                IntPtr sid = Marshal.ReadIntPtr(entry);
                uint attributes = (uint)Marshal.ReadInt32(entry + IntPtr.Size);
                if ((attributes & SeGroupLogonId) == SeGroupLogonId && sid != IntPtr.Zero)
                    found.Add(new SecurityIdentifier(sid));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return found;
    }

    static SafeAccessTokenHandle DuplicatePrimaryToken(SafeAccessTokenHandle token)
    {
        if (!DuplicateTokenEx(token, 0, IntPtr.Zero, SecurityImpersonation, TokenPrimary,
            out SafeAccessTokenHandle duplicate))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Nie zduplikowano ograniczonego tokenu Workera.");
        return duplicate;
    }

    static bool ValidHandle(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1);

    public void Dispose()
    {
        _stopSignal.Dispose();
        _limit.Dispose();
        Process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2Length;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool OpenProcessToken(IntPtr process, uint desiredAccess,
        out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DuplicateTokenEx(SafeAccessTokenHandle existingToken, uint desiredAccess,
        IntPtr tokenAttributes, int impersonationLevel, int tokenType,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateRestrictedToken(IntPtr existingToken, uint flags,
        uint disableSidCount, IntPtr sidsToDisable, uint deletePrivilegeCount,
        IntPtr privilegesToDelete, uint restrictedSidCount,
        [In] SidAndAttributes[] sidsToRestrict,
        out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsTokenRestricted(SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetTokenInformation(SafeAccessTokenHandle token, int tokenInformationClass,
        IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string applicationName,
        StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
}
