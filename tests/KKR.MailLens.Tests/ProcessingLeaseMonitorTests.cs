using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KKR.MailLens.Tests;

[TestClass]
public sealed class ProcessingLeaseMonitorTests
{
    [TestMethod]
    public async Task Monitor_CancelsWorkWhenRenewalLosesOwnership()
    {
        await using var monitor = ProcessingLeaseMonitor.Start(() => false,
            TimeSpan.FromMilliseconds(500), heartbeatInterval: TimeSpan.FromMilliseconds(20));

        await WaitUntilAsync(() => monitor.LostLease);

        Assert.IsTrue(monitor.Token.IsCancellationRequested);
        Assert.Throws<ProcessingLeaseLostException>(monitor.AssertActive);
    }

    [TestMethod]
    public async Task Monitor_RetriesTransientRenewalErrorBeforeLeaseDeadline()
    {
        int calls = 0;
        await using var monitor = ProcessingLeaseMonitor.Start(() =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1) throw new IOException("Neutralny błąd odnowienia");
            return true;
        }, TimeSpan.FromMilliseconds(500), heartbeatInterval: TimeSpan.FromMilliseconds(20));

        await WaitUntilAsync(() => Volatile.Read(ref calls) >= 2);

        Assert.IsFalse(monitor.LostLease);
        monitor.AssertActive();
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.IsTrue(condition(), "Warunek monitora dzierżawy nie został spełniony w limicie czasu.");
    }
}
