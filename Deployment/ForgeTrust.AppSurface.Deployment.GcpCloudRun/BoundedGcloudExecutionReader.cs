using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ForgeTrust.AppSurface.Deployment.GcpCloudRun;

/// <summary>Outcome of a bounded read of one selected Cloud Run execution.</summary>
public enum BoundedGcloudExecutionReadOutcome
{
    /// <summary>Process exited successfully and all output was captured and valid UTF-8.</summary>
    Complete,
    /// <summary>Process exited with a nonzero code.</summary>
    NonzeroExit,
    /// <summary>The gcloud process could not be started.</summary>
    ToolUnavailable,
    /// <summary>A stream exceeded its inclusive byte limit.</summary>
    OutputLimit,
    /// <summary>Standard output or standard error was not valid UTF-8.</summary>
    InvalidEncoding,
    /// <summary>The total read budget expired.</summary>
    Deadline,
    /// <summary>A process read failed.</summary>
    ReadFailure,
    /// <summary>Process or stream cleanup could not be confirmed.</summary>
    CleanupUnconfirmed,
}

/// <summary>Immutable, safe result from a bounded execution read.</summary>
/// <remarks>Standard error is deliberately never exposed. Output bytes are usable only for <see cref="BoundedGcloudExecutionReadOutcome.Complete"/> with a zero exit code.</remarks>
public sealed class BoundedGcloudExecutionReadResult
{
    private readonly byte[] _standardOutput;

    /// <summary>Creates a safe classified result and defensively copies the supplied standard-output bytes.</summary>
    /// <param name="outcome">Classification of process start, exit, output, and cleanup.</param>
    /// <param name="exitCode">Process exit code when known; a <see langword="null"/> value means no exit code is available.</param>
    /// <param name="standardOutput">Captured standard-output bytes. Standard error is never accepted or exposed by this result type.</param>
    /// <exception cref="ArgumentOutOfRangeException">The standard-output payload exceeds 1 MiB.</exception>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> is <see cref="BoundedGcloudExecutionReadOutcome.Complete"/> with a nonzero exit code, or the supplied bytes are not valid UTF-8.</exception>
    public BoundedGcloudExecutionReadResult(BoundedGcloudExecutionReadOutcome outcome, int? exitCode, ReadOnlySpan<byte> standardOutput = default)
    {
        if (standardOutput.Length > 1_048_576) throw new ArgumentOutOfRangeException(nameof(standardOutput));
        if (outcome == BoundedGcloudExecutionReadOutcome.Complete && exitCode != 0) throw new ArgumentException("Complete output requires a zero exit code.", nameof(exitCode));
        if (standardOutput.Length > 0 && !IsUtf8(standardOutput)) throw new ArgumentException("Standard output must be valid UTF-8.", nameof(standardOutput));
        Outcome = outcome;
        ExitCode = exitCode;
        _standardOutput = standardOutput.ToArray();
    }

    /// <summary>Gets the classified read outcome.</summary>
    public BoundedGcloudExecutionReadOutcome Outcome { get; }

    /// <summary>Gets the process exit code when the process reached an exit state.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets a defensive copy of the captured standard-output bytes; the array is empty when no bytes were retained.</summary>
    public byte[] StandardOutput => (byte[])_standardOutput.Clone();

    private static bool IsUtf8(ReadOnlySpan<byte> bytes)
    {
        try { _ = new UTF8Encoding(false, true).GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }
}

/// <summary>Reads one exact Cloud Run execution using a bounded, read-only gcloud process.</summary>
public interface IBoundedGcloudExecutionReader
{
    /// <summary>Reads the selected canonical execution within the supplied total budget.</summary>
    /// <param name="executionResourceName">Canonical Cloud Run execution resource name, including project, location, Job, and execution segments.</param>
    /// <param name="timeout">Finite positive total read budget. The default implementation reserves part of this budget for process cleanup.</param>
    /// <param name="cancellationToken">Caller cancellation. Cancellation is propagated; a timeout is returned as a <see cref="BoundedGcloudExecutionReadOutcome.Deadline"/> result.</param>
    /// <returns>A safe classification and, only for complete valid output, captured standard-output bytes. Standard error is never returned.</returns>
    /// <exception cref="ArgumentException"><paramref name="executionResourceName"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is nonpositive, infinite, or outside the supported timer range.</exception>
    /// <exception cref="OperationCanceledException">The caller requested cancellation.</exception>
    Task<BoundedGcloudExecutionReadResult> ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Default bounded reader for the fixed Cloud SDK execution-describe command.</summary>
public sealed class BoundedGcloudExecutionReader : IBoundedGcloudExecutionReader
{
    private const int StandardOutputLimit = 1_048_576;
    private const int StandardErrorLimit = 65_536;
    private readonly string _executable;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a reader using the platform gcloud launcher.</summary>
    /// <remarks>On Windows the launcher is <c>gcloud.cmd</c>; on other platforms it is <c>gcloud</c>. This constructor uses the system clock for read deadlines.</remarks>
    public BoundedGcloudExecutionReader()
        : this(OperatingSystem.IsWindows() ? "gcloud.cmd" : "gcloud", TimeProvider.System)
    {
    }

