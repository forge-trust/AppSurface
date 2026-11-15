using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Runs a cooperative child operation enrolled with the current Evidence invocation.</summary>
/// <remarks>
/// When all three current-user-only channel variables supplied by the invocation are absent, the
/// helper awaits the owned task or root process normally and assumes no receipt capability. If any
/// channel variable is present, all three must be valid and the helper requires a begin acknowledgement
/// before invoking work. The channel is carried in
/// <c>APPSURFACE_EVIDENCE_CHILD_PIPE</c>, <c>APPSURFACE_EVIDENCE_CHILD_EXECUTION_ID</c>, and
/// <c>APPSURFACE_EVIDENCE_CHILD_NONCE</c>. It completes enrollment only after the supplied task
/// has actually completed. Workloads must enroll every asynchronous or process descendant before
/// starting it and must await its real cleanup. This is a trusted cooperative promise, not OS
/// containment; unregistered, detached, or externally managed work is not proven settled. Do not
/// log or include the run-specific channel values in keys, artifacts, or diagnostics.
/// </remarks>
public static class EvidenceOwnedChild
{
    /// <summary>Maximum encoded protocol frame size.</summary>
    public const int MaximumFrameBytes = 4096;

    /// <summary>Maximum number of child enrollments in one invocation session.</summary>
    public const int MaximumChildren = 64;

    private const string PipeVariable = "APPSURFACE_EVIDENCE_CHILD_PIPE";
    private const string ExecutionVariable = "APPSURFACE_EVIDENCE_CHILD_EXECUTION_ID";
    private const string NonceVariable = "APPSURFACE_EVIDENCE_CHILD_NONCE";

    /// <summary>Enrolls before invoking <paramref name="work"/> and reports completion after its task settles.</summary>
    /// <param name="work">Cooperative work that must await all child work and cleanup it owns.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller; work remains owned until its task actually settles.</param>
    /// <returns>A task that completes after enrollment is acknowledged and the work and completion message settle.</returns>
    /// <exception cref="InvalidOperationException">A partially configured, invalid, unreachable, closed, or full invocation channel is present. The work callback has not been invoked.</exception>
    public static async Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        => await RunAsync(work, GetProcessChannelEnvironment(), cancellationToken).ConfigureAwait(false);

