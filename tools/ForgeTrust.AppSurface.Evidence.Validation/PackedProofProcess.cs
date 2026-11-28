using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Owns a packed-proof child and its bounded, redacted output log.</summary>
internal static class PackedProofProcess
{
    private const int DefaultMaximumCharactersPerStream = 4 * 1024 * 1024;

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
    /// <param name="logPath">Path to the stage log within caller-owned output.</param>
    /// <param name="cancellationToken">Cancels the owned command and joins cleanup before propagating.</param>
    /// <param name="stageTimeout">Optional deadline in addition to the caller's cancellation.</param>
    /// <param name="recordExit">Optional callback for the process exit and any interrupted wait.</param>
    /// <param name="progress">Optional journal for the individually observed process and stream boundaries.</param>
    /// <param name="terminateProcess">Optional test override for termination, with the tree scope passed explicitly.</param>
    /// <param name="maximumCharactersPerStream">Maximum captured characters for each output stream.</param>
    /// <returns>The exit code, standard output and retained redacted log path, when available, after settlement.</returns>
    /// <remarks>
    /// The retained redacted transcript is capped at the combined capture budget.
    /// A diagnostic I/O failure after a command failure preserves the original exception or exit code.
    /// </remarks>
    internal static async Task<Result> RunAsync(string stage, ProcessStartInfo start, string logPath,
        CancellationToken cancellationToken, TimeSpan? stageTimeout = null, Action<ExitObservation>? recordExit = null,
        PackedProofProgress? progress = null, Action<Process, bool>? terminateProcess = null,
        int maximumCharactersPerStream = DefaultMaximumCharactersPerStream)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharactersPerStream);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (stageTimeout is { } limit) timeout.CancelAfter(limit);
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
        var stdout = DrainAsync(process.StandardOutput, output, maximumCharactersPerStream, timeout.Token);
        var stderr = DrainAsync(process.StandardError, error, maximumCharactersPerStream, timeout.Token);
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
            await SettleInterruptedProcessAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (process.HasExited)
            {
                recordExit?.Invoke(new(process.ExitCode, stopwatch.ElapsedMilliseconds, interruptedWhile));
                retainedLogPath = await WriteFinalTranscriptAsync(logPath, output.ToString(), error.ToString(),
                    process, (int)Math.Min(int.MaxValue, 2L * maximumCharactersPerStream),
                    waitInterrupted: interruptedWhile is not null).ConfigureAwait(false);
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

        async Task SettleInterruptedProcessAsync()
        {
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

    private static async Task<string?> WriteFinalTranscriptAsync(string logPath, string output, string error, Process process,
        int maximumCharacters, bool waitInterrupted)
    {
        var redacted = RedactTranscript(output, error, process.StartInfo.Environment, maximumCharacters, waitInterrupted);
        try { await File.WriteAllTextAsync(logPath, redacted, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (IsLogIoFailure(exception)
            && (waitInterrupted || process.HasExited && process.ExitCode != 0))
        {
            return null;
        }
        return logPath;
    }

    /// <summary>Redacts the captured streams in output order, using the combined capture budget.</summary>
    /// <param name="output">Captured standard output.</param>
    /// <param name="error">Captured standard error.</param>
    /// <param name="environment">The child environment used to identify sensitive values.</param>
    /// <param name="maximumCharacters">Nonnegative combined character budget for the retained transcript.</param>
    /// <param name="waitInterrupted">Whether stream tails might contain a truncated sensitive value.</param>
    /// <returns>The bounded, redacted transcript.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The character budget is negative.</exception>
    internal static string RedactTranscript(string output, string error, IEnumerable<KeyValuePair<string, string?>> environment,
        int maximumCharacters, bool waitInterrupted)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        var transcript = output + error;
        if (transcript.Length == 0) return transcript;

        var sensitiveValues = environment.Where(pair => !string.IsNullOrEmpty(pair.Value)
                && (pair.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Value!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
        if (sensitiveValues.Length == 0)
            return transcript.Length <= maximumCharacters ? transcript : PrefixWithinBudget(transcript, maximumCharacters).ToString();

        // Check each stream's own endpoint. An interruption can retain an alias prefix even when
        // the next character in the combined transcript belongs to the other stream.
        var outputPrefixLength = waitInterrupted ? TrailingSensitivePrefixLength(output, sensitiveValues) : 0;
        var errorPrefixLength = waitInterrupted ? TrailingSensitivePrefixLength(error, sensitiveValues) : 0;
        var outputPrefixStart = outputPrefixLength == 0 ? -1 : output.Length - outputPrefixLength;
        var errorPrefixStart = errorPrefixLength == 0 ? -1 : transcript.Length - errorPrefixLength;

        // The longest alias covers everything matched at the same start. Resume one character later
        // so an alias beginning inside that span can extend the redaction.
        var expression = new Regex(string.Join("|", sensitiveValues.Select(Regex.Escape)), RegexOptions.CultureInvariant);
        var redacted = new StringBuilder(Math.Min(transcript.Length, maximumCharacters));
        var offset = 0;
        for (var next = FindMatch(0); next is { } span;)
        {
            var end = span.End;
            next = FindMatch(span.Start + 1);
            while (next is { } overlapping && overlapping.Start < end)
            {
                end = Math.Max(end, overlapping.End);
                next = FindMatch(overlapping.Start + 1);
            }

            if (!AppendWithinBudget(transcript.AsSpan(offset, span.Start - offset))
                || !AppendWithinBudget("[redacted]"))
                return redacted.ToString();
            offset = end;
        }
        AppendWithinBudget(transcript.AsSpan(offset));
        return redacted.ToString();

        (int Start, int End)? FindMatch(int startAt)
        {
            (int Start, int End)? tail = outputPrefixStart >= startAt ? (outputPrefixStart, output.Length)
                : errorPrefixStart >= startAt ? (errorPrefixStart, transcript.Length) : null;
            var matches = expression.EnumerateMatches(transcript.AsSpan(), startAt);
            if (!matches.MoveNext()) return tail;

            var match = matches.Current;
            var end = match.Index + match.Length;
            return tail is { } partial && (partial.Start < match.Index || partial.Start == match.Index && partial.End > end)
                ? partial : (match.Index, end);
        }

        bool AppendWithinBudget(ReadOnlySpan<char> value)
        {
            var prefix = PrefixWithinBudget(value, maximumCharacters - redacted.Length);
            redacted.Append(prefix);
            return prefix.Length == value.Length;
        }
    }

    private static ReadOnlySpan<char> PrefixWithinBudget(ReadOnlySpan<char> value, int available)
    {
        if (value.Length <= available) return value;

        // Keep a UTF-16 surrogate pair together at the truncation boundary.
        if (available > 0 && char.IsHighSurrogate(value[available - 1]) && char.IsLowSurrogate(value[available]))
            available--;
        return value[..available];
    }

    private static int TrailingSensitivePrefixLength(string stream, IReadOnlyList<string> sensitiveValues)
    {
        var longest = 0;
        foreach (var value in sensitiveValues)
        {
            var length = Math.Min(stream.Length, value.Length - 1);
            if (length <= longest) continue;

            // Prefix links let us find the longest matching suffix in linear time, including for
            // repeated characters in long values.
            var prefix = value.AsSpan(0, length);
            var links = new int[length];
            for (var index = 1; index < length; index++)
                links[index] = Advance(prefix, links, links[index - 1], prefix[index]);

            var matched = 0;
            foreach (var character in stream.AsSpan(stream.Length - length))
                matched = Advance(prefix, links, matched, character);
            longest = Math.Max(longest, matched);
        }
        return longest;

        static int Advance(ReadOnlySpan<char> prefix, int[] links, int matched, char character)
        {
            while (matched > 0 && prefix[matched] != character)
                matched = links[matched - 1];
            return prefix[matched] == character ? matched + 1 : matched;
        }
    }

    private static bool IsLogIoFailure(Exception exception) => exception is IOException or UnauthorizedAccessException;

    private static bool IsTerminationFailure(Exception exception)
        => exception is Win32Exception or InvalidOperationException or NotSupportedException or AggregateException;

    private static async Task DrainAsync(StreamReader reader, StringBuilder capture, int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (count > maximumCharacters - capture.Length)
                throw new InvalidDataException("Bounded proof output exceeded.");
            capture.Append(buffer, 0, count);
        }
    }

    /// <summary>Contains a settled command's output and the log path when the redacted output was retained.</summary>
    internal sealed record Result(int ExitCode, string StandardOutput, string? LogPath);

    /// <summary>Records the exit code, elapsed time and the state observed just before interruption cleanup.</summary>
    internal sealed record ExitObservation(int ExitCode, long ElapsedMilliseconds, string? InterruptedWhile);
}
