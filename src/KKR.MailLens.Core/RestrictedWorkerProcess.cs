using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    const int SecurityMaxSidSize = 68;
    const int WinBuiltinUsersSid = 27;
    const int SecurityImpersonation = 2;
    const int TokenPrimary = 1;
    const int StdInputHandle = -10;
    const int StdOutputHandle = -11;
    const int StdErrorHandle = -12;

    readonly WorkerProcessLimit _limit;
    public Process Process { get; }

    RestrictedWorkerProcess(Process process, WorkerProcessLimit limit)
    { Process = process; _limit = limit; }

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
        bool transferred = false;
        try
        {
            process = Process.GetProcessById((int)information.ProcessId);
            _ = process.Handle;
            limit = WorkerProcessLimit.TryAttach(process, memoryBytes, out string? limitError)
                ?? throw new Win32Exception(limitError ?? "Nie ustawiono ograniczeń procesu Workera.");
            if (ResumeThread(information.Thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Nie wznowiono procesu Workera.");
            var result = new RestrictedWorkerProcess(process, limit);
            transferred = true;
            process = null;
            limit = null;
            return result;
        }
        finally
        {
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

        IntPtr usersSid = IntPtr.Zero;
        try
        {
            usersSid = Marshal.AllocHGlobal(SecurityMaxSidSize);
            uint sidSize = SecurityMaxSidSize;
            if (!CreateWellKnownSid(WinBuiltinUsersSid, IntPtr.Zero, usersSid, ref sidSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Nie utworzono identyfikatora ograniczającego Workera.");

            var restrictingSids = new[] { new SidAndAttributes
            {
                Sid = usersSid,
                Attributes = 0
            } };

            if (!CreateRestrictedToken(token.DangerousGetHandle(), DisableMaxPrivilege, 0, IntPtr.Zero,
                0, IntPtr.Zero, 1, restrictingSids, out IntPtr restrictedHandle))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"Nie utworzono ograniczonego tokenu Workera (Win32 {error}).");
            }
            return new SafeAccessTokenHandle(restrictedHandle);
        }
        finally
        {
            if (usersSid != IntPtr.Zero) Marshal.FreeHGlobal(usersSid);
        }
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
    static extern bool CreateWellKnownSid(int wellKnownSidType, IntPtr domainSid,
        IntPtr sid, ref uint sidSize);

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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string applicationName,
        StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
}
