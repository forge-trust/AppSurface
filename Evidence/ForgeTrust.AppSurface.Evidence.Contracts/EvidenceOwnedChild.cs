using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceOwnedChildWire;

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

    private const string StartupCompletionFailureDataKey = "EvidenceOwnedChild.StartupCompletionFailure";
    private static readonly TimeSpan ProtocolIoDeadline = TimeSpan.FromSeconds(5);

    /// <summary>Enrolls before invoking <paramref name="work"/> and reports completion after its task settles.</summary>
    /// <param name="work">Cooperative work that must await all child work and cleanup it owns.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller; work remains owned until its task actually settles.</param>
    /// <returns>A task that completes after enrollment is acknowledged and the work and completion message settle.</returns>
    /// <exception cref="InvalidOperationException">A partially configured, invalid, unreachable, closed, or full invocation channel is present. The work callback has not been invoked.</exception>
    public static async Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        => await RunAsync(work, EvidenceOwnedChildChannel.ReadAmbientEnvironment(), cancellationToken).ConfigureAwait(false);

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
        var channel = EvidenceOwnedChildChannel.Read(environment);
        if (channel is null)
        {
            await work(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        await using var enrollment = await EnrollAsync(channel, cancellationToken).ConfigureAwait(false);
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
    /// Redirected standard output and error are drained while the root runs; redirected standard input is closed.
    /// After caller cancellation, pending drains are canceled and joined after the root exits, so descendants holding
    /// inherited output handles do not block this call. Independently enrolled descendants remain active in the invocation session.
    /// This API returns only the exit code and does not expose redirected stream contents.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A partially configured, invalid, unreachable, closed, or full invocation channel is present. The process has not been started.</exception>
    public static async Task<int> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        => await RunProcessAsync(startInfo, EvidenceOwnedChildChannel.ReadAmbientEnvironment(), cancellationToken).ConfigureAwait(false);

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
        var channel = EvidenceOwnedChildChannel.Read(environment);
        if (channel is not null)
        {
            if (startInfo.UseShellExecute || !string.IsNullOrEmpty(startInfo.UserName))
                throw new ArgumentException("Cooperative child processes must use the current user and UseShellExecute=false.", nameof(startInfo));
            if (OperatingSystem.IsWindows() && (!string.IsNullOrEmpty(startInfo.Domain) || startInfo.Password is not null))
                throw new ArgumentException("Cooperative child processes must use the current user and UseShellExecute=false.", nameof(startInfo));
        }
        var enrollment = channel is not null ? await EnrollAsync(channel, cancellationToken).ConfigureAwait(false) : null;
        await using var ownedEnrollment = enrollment;
        var copyEnvironment = !OperatingSystem.IsWindows() || !startInfo.UseShellExecute || channel is not null;
        var ownedStartInfo = Clone(startInfo, copyEnvironment);
        if (channel is not null)
            channel.ApplyTo(ownedStartInfo.Environment);
        else if (copyEnvironment)
            EvidenceOwnedChildChannel.RemoveFrom(ownedStartInfo.Environment);
        using var process = new Process { StartInfo = ownedStartInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The enrolled child process did not start.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (enrollment is not null)
            {
                try { await enrollment.CompleteAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException completionFailure)
                {
                    exception.Data[StartupCompletionFailureDataKey] = completionFailure;
                }
            }
            throw;
        }

        if (ownedStartInfo.RedirectStandardInput) process.StandardInput.Close();
        using var drainCancellation = new CancellationTokenSource();
        var stdoutDrain = ownedStartInfo.RedirectStandardOutput
            ? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, drainCancellation.Token)
            : Task.CompletedTask;
        var stderrDrain = ownedStartInfo.RedirectStandardError
            ? process.StandardError.BaseStream.CopyToAsync(Stream.Null, drainCancellation.Token)
            : Task.CompletedTask;

        var cancelled = false;
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            drainCancellation.Cancel();
        }

        // The actual root is joined. Cancellation must still reach drains when it arrives after root exit.
        using var drainRegistration = cancellationToken.Register(drainCancellation.Cancel);
        try { await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && drainCancellation.IsCancellationRequested)
        { cancelled = true; }
        cancelled |= cancellationToken.IsCancellationRequested;

        var exitCode = process.ExitCode;
        if (enrollment is not null) await enrollment.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        if (cancelled) throw new OperationCanceledException(cancellationToken);
        return exitCode;
    }

    private static ProcessStartInfo Clone(ProcessStartInfo source, bool copyEnvironment)
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
        if (copyEnvironment) clone.Environment.Clear();
        if (OperatingSystem.IsWindows())
        {
            clone.LoadUserProfile = source.LoadUserProfile;
            clone.Domain = source.Domain;
            clone.Password = source.Password;
        }
        foreach (var argument in source.ArgumentList) clone.ArgumentList.Add(argument);
        if (copyEnvironment)
            foreach (var (key, value) in source.Environment) clone.Environment[key] = value;
        return clone;
    }

    private static async Task<Enrollment> EnrollAsync(EvidenceOwnedChildChannel channel, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", channel.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var protocolDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        protocolDeadline.CancelAfter(ProtocolIoDeadline);
        try
        {
            await pipe.ConnectAsync((int)ProtocolIoDeadline.TotalMilliseconds, protocolDeadline.Token).ConfigureAwait(false);
            await WriteFrameAsync(pipe, FormatBegin(channel.ExecutionId, channel.Nonce), protocolDeadline.Token).ConfigureAwait(false);
            var response = await ReadFrameAsync(pipe, protocolDeadline.Token).ConfigureAwait(false);
            if (!TryParseAcknowledgement(response, out var childId))
                throw new InvalidOperationException("Cooperative child enrollment was rejected or the invocation channel was unavailable.");
            return new Enrollment(pipe, childId);
        }
        catch (InvalidOperationException) { pipe.Dispose(); throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            pipe.Dispose();
            throw new InvalidOperationException("Cooperative child enrollment did not complete before its protocol deadline.");
        }
        catch (OperationCanceledException) { pipe.Dispose(); throw; }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or ArgumentException)
        {
            pipe.Dispose();
            throw new InvalidOperationException("Cooperative child enrollment could not connect to the current-user Evidence invocation channel.");
        }
    }

    private static Task WriteFrameAsync(Stream stream, string value, CancellationToken cancellationToken) =>
        EvidenceOwnedChildWire.WriteFrameAsync(stream, value, cancellationToken,
            static () => new InvalidOperationException("Cooperative child protocol frame exceeds its limit."));

    private sealed class Enrollment(NamedPipeClientStream pipe, Guid childId) : IAsyncDisposable
    {
        private bool _completed;

        internal async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (_completed) return;
            _completed = true;
            using var protocolDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            protocolDeadline.CancelAfter(ProtocolIoDeadline);
            try
            {
                await WriteFrameAsync(pipe, FormatCompletion(childId), protocolDeadline.Token).ConfigureAwait(false);
                var response = await ReadFrameAsync(pipe, protocolDeadline.Token).ConfigureAwait(false);
                if (!IsDoneAcknowledgement(response, childId))
                    throw new InvalidOperationException("The invocation did not acknowledge cooperative child completion.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("The invocation channel did not acknowledge cooperative child completion before its protocol deadline.");
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
