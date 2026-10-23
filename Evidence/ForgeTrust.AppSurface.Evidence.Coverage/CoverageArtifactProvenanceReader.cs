using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Reads bounded, informational Git fingerprints for a coverage artifact run.</summary>
/// <remarks>Only hashes and the clean/dirty/unknown label leave this reader; Git output is never retained.</remarks>
internal sealed class CoverageArtifactProvenanceReader
{
    private static readonly TimeSpan TotalLimit = TimeSpan.FromMilliseconds(500);
    private const int OutputLimit = 16 * 1024;
    private readonly Func<string, string[], TimeSpan, CancellationToken, Task<string?>> _run;

    /// <summary>Creates a reader using the local Git executable.</summary>
    internal CoverageArtifactProvenanceReader() : this(RunGitAsync) { }

    /// <summary>Creates a reader with a bounded command seam used for cancellation and fault verification.</summary>
    /// <param name="run">A command runner that observes the supplied total deadline token and returns bounded output.</param>
    internal CoverageArtifactProvenanceReader(Func<string, string[], TimeSpan, CancellationToken, Task<string?>> run)
        => _run = run ?? throw new ArgumentNullException(nameof(run));

    /// <summary>Combines caller policy metadata with repository HEAD and working-tree state fingerprints.</summary>
    /// <param name="repository">Repository root supplied by the coverage invocation.</param>
    /// <param name="policy">Caller-provided provenance whose configuration fingerprint is preserved.</param>
    /// <param name="token">Cancellation token for the bounded probes.</param>
    internal async Task<CoverageArtifactProvenance> ReadAsync(string repository, CoverageArtifactProvenance policy, CancellationToken token = default)
        => (await ReadWithOutcomeAsync(repository, policy, token).ConfigureAwait(false)).Provenance;

    /// <summary>Reads provenance and returns a safe outcome for the Git-state probe.</summary>
    /// <param name="repository">Repository root supplied by the coverage invocation.</param>
    /// <param name="policy">Caller-provided provenance whose configuration fingerprint is preserved.</param>
    /// <param name="token">Cancellation token for the bounded probes.</param>
    /// <returns>Fingerprints and a non-sensitive state describing whether the status probe completed.</returns>
    internal async Task<CoverageArtifactProvenanceReadResult> ReadWithOutcomeAsync(
        string repository, CoverageArtifactProvenance policy, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TotalLimit);
        try
        {
            var head = (await _run(repository, ["rev-parse", "--verify", "HEAD"], TotalLimit, deadline.Token).ConfigureAwait(false))?.Trim();
            if (!IsRevision(head))
                return new(new(null, "unknown", policy.ConfigurationFingerprint), CoverageArtifactGitStateProbeOutcome.NotAttempted);
            var revision = Fingerprint(head!);
            string? status;
            try { status = await _run(repository, ["status", "--porcelain=v1", "--untracked-files=normal"], TotalLimit, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
            {
                return new(new(revision, "unknown", policy.ConfigurationFingerprint), CoverageArtifactGitStateProbeOutcome.DeadlineExceeded);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return new(new(revision, "unknown", policy.ConfigurationFingerprint), CoverageArtifactGitStateProbeOutcome.RunnerCancelled);
            }
            catch (Exception exception) when (IsUnavailable(exception))
            { return new(new(revision, "unknown", policy.ConfigurationFingerprint), GetFailureOutcome(exception)); }
            if (status is null)
                return new(new(revision, "unknown", policy.ConfigurationFingerprint), CoverageArtifactGitStateProbeOutcome.NoUsableOutput);
            if (Encoding.UTF8.GetByteCount(status) > OutputLimit)
                return new(new(revision, "unknown", policy.ConfigurationFingerprint), CoverageArtifactGitStateProbeOutcome.OutputRejected);
            var state = status.Length == 0 ? "clean" : "dirty";
            return new(new(revision, Fingerprint(state + "\n" + status), policy.ConfigurationFingerprint),
                CoverageArtifactGitStateProbeOutcome.Fingerprinted);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        { return new(new(null, "unknown", policy.ConfigurationFingerprint), CoverageArtifactGitStateProbeOutcome.RevisionDeadlineExceeded); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(new(null, "unknown", policy.ConfigurationFingerprint), CoverageArtifactGitStateProbeOutcome.NotAttempted); }
        catch (Exception exception) when (IsUnavailable(exception))
        { return new(new(null, "unknown", policy.ConfigurationFingerprint), CoverageArtifactGitStateProbeOutcome.NotAttempted); }
    }

    private static bool IsUnavailable(Exception exception) => exception is IOException or InvalidDataException
        or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException;

    private static CoverageArtifactGitStateProbeOutcome GetFailureOutcome(Exception exception) => exception switch
    {
        InvalidDataException => CoverageArtifactGitStateProbeOutcome.OutputRejected,
        IOException => CoverageArtifactGitStateProbeOutcome.IoFailure,
        InvalidOperationException => CoverageArtifactGitStateProbeOutcome.InvalidProcessState,
        System.ComponentModel.Win32Exception => CoverageArtifactGitStateProbeOutcome.NativeProcessFailure,
        UnauthorizedAccessException => CoverageArtifactGitStateProbeOutcome.PermissionDenied,
        _ => throw new ArgumentOutOfRangeException(nameof(exception))
    };

    private static bool IsRevision(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant();

    private static async Task<string?> RunGitAsync(string repository, string[] arguments, TimeSpan timeout, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repository,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--no-optional-locks");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("core.fsmonitor=false");
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(repository);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(timeout);
        var output = ReadBoundedAsync(process.StandardOutput, OutputLimit, linked.Token);
        var error = ReadBoundedAsync(process.StandardError, OutputLimit, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false);
            _ = await error.ConfigureAwait(false);
            return process.ExitCode == 0 && stdout.Length <= OutputLimit ? stdout : null;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            linked.Cancel();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException) { }
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or TimeoutException or ObjectDisposedException) { }
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var buffer = new char[1024];
        var result = new StringBuilder(Math.Min(limit, 1024));
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return result.ToString();
            if (result.Length + count > limit) throw new InvalidDataException("Git probe output exceeded its bound.");
            result.Append(buffer, 0, count);
        }
    }
}

