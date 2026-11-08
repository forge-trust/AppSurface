using System.Diagnostics;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>Observed bounded exit without exposing raw process output as a public diagnostic.</summary>
internal sealed record ProcessObservation(int ExitCode, string Output, string Error, TimeSpan Elapsed);
/// <summary>Identifies the bounded execution stage that stopped a private process.</summary>
internal enum ProcessStopReason
{
    Cancelled,
    Deadline,
    OutputLimit,
    Start,
    Cleanup
}

/// <summary>Provides a safe process failure without preserving an unsafe nested exception.</summary>
internal sealed class BoundedProcessException(ProcessStopReason reason) : Exception("The bounded process stopped: " + reason)
{
    internal ProcessStopReason Reason { get; } = reason;
}

/// <summary>Runs a single framework-owned argument vector with bounded output and no retry.</summary>
internal static class BoundedProcess
{
    internal static async Task<ProcessObservation> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan deadline, int maximumOutputBytes, IReadOnlyCollection<string> secrets, CancellationToken cancellationToken, InvocationLimits? limits = null)
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
        if (workingDirectory is not null)
            start.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process
        {
            StartInfo = start
        };
        using var timer = new CancellationTokenSource(effectiveDeadline);
        using var outputLimit = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token, outputLimit.Token);
        var clock = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
                throw new BoundedProcessException(ProcessStopReason.Start);
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
                if (read == 0)
                    break;
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
            var reason = cancellationToken.IsCancellationRequested ? ProcessStopReason.Cancelled : outputLimit.IsCancellationRequested ? ProcessStopReason.OutputLimit : timer.IsCancellationRequested ? ProcessStopReason.Deadline : ProcessStopReason.Cleanup;
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(limits.EffectiveCleanupDeadline);
                await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupError) when (cleanupError is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                // The original stop reason is retained. The owning adapter reports uncertain external effects.
            }

            try
            {
                await Task.WhenAll(stdout, stderr).WaitAsync(limits.EffectiveCleanupDeadline).ConfigureAwait(false);
            }
            catch (Exception drainError) when (drainError is OperationCanceledException or IOException or InvalidDataException or BoundedProcessException or TimeoutException)
            {
            }

            throw new BoundedProcessException(reason);
        }
    }

    internal static async Task RunToFileAsync(string executable, IReadOnlyList<string> arguments, string absentOutput, long maxBytes, TimeSpan deadline, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("An executable is required.", nameof(executable));
        if (!Path.IsPathFullyQualified(absentOutput))
            throw new ArgumentException("The output path must be absolute.", nameof(absentOutput));
        OciLayout.RequireNoLinks(Path.GetDirectoryName(absentOutput)!);
        if (Path.Exists(absentOutput))
            throw new IOException("The output path must be absent.");
        if (maxBytes is < 1 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(300))
            throw new ArgumentOutOfRangeException(nameof(deadline));
        var stagingOutput = absentOutput + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = new Process
            {
                StartInfo = start
            };
            using var timer = new CancellationTokenSource(deadline);
            using var outputLimit = new CancellationTokenSource();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token, outputLimit.Token);
            try
            {
                if (!process.Start())
                    throw new BoundedProcessException(ProcessStopReason.Start);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new BoundedProcessException(ProcessStopReason.Start);
            }

            var stdout = CopyStdoutAsync(process.StandardOutput.BaseStream, stagingOutput, maxBytes, outputLimit, stop.Token);
            var stderr = DrainStderrAsync(process.StandardError.BaseStream, outputLimit, stop.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(stop.Token), stdout, stderr).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new BoundedProcessException(ProcessStopReason.Cleanup);
                if (!File.Exists(stagingOutput))
                    throw new BoundedProcessException(ProcessStopReason.Cleanup);
                OciLayout.RequireNoLinks(Path.GetDirectoryName(absentOutput)!);
                AtomicFilePublication.Publish(stagingOutput, absentOutput);
            }
            catch (InvalidDataException)
            {
                // Invocation-budget failure retains its primary classification after bounded child cleanup.
                await StopAndDrainAsync(process, stdout, stderr).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or BoundedProcessException)
            {
                var reason = cancellationToken.IsCancellationRequested ? ProcessStopReason.Cancelled : outputLimit.IsCancellationRequested ? ProcessStopReason.OutputLimit : timer.IsCancellationRequested ? ProcessStopReason.Deadline : ProcessStopReason.Cleanup;
                await StopAndDrainAsync(process, stdout, stderr).ConfigureAwait(false);
                throw new BoundedProcessException(reason);
            }
        }
        catch
        {
            TryDelete(stagingOutput);
            throw;
        }
    }

    private static async Task CopyStdoutAsync(Stream source, string stagingOutput, long maxBytes, CancellationTokenSource outputLimit, CancellationToken token)
    {
        try
        {
            await using var destination = new FileStream(stagingOutput, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                    break;
                var next = checked(total + read);
                if (next > maxBytes)
                {
                    outputLimit.Cancel();
                    throw new BoundedProcessException(ProcessStopReason.OutputLimit);
                }

                total = next;
                TemporaryDiskBudget.Account(read);
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }

            await destination.FlushAsync(token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            outputLimit.Cancel();
            throw;
        }
    }

    private static async Task DrainStderrAsync(Stream source, CancellationTokenSource outputLimit, CancellationToken token)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                    break;
                var next = checked(total + read);
                if (next > 1024 * 1024)
                {
                    outputLimit.Cancel();
                    throw new BoundedProcessException(ProcessStopReason.OutputLimit);
                }

                total = next;
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            outputLimit.Cancel();
            throw;
        }
    }

    private static async Task StopAndDrainAsync(Process process, Task stdout, Task stderr)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception cleanupError) when (cleanupError is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
        }

        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception drainError) when (drainError is OperationCanceledException or IOException or InvalidDataException or BoundedProcessException or TimeoutException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
