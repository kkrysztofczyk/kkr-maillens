using System.Diagnostics;

namespace KKR.MailLens;

sealed record ProcessRunResult(int ExitCode, string Output, string Error);

static class ProcessRunner
{
    public static bool IsBatchScript(string executable) =>
        Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase);

    public static ProcessStartInfo CreateStartInfo(string executable, bool redirectInput)
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (IsBatchScript(executable))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(executable);
        }
        else start.FileName = executable;
        return start;
    }

    public static Process Start(ProcessStartInfo start, string displayName) =>
        Process.Start(start) ?? throw new InvalidOperationException($"Nie udało się uruchomić {displayName}.");

    public static async Task<ProcessRunResult> RunAsync(Process process, string toolName, TimeSpan timeout,
        byte[]? standardInput, Func<CancellationToken, Task<string>> outputReader,
        Func<CancellationToken, Task<string>> errorReader, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        Task<string> outputTask = outputReader(linked.Token);
        Task<string> errorTask = errorReader(linked.Token);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(standardInput, linked.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return new ProcessRunResult(process.ExitCode,
                await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{toolName} przekroczył limit {timeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (standardInput is not null && ex is IOException or ObjectDisposedException)
        {
            TryKill(process);
            string error = await DiagnosticAsync(errorTask).ConfigureAwait(false);
            string detail = error.Length == 0 ? ex.Message : error;
            throw new InvalidOperationException(
                $"{toolName} przerwał strumień wejściowy: {DiagnosticText.Limit(detail)}", ex);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    static async Task<string> DiagnosticAsync(Task<string> errorTask)
    {
        try { return await errorTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { return ""; }
    }
}

static class DiagnosticText
{
    public static string Limit(string value)
    {
        string clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 500 ? clean : clean[..500];
    }
}