/// <summary>Describes whether the bounded Git-state probe produced a fingerprint or a safe unknown.</summary>
internal enum CoverageArtifactGitStateProbeOutcome
{
    /// <summary>The revision was unavailable or invalid, so no state probe was attempted.</summary>
    NotAttempted,
    /// <summary>The shared 500 ms deadline expired during the revision probe, so no state probe was attempted.</summary>
    RevisionDeadlineExceeded,
    /// <summary>The bounded status probe completed and its result was fingerprinted.</summary>
    Fingerprinted,
    /// <summary>The shared 500 ms deadline expired during the status probe.</summary>
    DeadlineExceeded,
    /// <summary>The command runner canceled without the shared deadline or caller token being canceled.</summary>
    RunnerCancelled,
    /// <summary>The command runner returned null, such as when Git could not start or returned a failing exit code.</summary>
    NoUsableOutput,
    /// <summary>The status output exceeded its configured byte bound.</summary>
    OutputRejected,
    /// <summary>The status process encountered an input/output failure.</summary>
    IoFailure,
    /// <summary>The status process reported an invalid operation state.</summary>
    InvalidProcessState,
    /// <summary>The operating system rejected or failed the status process operation.</summary>
    NativeProcessFailure,
    /// <summary>The status process could not access the repository.</summary>
    PermissionDenied
}

/// <summary>Pairs provenance fingerprints with the safe outcome of its Git-state probe.</summary>
/// <param name="Provenance">The fingerprints to retain with the coverage artifact.</param>
/// <param name="GitStateProbeOutcome">A non-sensitive status-probe outcome for diagnostics and verification.</param>
internal sealed record CoverageArtifactProvenanceReadResult(
    CoverageArtifactProvenance Provenance,
    CoverageArtifactGitStateProbeOutcome GitStateProbeOutcome);
