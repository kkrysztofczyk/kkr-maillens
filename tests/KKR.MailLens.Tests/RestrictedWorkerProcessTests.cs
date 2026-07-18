using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class RestrictedWorkerProcessTests
{
    [TestMethod]
    public void CreateRestrictedToken_ProducesRestrictedWindowsToken()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Test wymaga Windows.");

        Assert.IsTrue(RestrictedWorkerProcess.CanCreateRestrictedToken());
    }

    [TestMethod]
    public void Start_AttachesRestrictedProcessBeforeExecution()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Test wymaga Windows.");

        string command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var worker = RestrictedWorkerProcess.Start(command, "/d /c exit 0", 128L * 1024 * 1024);

        Assert.IsTrue(worker.Process.WaitForExit(10_000));
        Assert.AreEqual(0, worker.Process.ExitCode);
    }
}
