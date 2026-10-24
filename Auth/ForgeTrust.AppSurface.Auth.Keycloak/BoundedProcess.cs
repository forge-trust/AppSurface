using System.Diagnostics;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Observed bounded exit without exposing raw process output as a public diagnostic.</summary>
internal sealed record ProcessObservation(int ExitCode, string Output, string Error, TimeSpan Elapsed);

/// <summary>Identifies the bounded execution stage that stopped a private process.</summary>
internal enum ProcessStopReason { Cancelled, Deadline, OutputLimit, Start, Cleanup }

/// <summary>Provides a safe process failure without preserving an unsafe nested exception.</summary>
internal sealed class BoundedProcessException(ProcessStopReason reason) : Exception("The bounded process stopped: " + reason)
{
    internal ProcessStopReason Reason { get; } = reason;
}

/// <summary>Runs a single framework-owned argument vector with bounded output and no retry.</summary>
internal static class BoundedProcess
{
    internal static async Task<ProcessObservation> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan deadline,
        int maximumOutputBytes,
        IReadOnlyCollection<string> secrets,
        CancellationToken cancellationToken,
        InvocationLimits? limits = null)
    {
        limits ??= new InvocationLimits(Deadline: deadline, MaxProcessOutputBytes: maximumOutputBytes);
        var effectiveDeadline = limits.EffectiveDeadline < deadline ? limits.EffectiveDeadline : deadline;
        cancellationToken.ThrowIfCancellationRequested();
        if (effectiveDeadline <= TimeSpan.Zero || effectiveDeadline > TimeSpan.FromSeconds(300) || limits.MaxProcessOutputBytes is < 1 or > 1024 * 1024 || arguments.Count > limits.MaxArgumentCount || arguments.Sum(x => (long)Encoding.UTF8.GetByteCount(x)) > limits.MaxArgumentBytes || executable.Length > limits.MaxArgumentBytes || (workingDirectory?.Length ?? 0) > limits.MaxArgumentBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timer = new CancellationTokenSource(effectiveDeadline);
        using var outputLimit = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token, outputLimit.Token);
        var clock = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) throw new BoundedProcessException(ProcessStopReason.Start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new BoundedProcessException(ProcessStopReason.Start);
        }

        long outputBytes = 0;
        async Task<string> DrainAsync(Stream stream)
        {
            using var owned = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (Interlocked.Add(ref outputBytes, read) > limits.MaxProcessOutputBytes)
                {
                    outputLimit.Cancel();
                    throw new BoundedProcessException(ProcessStopReason.OutputLimit);
                }
                await owned.WriteAsync(buffer.AsMemory(0, read), stop.Token).ConfigureAwait(false);
            }
            var text = Encoding.UTF8.GetString(owned.GetBuffer(), 0, checked((int)owned.Length));
            foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length))
            {
                text = text.Replace(secret, "<redacted>", StringComparison.Ordinal);
            }
            return text;
        }

        var stdout = DrainAsync(process.StandardOutput.BaseStream);
        var stderr = DrainAsync(process.StandardError.BaseStream);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(stop.Token), stdout, stderr).ConfigureAwait(false);
            // Cancellation wins until the observation is committed to the caller.
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessObservation(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), clock.Elapsed);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or BoundedProcessException)
        {
            var reason = cancellationToken.IsCancellationRequested ? ProcessStopReason.Cancelled
                : outputLimit.IsCancellationRequested ? ProcessStopReason.OutputLimit
                : timer.IsCancellationRequested ? ProcessStopReason.Deadline : ProcessStopReason.Cleanup;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(limits.EffectiveCleanupDeadline);
                await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupError) when (cleanupError is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                // The original stop reason is retained. The owning adapter reports uncertain external effects.
            }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(limits.EffectiveCleanupDeadline).ConfigureAwait(false); }
            catch (Exception drainError) when (drainError is OperationCanceledException or IOException or BoundedProcessException or TimeoutException) { }
            throw new BoundedProcessException(reason);
        }
    }
}
