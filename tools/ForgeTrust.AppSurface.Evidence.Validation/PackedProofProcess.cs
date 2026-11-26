using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Owns a packed-proof child and its bounded, redacted output log.</summary>
internal static class PackedProofProcess
{
    private const int MaximumCharactersPerStream = 4 * 1024 * 1024;

    /// <summary>Builds a command that uses the caller's environment with SDK build servers disabled.</summary>
    internal static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        return start;
    }

    /// <summary>Starts one command and joins its process and output streams before publishing its stage log.</summary>
    /// <param name="stage">Stage identifier used for the log's name and startup marker.</param>
    /// <param name="start">Command and caller-selected child environment from <see cref="CreateStartInfo"/>.</param>
    /// <param name="logDirectory">Caller-owned directory for the stage log.</param>
    /// <param name="cancellationToken">Cancels the owned command and joins cleanup before propagating.</param>
    /// <param name="stageTimeout">Optional deadline in addition to the caller's cancellation.</param>
    /// <param name="recordExit">Optional callback for the process exit and any interrupted wait.</param>
    /// <returns>The exit code, standard output and retained redacted log path, when available, after settlement.</returns>
    /// <remarks>A diagnostic I/O failure after a command failure preserves the original exception or exit code.</remarks>
    internal static async Task<Result> RunAsync(string stage, ProcessStartInfo start, string logDirectory,
        CancellationToken cancellationToken, TimeSpan? stageTimeout = null, Action<ExitObservation>? recordExit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (stageTimeout is { } limit) timeout.CancelAfter(limit);
        var logPath = PathUtils.PathUnder(logDirectory, stage + ".log");
        await File.WriteAllTextAsync(logPath, JsonSerializer.Serialize(new
        {
            stage,
            state = "starting",
            startedAtUtc = DateTimeOffset.UtcNow,
        }), timeout.Token).ConfigureAwait(false);

        using var process = new Process { StartInfo = start };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch
        {
            try { File.Delete(logPath); }
            catch (Exception exception) when (IsLogIoFailure(exception))
            {
                // Preserve the process launch exception.
            }
            throw;
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        var stdout = DrainAsync(process.StandardOutput, output, timeout.Token);
        var stderr = DrainAsync(process.StandardError, error, timeout.Token);
        string? interruptedWhile = null;
        string? retainedLogPath = logPath;
        try
        {
            var pending = new List<Task> { process.WaitForExitAsync(timeout.Token), stdout, stderr };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
        }
        catch
        {
            // Take the snapshot before cleanup changes which work is still unfinished.
            interruptedWhile = (process.HasExited, stdout.IsCompletedSuccessfully, stderr.IsCompletedSuccessfully) switch
            {
                (false, _, _) => "process-exit",
                (true, false, false) => "standard-output-and-error",
                (true, false, true) => "standard-output",
                (true, true, false) => "standard-error",
                _ => "process-exit-observation",
            };
            timeout.Cancel();
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between the check and the kill request.
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (Exception) { /* The original failure remains authoritative. */ }
            throw;
        }
        finally
        {
            if (process.HasExited)
                recordExit?.Invoke(new(process.ExitCode, stopwatch.ElapsedMilliseconds, interruptedWhile));
            var log = output.ToString() + error.ToString();
            foreach (var pair in start.Environment)
            {
                if (!string.IsNullOrEmpty(pair.Value) && (pair.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)))
                    log = log.Replace(pair.Value, "[redacted]", StringComparison.Ordinal);
            }
            try { await File.WriteAllTextAsync(logPath, log, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (IsLogIoFailure(exception)
                && (interruptedWhile is not null || process.HasExited && process.ExitCode != 0))
            {
                retainedLogPath = null;
            }
        }
        return new(process.ExitCode, output.ToString(), retainedLogPath);
    }

    private static bool IsLogIoFailure(Exception exception) => exception is IOException or UnauthorizedAccessException;

    private static async Task DrainAsync(StreamReader reader, StringBuilder capture, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (capture.Length + count > MaximumCharactersPerStream)
                throw new InvalidDataException("Bounded proof output exceeded.");
            capture.Append(buffer, 0, count);
        }
    }

    /// <summary>Contains a settled command's output and the log path when the redacted output was retained.</summary>
    internal sealed record Result(int ExitCode, string StandardOutput, string? LogPath);

    /// <summary>Records the exit code, elapsed time and the state observed just before interruption cleanup.</summary>
    internal sealed record ExitObservation(int ExitCode, long ElapsedMilliseconds, string? InterruptedWhile);
}