    /// <summary>Runs work with an explicitly supplied cooperative invocation channel, or normally when it is absent.</summary>
    /// <param name="work">Cooperative work that must await all child work and cleanup it owns.</param>
    /// <param name="environment">Invocation environment values; only the three documented child-channel keys are read.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller; work remains owned until its task actually settles.</param>
    /// <returns>A task that completes after optional enrollment acknowledgement and actual work settlement.</returns>
    /// <exception cref="InvalidOperationException">Any channel key is present but the channel is incomplete, invalid, unreachable, closed, or full. The work callback has not been invoked.</exception>
    public static async Task RunAsync(Func<CancellationToken, Task> work, IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(environment);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetChannel(environment, out var pipeName, out var executionId, out var nonce))
        {
            await work(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        await using var enrollment = await EnrollAsync(pipeName!, executionId!, nonce!, cancellationToken).ConfigureAwait(false);
        Exception? workFailure = null;
        try { await work(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) { workFailure = exception; }

        Exception? completionFailure = null;
        try { await enrollment.CompleteAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { completionFailure = exception; }
        if (workFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(workFailure).Throw();
        if (completionFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(completionFailure).Throw();
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Starts a process only after enrollment acknowledgement and reports its actual root-process exit.</summary>
    /// <param name="startInfo">The process to start. Its environment inherits the cooperative channel variables.</param>
    /// <param name="cancellationToken">Cancellation requests termination of the owned root process and joins its actual exit.</param>
    /// <returns>The root process exit code after the completion message is acknowledged.</returns>
    /// <remarks>
    /// Descendants must independently enroll through this protocol; killing or observing the root does not settle them.
    /// Redirected standard output and error are drained to prevent a pipe deadlock; redirected standard input is closed.
    /// This API returns only the exit code and does not expose redirected stream contents.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A partially configured, invalid, unreachable, closed, or full invocation channel is present. The process has not been started.</exception>
    public static async Task<int> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        => await RunProcessAsync(startInfo, GetProcessChannelEnvironment(), cancellationToken).ConfigureAwait(false);

    /// <summary>Starts a process with an explicitly supplied cooperative channel and joins its actual root-process exit.</summary>
    /// <param name="startInfo">The process to start; an owned copy receives the supplied channel values.</param>
    /// <param name="environment">Invocation environment values; only the three documented child-channel keys are read.</param>
    /// <param name="cancellationToken">Cancellation requests termination of the owned root process and joins its actual exit.</param>
    /// <returns>The root process exit code after optional enrollment completion acknowledgement.</returns>
    /// <exception cref="InvalidOperationException">Any channel key is present but the channel is incomplete, invalid, unreachable, closed, or full. The process has not been started.</exception>
    public static async Task<int> RunProcessAsync(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(environment);
        cancellationToken.ThrowIfCancellationRequested();
        var enrolled = TryGetChannel(environment, out var pipeName, out var executionId, out var nonce);
        if (enrolled)
        {
            if (startInfo.UseShellExecute || !string.IsNullOrEmpty(startInfo.UserName))
                throw new ArgumentException("Cooperative child processes must use the current user and UseShellExecute=false.", nameof(startInfo));
            if (OperatingSystem.IsWindows() && (!string.IsNullOrEmpty(startInfo.Domain) || startInfo.Password is not null))
                throw new ArgumentException("Cooperative child processes must use the current user and UseShellExecute=false.", nameof(startInfo));
        }
        Enrollment? enrollment = enrolled ? await EnrollAsync(pipeName!, executionId!, nonce!, cancellationToken).ConfigureAwait(false) : null;
        await using var ownedEnrollment = enrollment;
        var ownedStartInfo = Clone(startInfo);
        if (enrolled)
        {
            ownedStartInfo.Environment[PipeVariable] = pipeName;
            ownedStartInfo.Environment[ExecutionVariable] = executionId;
            ownedStartInfo.Environment[NonceVariable] = nonce;
        }
        else
        {
            ownedStartInfo.Environment.Remove(PipeVariable);
            ownedStartInfo.Environment.Remove(ExecutionVariable);
            ownedStartInfo.Environment.Remove(NonceVariable);
        }
        using var process = new Process { StartInfo = ownedStartInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The enrolled child process did not start.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (enrollment is not null) await enrollment.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (ownedStartInfo.RedirectStandardInput) process.StandardInput.Close();
        var stdoutDrain = ownedStartInfo.RedirectStandardOutput ? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null) : Task.CompletedTask;
        var stderrDrain = ownedStartInfo.RedirectStandardError ? process.StandardError.BaseStream.CopyToAsync(Stream.Null) : Task.CompletedTask;

        var cancelled = false;
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false);

        var exitCode = process.ExitCode;
        if (enrollment is not null) await enrollment.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        if (cancelled) throw new OperationCanceledException(cancellationToken);
        return exitCode;
    }

    private static IReadOnlyDictionary<string, string> GetProcessChannelEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { PipeVariable, ExecutionVariable, NonceVariable })
            if (System.Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        return environment;
    }

    private static ProcessStartInfo Clone(ProcessStartInfo source)
    {
        var clone = new ProcessStartInfo
        {
            FileName = source.FileName,
            Arguments = source.Arguments,
            WorkingDirectory = source.WorkingDirectory,
            Verb = source.Verb,
            UseShellExecute = source.UseShellExecute,
            CreateNoWindow = source.CreateNoWindow,
            WindowStyle = source.WindowStyle,
            ErrorDialog = source.ErrorDialog,
            ErrorDialogParentHandle = source.ErrorDialogParentHandle,
            RedirectStandardInput = source.RedirectStandardInput,
            RedirectStandardOutput = source.RedirectStandardOutput,
            RedirectStandardError = source.RedirectStandardError,
            StandardOutputEncoding = source.StandardOutputEncoding,
            StandardErrorEncoding = source.StandardErrorEncoding,
            UserName = source.UserName,
        };
        clone.Environment.Clear();
        if (OperatingSystem.IsWindows())
        {
            clone.LoadUserProfile = source.LoadUserProfile;
            clone.Domain = source.Domain;
            clone.Password = source.Password;
        }
        foreach (var argument in source.ArgumentList) clone.ArgumentList.Add(argument);
        foreach (var (key, value) in source.Environment) clone.Environment[key] = value;
        return clone;
    }

    private static bool TryGetChannel(IReadOnlyDictionary<string, string> environment,
        out string? pipeName, out string? executionId, out string? nonce)
    {
        environment.TryGetValue(PipeVariable, out pipeName);
        environment.TryGetValue(ExecutionVariable, out executionId);
        environment.TryGetValue(NonceVariable, out nonce);
        if (pipeName is null && executionId is null && nonce is null) return false;
        if (!IsProtocolToken(pipeName, 180) || !IsProtocolToken(executionId, 128) || !IsProtocolToken(nonce, 128))
            throw new InvalidOperationException("Cooperative child enrollment requires a valid current-user Evidence invocation channel.");
        return true;
    }

    private static async Task<Enrollment> EnrollAsync(string pipeName, string executionId, string nonce, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
            await WriteFrameAsync(pipe, $"BEGIN|{executionId}|{nonce}", cancellationToken).ConfigureAwait(false);
            var response = await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (response is null || !response.StartsWith("ACK|", StringComparison.Ordinal)
                || !Guid.TryParseExact(response.AsSpan(4), "N", out var childId))
                throw new InvalidOperationException("Cooperative child enrollment was rejected or the invocation channel was unavailable.");
            return new Enrollment(pipe, childId);
        }
        catch (InvalidOperationException) { pipe.Dispose(); throw; }
        catch (OperationCanceledException) { pipe.Dispose(); throw; }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or ArgumentException)
        {
            pipe.Dispose();
            throw new InvalidOperationException("Cooperative child enrollment could not connect to the current-user Evidence invocation channel.");
        }
    }

    private static bool IsProtocolToken(string? value, int maximumLength)
        => value is { Length: > 0 } && value.Length <= maximumLength
            && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static async Task WriteFrameAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\n");
        if (bytes.Length > MaximumFrameBytes) throw new InvalidOperationException("Cooperative child protocol frame exceeds its limit.");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[MaximumFrameBytes];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) return length == 0 ? null : throw new IOException("Truncated cooperative child protocol frame.");
            if (bytes[length++] == (byte)'\n') return Encoding.UTF8.GetString(bytes, 0, length - 1);
        }
        throw new IOException("Cooperative child protocol frame exceeds its limit.");
    }

    private sealed class Enrollment(NamedPipeClientStream pipe, Guid childId) : IAsyncDisposable
    {
        private bool _completed;

        internal async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (_completed) return;
            _completed = true;
            try
            {
                await WriteFrameAsync(pipe, $"COMPLETE|{childId:N}", cancellationToken).ConfigureAwait(false);
                var response = await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(response, $"DONE|{childId:N}", StringComparison.Ordinal))
                    throw new InvalidOperationException("The invocation did not acknowledge cooperative child completion.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                throw new InvalidOperationException("The invocation channel was lost before cooperative child completion was acknowledged.");
            }
        }

        public ValueTask DisposeAsync()
        {
            pipe.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
