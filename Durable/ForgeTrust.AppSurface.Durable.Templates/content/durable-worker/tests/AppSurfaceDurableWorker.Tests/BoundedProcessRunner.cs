using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DurableWorkerTemplate.Tests;

internal static class BoundedProcessRunner
{
    private const int MaximumCaptureBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> SensitiveEnvironmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PGPASSWORD",
        "APPSURFACE_TEMPLATE_ACTIVATION_TOKEN",
        "APPSURFACE_TEMPLATE_NATIVE_ADMIN_CONNECTION",
        "APPSURFACE_TEMPLATE_NATIVE_PG_BIN",
        "APPSURFACE_TEMPLATE_NATIVE_SETUP_REMAINING_MS",
        "APPSURFACE_DURABLE_MIGRATION_CONNECTION",
    };

    internal static async Task<BoundedProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        TimeSpan budget,
        IReadOnlyCollection<string>? redactValues = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);

        var startInfo = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        RemoveSensitiveInheritedEnvironment(startInfo.Environment);
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                if (pair.Value is null)
                {
                    startInfo.Environment.Remove(pair.Key);
                }
                else
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start the owned {Path.GetFileName(executable)} process.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not start the owned {Path.GetFileName(executable)} process ({exception.GetType().Name}).");
        }

        var stdoutTask = CaptureAsync(process.StandardOutput.BaseStream);
        var stderrTask = CaptureAsync(process.StandardError.BaseStream);
        using var deadline = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            var cleanupStartedAt = Stopwatch.GetTimestamp();
            var terminated = await TerminateOwnedTreeAsync(process).ConfigureAwait(false);
            if (!terminated)
            {
                throw new InvalidOperationException(
                    $"The owned {Path.GetFileName(executable)} process tree did not terminate within the bounded cleanup window.");
            }

            var captureBudget = FixtureBudgets.ChildTermination - Stopwatch.GetElapsedTime(cleanupStartedAt);
            if (!await ObserveCapturesAsync(stdoutTask, stderrTask, captureBudget).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"The owned {Path.GetFileName(executable)} process left an output pipe open beyond the bounded cleanup window.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new TimeoutException(
                $"The owned {Path.GetFileName(executable)} process exceeded its {budget.TotalSeconds:0.###}-second phase budget; its process tree was terminated.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new BoundedProcessResult(
            process.ExitCode,
            Redact(stdout.Text, redactValues),
            Redact(stderr.Text, redactValues),
            stdout.Truncated,
            stderr.Truncated);
    }

    private static async Task<CaptureResult> CaptureAsync(Stream stream)
    {
        var buffer = new byte[16 * 1024];
        using var capture = new MemoryStream(capacity: MaximumCaptureBytes);
        var truncated = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var accepted = Math.Min(read, MaximumCaptureBytes - (int)capture.Length);
            if (accepted > 0)
            {
                capture.Write(buffer, 0, accepted);
            }

            truncated |= accepted < read;
        }

        return new CaptureResult(Encoding.UTF8.GetString(capture.GetBuffer(), 0, (int)capture.Length), truncated);
    }

    private static async Task<bool> TerminateOwnedTreeAsync(Process process)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                return process.HasExited;
            }
            catch (Win32Exception)
            {
                return process.HasExited;
            }
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(FixtureBudgets.ChildTermination).ConfigureAwait(false);
            return process.HasExited;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task<bool> ObserveCapturesAsync(
        Task<CaptureResult> standardOutput,
        Task<CaptureResult> standardError,
        TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero)
        {
            return standardOutput.IsCompleted && standardError.IsCompleted;
        }

        try
        {
            _ = await Task.WhenAll(standardOutput, standardError).WaitAsync(budget).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            // Closing a pipe during owned process-tree termination is an expected late-capture outcome.
            return true;
        }
    }

    private static void RemoveSensitiveInheritedEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.ToArray())
        {
            if (SensitiveEnvironmentNames.Contains(name)
                || name.StartsWith("PG", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("APPSURFACE_", StringComparison.OrdinalIgnoreCase)
                || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
                || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                || name.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase))
            {
                environment.Remove(name);
            }
        }
    }

    private static string Redact(string value, IReadOnlyCollection<string>? redactions)
    {
        if (redactions is null)
        {
            return value;
        }

        foreach (var redaction in redactions.Where(static value => !string.IsNullOrEmpty(value)))
        {
            value = value.Replace(redaction, "<redacted>", StringComparison.Ordinal);
        }

        return value;
    }

    private sealed record CaptureResult(string Text, bool Truncated);
}

internal sealed record BoundedProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated);
