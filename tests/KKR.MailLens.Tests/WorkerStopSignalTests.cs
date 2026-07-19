using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class WorkerStopSignalTests
{
    [TestMethod]
    public void StopSignal_RoundTripsBetweenLauncherAndWorkerSide()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Test wymaga Windows.");

        using EventWaitHandle launcherSide = WorkerStopSignal.CreateForChild(Environment.ProcessId);
        using EventWaitHandle? workerSide = WorkerStopSignal.TryOpenForCurrentProcess();

        Assert.IsNotNull(workerSide);
        Assert.IsFalse(workerSide.WaitOne(0));
        launcherSide.Set();
        Assert.IsTrue(workerSide.WaitOne(1_000));
    }

    [TestMethod]
    public void TryOpen_ReturnsNullWithoutLauncher()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Test wymaga Windows.");

        Assert.IsNull(WorkerStopSignal.TryOpen(int.MaxValue - 1));
    }

    [TestMethod]
    public void RequestStop_SignalsEventCreatedForRestrictedChild()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Test wymaga Windows.");

        string command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var worker = RestrictedWorkerProcess.Start(command,
            "/d /c ping -n 30 127.0.0.1 >nul", 128L * 1024 * 1024);
        using EventWaitHandle? signal = WorkerStopSignal.TryOpen(worker.Process.Id);

        Assert.IsNotNull(signal);
        Assert.IsFalse(signal.WaitOne(0));
        worker.RequestStop();
        Assert.IsTrue(signal.WaitOne(1_000));
        // Dispose zamyka job object (KILL_ON_JOB_CLOSE) i sprząta proces ping/cmd.
    }
}
