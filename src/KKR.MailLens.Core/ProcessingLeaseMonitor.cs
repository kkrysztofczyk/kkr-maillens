namespace KKR.MailLens;

sealed class ProcessingLeaseLostException(string message, Exception? innerException = null)
    : OperationCanceledException(message, innerException);

/// <summary>Odnawia dzierżawę zadania poza połączeniem wykonującym właściwą pracę
/// i anuluje operację zanim niewłaściciel zapisze jej wynik.</summary>
sealed class ProcessingLeaseMonitor : IAsyncDisposable
{
    readonly Func<bool> renew;
    readonly TimeSpan leaseDuration;
    readonly TimeSpan interval;
    readonly CancellationTokenSource cancellation;
    readonly Task loop;
    Exception? renewalError;
    int lostLease;
    int stopped;

    ProcessingLeaseMonitor(Func<bool> renew, TimeSpan leaseDuration, TimeSpan interval,
        CancellationToken cancellationToken)
    {
        this.renew = renew;
        this.leaseDuration = leaseDuration;
        this.interval = interval;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loop = RunAsync();
    }

    public CancellationToken Token => cancellation.Token;
    public bool LostLease => Volatile.Read(ref lostLease) == 1;

    public static ProcessingLeaseMonitor Start(Func<bool> renew, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default, TimeSpan? heartbeatInterval = null)
    {
        ArgumentNullException.ThrowIfNull(renew);
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        TimeSpan interval = heartbeatInterval ?? TimeSpan.FromSeconds(
            Math.Clamp(leaseDuration.TotalSeconds / 3, 5, 30));
        if (interval <= TimeSpan.Zero || interval >= leaseDuration)
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        return new ProcessingLeaseMonitor(renew, leaseDuration, interval, cancellationToken);
    }

    public void AssertActive()
    {
        if (LostLease)
            throw new ProcessingLeaseLostException("Worker utracił dzierżawę zadania.", renewalError);
        Token.ThrowIfCancellationRequested();
    }

    public async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 0) cancellation.Cancel();
        try { await loop.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        cancellation.Dispose();
    }

    async Task RunAsync()
    {
        DateTimeOffset lastConfirmed = DateTimeOffset.UtcNow;
        while (true)
        {
            try { await Task.Delay(interval, Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { return; }

            try
            {
                if (!renew())
                {
                    LoseLease();
                    return;
                }
                lastConfirmed = DateTimeOffset.UtcNow;
                renewalError = null;
            }
            catch (Exception ex)
            {
                renewalError = ex;
                // Błąd blokady/IO nie dowodzi utraty własności. Ponawiamy aż do marginesu
                // przed faktycznym wygaśnięciem ostatniej potwierdzonej dzierżawy.
                if (DateTimeOffset.UtcNow - lastConfirmed >= leaseDuration - interval)
                {
                    LoseLease();
                    return;
                }
            }
        }
    }

    void LoseLease()
    {
        Interlocked.Exchange(ref lostLease, 1);
        cancellation.Cancel();
    }
}