    /// <summary>Creates a reader with an explicit launcher and clock for process-contract tests.</summary>
    internal BoundedGcloudExecutionReader(string executable, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        _executable = executable;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<BoundedGcloudExecutionReadResult> ReadAsync(string executionResourceName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionResourceName);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ExecutionResource.TryParse(executionResourceName, out var resource) || resource is null)
            return new(BoundedGcloudExecutionReadOutcome.ReadFailure, null);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan || timeout.TotalMilliseconds > 4_294_967_294d)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();

        var start = _timeProvider.GetTimestamp();
        var remaining = Remaining(start, timeout);
        var reserve = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMilliseconds(250).Ticks, remaining.Ticks / 10));
        var launchAllowance = remaining - reserve;
        if (launchAllowance < TimeSpan.FromMilliseconds(1))
            return new(BoundedGcloudExecutionReadOutcome.Deadline, null);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var deadline = new CancellationTokenSource(launchAllowance, _timeProvider);
        using var workRegistration = deadline.Token.Register(static state => ((CancellationTokenSource)state!).Cancel(), budget);
        var startInfo = CreateStartInfo(resource);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) return new(BoundedGcloudExecutionReadOutcome.ToolUnavailable, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new(BoundedGcloudExecutionReadOutcome.ToolUnavailable, null);
        }

        var overflow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutTask = ReadLimitedAsync(process.StandardOutput.BaseStream, StandardOutputLimit, budget.Token, overflow);
        var stderrTask = ReadLimitedAsync(process.StandardError.BaseStream, StandardErrorLimit, budget.Token, overflow);
        var waitTask = process.WaitForExitAsync(budget.Token);
        var combinedTask = Task.WhenAll(waitTask, stdoutTask, stderrTask);
        _ = combinedTask.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var budgetSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var budgetSignalRegistration = budget.Token.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), budgetSignal);
        try
        {
            var completed = await Task.WhenAny(combinedTask, overflow.Task, budgetSignal.Task).ConfigureAwait(false);
            if (completed == budgetSignal.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await CleanupAndResultAsync(process, BoundedGcloudExecutionReadOutcome.Deadline, null, [], stdoutTask, stderrTask, start, timeout, cancellationToken).ConfigureAwait(false);
            }
            if (completed == overflow.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.Cancel();
                return await CleanupAndResultAsync(process, BoundedGcloudExecutionReadOutcome.OutputLimit, null, [], stdoutTask, stderrTask, start, timeout, cancellationToken).ConfigureAwait(false);
            }

            await combinedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (Remaining(start, timeout) <= TimeSpan.Zero)
                return new(BoundedGcloudExecutionReadOutcome.Deadline, process.ExitCode);
            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;
            if (stdout.Exceeded || stderr.Exceeded)
                return await CleanupAndResultAsync(process, BoundedGcloudExecutionReadOutcome.OutputLimit, process.ExitCode, stdout.Bytes, stdoutTask, stderrTask, start, timeout, cancellationToken).ConfigureAwait(false);
            if (!IsValidUtf8(stdout.Bytes) || !IsValidUtf8(stderr.Bytes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(BoundedGcloudExecutionReadOutcome.InvalidEncoding, process.ExitCode);
            }
            if (process.ExitCode != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(BoundedGcloudExecutionReadOutcome.NonzeroExit, process.ExitCode);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(BoundedGcloudExecutionReadOutcome.Complete, 0, stdout.Bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupAndResultAsync(process, BoundedGcloudExecutionReadOutcome.ReadFailure, null, [], stdoutTask, stderrTask, start, timeout, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            return await CleanupAndResultAsync(process, BoundedGcloudExecutionReadOutcome.Deadline, null, [], stdoutTask, stderrTask, start, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (stdoutTask.IsFaulted || stderrTask.IsFaulted)
        {
            return await CleanupAndResultAsync(process, BoundedGcloudExecutionReadOutcome.ReadFailure, null, [], stdoutTask, stderrTask, start, timeout, cancellationToken).ConfigureAwait(false);
        }
    }

    internal ProcessStartInfo CreateStartInfo(ExecutionResource resource) => CreateStartInfo(resource, OperatingSystem.IsWindows());

    /// <summary>Builds the fixed launcher tokens; the platform seam permits testing cmd admission without starting cmd.</summary>
    internal ProcessStartInfo CreateStartInfo(ExecutionResource resource, bool windows)
    {
        var info = new ProcessStartInfo { FileName = _executable, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (windows && _executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(string.Join(' ', ["gcloud.cmd", "run", "jobs", "executions", "describe", resource.Name, "--project", resource.Project, "--region", resource.Region, "--format=json", "--quiet"]));
        }
        else
        {
            foreach (var argument in new[] { "run", "jobs", "executions", "describe", resource.Name, "--project", resource.Project, "--region", resource.Region, "--format=json", "--quiet" }) info.ArgumentList.Add(argument);
        }
        return info;
    }

    private TimeSpan Remaining(long start, TimeSpan timeout)
    {
        var elapsed = _timeProvider.GetElapsedTime(start);
        var remaining = timeout - elapsed;
        return remaining;
    }

    private static Task<(byte[] Bytes, bool Exceeded)> ReadLimitedAsync(Stream stream, int limit, CancellationToken cancellationToken, TaskCompletionSource<bool> overflow)
        // Process anonymous pipes use blocking handles on Unix. FileStream.ReadAsync can occupy the shared
        // worker pool, starving its own cancellation/exit callbacks. Give each finite drain an owned worker.
        => Task.Factory.StartNew(() => ReadLimited(stream, limit, cancellationToken, overflow),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static (byte[] Bytes, bool Exceeded) ReadLimited(Stream stream, int limit, CancellationToken cancellationToken, TaskCompletionSource<bool> overflow)
    {
        var bytes = new List<byte>(Math.Min(limit, 4096));
        var buffer = new byte[8192];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var allowance = Math.Min(buffer.Length, limit - bytes.Count + 1);
            var count = stream.Read(buffer, 0, allowance);
            if (count == 0) return (bytes.ToArray(), false);
            if (bytes.Count + count > limit)
            {
                overflow.TrySetResult(true);
                return (bytes.ToArray(), true);
            }
            bytes.AddRange(buffer.AsSpan(0, count).ToArray());
        }
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try { _ = new UTF8Encoding(false, true).GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private async Task<BoundedGcloudExecutionReadResult> CleanupAndResultAsync(Process process, BoundedGcloudExecutionReadOutcome outcome, int? exitCode, byte[] stdout, Task stdoutTask, Task stderrTask, long start, TimeSpan timeout, CancellationToken callerToken)
    {
        var cleanup = false;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.HasExited)
                    process.Kill(entireProcessTree: false);
            }
            var remaining = Remaining(start, timeout);
            var cleanupTicks = Math.Min(TimeSpan.FromMilliseconds(250).Ticks, Math.Max(0, remaining.Ticks));
            if (cleanupTicks <= 0)
            {
                callerToken.ThrowIfCancellationRequested();
                return new(BoundedGcloudExecutionReadOutcome.CleanupUnconfirmed, null);
            }
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromTicks(cleanupTicks), _timeProvider);
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync(cleanupCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(cleanupCts.Token).ConfigureAwait(false);
            }
            catch (Exception) { }
            if (!stdoutTask.IsCompleted || !stderrTask.IsCompleted)
            {
                // Pipe closure is the final bounded escape for a descendant-held inherited pipe.
                try { process.StandardOutput.Close(); } catch (ObjectDisposedException) { }
                try { process.StandardError.Close(); } catch (ObjectDisposedException) { }
                try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(cleanupCts.Token).ConfigureAwait(false); }
                catch (Exception) { }
            }
            cleanup = process.HasExited && stdoutTask.IsCompleted && stderrTask.IsCompleted;
        }
        catch (Exception) { cleanup = false; }
        callerToken.ThrowIfCancellationRequested();
        return new(cleanup ? outcome : BoundedGcloudExecutionReadOutcome.CleanupUnconfirmed, cleanup ? exitCode : null);
    }

}
