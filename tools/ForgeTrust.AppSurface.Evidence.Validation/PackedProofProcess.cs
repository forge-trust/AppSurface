using System.ComponentModel;
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
    /// <param name="progress">Optional journal for the individually observed process and stream boundaries.</param>
    /// <param name="terminateProcess">Optional test override for termination, with the tree scope passed explicitly.</param>
    /// <returns>The exit code, standard output and retained redacted log path, when available, after settlement.</returns>
    /// <remarks>A diagnostic I/O failure after a command failure preserves the original exception or exit code.</remarks>
    internal static async Task<Result> RunAsync(string stage, ProcessStartInfo start, string logDirectory,
        CancellationToken cancellationToken, TimeSpan? stageTimeout = null, Action<ExitObservation>? recordExit = null,
        PackedProofProgress? progress = null, Action<Process, bool>? terminateProcess = null)
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

        progress?.Record(stage, "process-started");
        var output = new StringBuilder();
        var error = new StringBuilder();
        var stdout = DrainAsync(process.StandardOutput, output, timeout.Token);
        var stderr = DrainAsync(process.StandardError, error, timeout.Token);
        string? interruptedWhile = null;
        string? retainedLogPath;
        try
        {
            await ObserveWaitsAsync(WaitPhase.Execution, (WaitKind.ProcessExit, process.WaitForExitAsync(timeout.Token)),
                (WaitKind.Stdout, stdout), (WaitKind.Stderr, stderr)).ConfigureAwait(false);
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
            progress?.Record(stage, "wait-interrupted");
            timeout.Cancel();
            try
            {
                RequestTermination(entireProcessTree: true);
            }
            catch (Exception treeFailure) when (IsTerminationFailure(treeFailure))
            {
                progress?.Record(stage, "cleanup-kill-failed");
                try { RequestTermination(entireProcessTree: false); }
                catch (Exception rootFailure) when (IsTerminationFailure(rootFailure))
                {
                    progress?.Record(stage, "cleanup-root-kill-failed");
                    throw new AggregateException(treeFailure, rootFailure);
                }
                throw;
            }
            finally
            {
                // Keep ownership even if neither signal succeeds. Returning would let the child keep writing.
                try
                {
                    await ObserveWaitsAsync(WaitPhase.Cleanup,
                        (WaitKind.ProcessExit, process.WaitForExitAsync(CancellationToken.None)))
                        .ConfigureAwait(false);
                }
                finally
                {
                    await ObserveWaitsAsync(WaitPhase.Cleanup, (WaitKind.Stdout, stdout), (WaitKind.Stderr, stderr))
                        .ConfigureAwait(false);
                }
                progress?.Record(stage, "interruption-cleanup-completed");
            }
            throw;
        }
        finally
        {
            if (process.HasExited)
            {
                recordExit?.Invoke(new(process.ExitCode, stopwatch.ElapsedMilliseconds, interruptedWhile));
                retainedLogPath = await WriteFinalTranscriptAsync(logPath, output.ToString() + error.ToString(),
                    process, waitInterrupted: interruptedWhile is not null).ConfigureAwait(false);
            }
            else retainedLogPath = logPath;
        }
        return new(process.ExitCode, output.ToString(), retainedLogPath);

        void RequestTermination(bool entireProcessTree)
        {
            try
            {
                if (process.HasExited) return;
                if (terminateProcess is null) process.Kill(entireProcessTree);
                else terminateProcess(process, entireProcessTree);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between the check and the kill request.
            }
        }

        async Task ObserveWaitsAsync(WaitPhase phase, params (WaitKind Kind, Task Completion)[] waits)
        {
            var pending = waits.ToList();
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending.Select(item => item.Completion)).ConfigureAwait(false);
                var index = pending.FindIndex(item => item.Completion == completed);
                var kind = pending[index].Kind;
                try { await completed.ConfigureAwait(false); }
                catch (Exception) when (phase == WaitPhase.Cleanup && kind != WaitKind.ProcessExit)
                {
                    // A stream failure during cleanup must not replace the original exception.
                }

                var prefix = phase == WaitPhase.Cleanup ? "cleanup-" : "";
                if (kind == WaitKind.ProcessExit)
                    progress?.Record(stage, prefix + "process-exited", process.ExitCode);
                else
                {
                    var stream = kind == WaitKind.Stdout ? "stdout" : "stderr";
                    var outcome = completed.IsCompletedSuccessfully ? "drained" : completed.IsCanceled ? "cancelled" : "faulted";
                    progress?.Record(stage, $"{prefix}{stream}-{outcome}");
                }
                pending.RemoveAt(index);
            }
        }
    }

    private enum WaitPhase { Execution, Cleanup }

    private enum WaitKind { ProcessExit, Stdout, Stderr }

    private static async Task<string?> WriteFinalTranscriptAsync(string logPath, string log, Process process,
        bool waitInterrupted)
    {
        foreach (var pair in process.StartInfo.Environment)
        {
            if (!string.IsNullOrEmpty(pair.Value) && (pair.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)))
                log = log.Replace(pair.Value, "[redacted]", StringComparison.Ordinal);
        }
        try { await File.WriteAllTextAsync(logPath, log, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (IsLogIoFailure(exception)
            && (waitInterrupted || process.HasExited && process.ExitCode != 0))
        {
            return null;
        }
        return logPath;
    }

    private static bool IsLogIoFailure(Exception exception) => exception is IOException or UnauthorizedAccessException;

    private static bool IsTerminationFailure(Exception exception)
        => exception is Win32Exception or InvalidOperationException or NotSupportedException or AggregateException;

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
