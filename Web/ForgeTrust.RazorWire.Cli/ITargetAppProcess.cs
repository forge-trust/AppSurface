using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CliWrap;
using CliWrap.Exceptions;
using CliCommand = CliWrap.Cli;
using CliCommandResult = CliWrap.CommandResult;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Represents a started or startable external target application process.
/// </summary>
/// <remarks>
/// The wrapper is a supervised long-running process boundary for export targets,
/// not a general command runner. Implementations raise non-empty output lines as
/// they arrive, raise <see cref="Exited"/> after observable output has drained
/// when possible, and treat disposal as best-effort process-tree cleanup.
/// </remarks>
public interface ITargetAppProcess : IAsyncDisposable
{
    /// <summary>
    /// Raised when a non-empty stdout line is received.
    /// </summary>
    event Action<string>? OutputLineReceived;

    /// <summary>
    /// Raised when a non-empty stderr line is received.
    /// </summary>
    event Action<string>? ErrorLineReceived;

    /// <summary>
    /// Raised when the process exits.
    /// </summary>
    event Action? Exited;

    /// <summary>
    /// Gets a value indicating whether the process has exited.
    /// </summary>
    /// <remarks>
    /// This value reflects the wrapper lifecycle, not only operating-system
    /// process liveness. It returns <see langword="true"/> before
    /// <see cref="Start"/> is called, while completion is being observed, and
    /// after disposal. It returns <see langword="false"/> only while the target
    /// app is actively running. Callers that need to distinguish
    /// not-started, running, completing, and disposed states should use the
    /// surrounding lifecycle ordering instead of treating this as a simple
    /// liveness probe.
    /// </remarks>
    bool HasExited { get; }

    /// <summary>
    /// Starts the process and begins asynchronous output capture.
    /// </summary>
    /// <remarks>
    /// Calling <see cref="Start"/> more than once is invalid. Startup failures
    /// that occur after the command task is created are surfaced through
    /// <see cref="ErrorLineReceived"/> followed by <see cref="Exited"/> so
    /// callers waiting for a listening URL can report the underlying launch
    /// failure instead of a generic readiness timeout.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the process has already been started.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the wrapper has already been disposed.</exception>
    void Start();

    /// <summary>
    /// Performs best-effort asynchronous cleanup of the started target process.
    /// </summary>
    /// <remarks>
    /// Cleanup order is:
    /// <list type="number">
    /// <item><description>Check <see cref="HasExited"/> when startup completed.</description></item>
    /// <item><description>If the process is still running, cancel the CliWrap command task. CliWrap translates cancellation into process-tree termination for the launched command.</description></item>
    /// <item><description>Wait up to 5 seconds for the command task and output pipes to complete before returning.</description></item>
    /// </list>
    /// Pitfalls:
    /// <list type="bullet">
    /// <item><description>Short-lived processes can exit before callers observe their output callbacks, so <see cref="Exited"/> is raised only after the command task completes and output pipes have drained when possible.</description></item>
    /// <item><description>Cleanup swallows <see cref="InvalidOperationException"/>, timeout-driven <see cref="OperationCanceledException"/>, <see cref="ObjectDisposedException"/>, and recoverable command-task exceptions such as <see cref="CliWrapException"/>, <see cref="Win32Exception"/>, or <see cref="NotSupportedException"/> as part of best-effort disposal.</description></item>
    /// <item><description>Callers must not rely on guaranteed process termination; disposal can return after the 5-second timeout even if the operating system process has not fully exited.</description></item>
    /// </list>
    /// </remarks>
    /// <returns>A task that completes after cleanup work finishes.</returns>
    new ValueTask DisposeAsync();
}

/// <summary>
/// Creates <see cref="ITargetAppProcess"/> instances for launch specifications.
/// </summary>
public interface ITargetAppProcessFactory
{
    /// <summary>
    /// Creates a new process wrapper for the provided launch spec.
    /// </summary>
    /// <param name="spec">The process launch specification.</param>
    /// <returns>A process wrapper ready to start.</returns>
    ITargetAppProcess Create(ProcessLaunchSpec spec);
}

/// <summary>
/// Default <see cref="ITargetAppProcessFactory"/> implementation.
/// </summary>
public sealed class TargetAppProcessFactory : ITargetAppProcessFactory
{
    /// <inheritdoc />
    public ITargetAppProcess Create(ProcessLaunchSpec spec) => new TargetAppProcess(spec);
}

internal sealed class TargetAppProcess : ITargetAppProcess
{
    private const int ProcessExitTimeoutMilliseconds = 5000;

    private readonly ProcessLaunchSpec _spec;
    private readonly TargetAppProcessHooks? _hooks;
    private readonly Process? _hookProcess;
    private readonly object _gate = new();
    private CommandTask<CliCommandResult>? _commandTask;
    private CancellationTokenSource? _processCancellation;
    private TargetAppProcessState _state;
    private bool _exitedRaised;

    public event Action<string>? OutputLineReceived;
    public event Action<string>? ErrorLineReceived;
    public event Action? Exited;

    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                if (_state != TargetAppProcessState.Running)
                {
                    return true;
                }

                if (_commandTask is not null)
                {
                    return _commandTask.Task.IsCompleted;
                }
            }

            if (_hookProcess is null)
            {
                return false;
            }

            try
            {
                return GetHasExited(_hookProcess);
            }
            catch (Exception ex) when (IsBestEffortCleanupException(ex))
            {
                return true;
            }
        }
    }

    public TargetAppProcess(ProcessLaunchSpec spec)
        : this(spec, hooks: null, process: null, started: false)
    {
    }

    /// <summary>
    /// Initializes a process wrapper with optional hook overrides for deterministic tests.
    /// </summary>
    /// <param name="spec">The process launch specification used to build the default <see cref="ProcessStartInfo"/>.</param>
    /// <param name="hooks">
    /// Optional cleanup hooks that override exit checks, kill behavior, and wait behavior. Use this only in tests that
    /// need to simulate cleanup edge cases such as kill failures or timeout behavior without relying on OS-specific
    /// process semantics.
    /// </param>
    /// <param name="process">
    /// Optional process instance to wrap instead of constructing a new one. When supplied in an unstarted state, this
    /// wrapper applies the launch spec's start info and event subscriptions. Already-started injected processes keep
    /// their existing launch configuration so tests can wrap a live associated process without mutating it.
    /// </param>
    /// <param name="started">
    /// Whether the wrapped process should be treated as already started when the wrapper is created. Tests can use this
    /// to exercise <see cref="DisposeAsync"/> without launching a real child process.
    /// </param>
    internal TargetAppProcess(
        ProcessLaunchSpec spec,
        TargetAppProcessHooks? hooks,
        Process? process = null,
        bool started = false)
    {
        ArgumentNullException.ThrowIfNull(spec);

        _spec = spec;
        _hookProcess = process;
        if (process is not null && !started)
        {
            process.StartInfo = BuildStartInfo(spec);
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    OutputLineReceived?.Invoke(args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    ErrorLineReceived?.Invoke(args.Data);
                }
            };
            process.Exited += (_, _) => RaiseExitedOnce();
        }

        _hooks = hooks;
        _state = started ? TargetAppProcessState.Running : TargetAppProcessState.NotStarted;
    }

    public void Start()
    {
        if (_hooks?.StartOverride is { } startOverride)
        {
            lock (_gate)
            {
                ThrowIfCannotStart();
                _state = TargetAppProcessState.Running;
            }

            startOverride(this);
            return;
        }

        CancellationTokenSource? cancellation = null;
        CommandTask<CliCommandResult>? commandTask = null;
        var launchAttempted = false;
        try
        {
            lock (_gate)
            {
                ThrowIfCannotStart();
                cancellation = new CancellationTokenSource();
                _processCancellation = cancellation;
                launchAttempted = true;
                commandTask = BuildCliWrapCommand().ExecuteAsync(cancellation.Token);
                _commandTask = commandTask;
                _state = TargetAppProcessState.Running;
            }

            _ = ObserveCompletionAsync(commandTask, cancellation.Token);
        }
        catch (Exception ex) when (launchAttempted && IsStartupFailureException(ex))
        {
            cancellation?.Dispose();
            MarkCompletingAfterStartupFailure();
            RaiseErrorLineForProcessFailure(ex);
            RaiseExitedOnce();
        }
    }

    internal void RaiseOutputLineForTesting(string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            OutputLineReceived?.Invoke(line);
        }
    }

    internal void RaiseErrorLineForTesting(string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            ErrorLineReceived?.Invoke(line);
        }
    }

    internal void RaiseExitedForTesting()
    {
        RaiseExitedOnce();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        CommandTask<CliCommandResult>? commandTask;
        CancellationTokenSource? cancellation;
        Process? hookProcess;
        TargetAppProcessState previousState;

        lock (_gate)
        {
            if (_state == TargetAppProcessState.Disposed)
            {
                return;
            }

            previousState = _state;
            _state = TargetAppProcessState.Disposed;
            commandTask = _commandTask;
            cancellation = _processCancellation;
            hookProcess = _hookProcess;
        }

        using var cancellationToDispose = cancellation;
        using var commandTaskToDispose = commandTask;
        using var hookProcessToDispose = hookProcess;

        if (commandTask is not null && previousState == TargetAppProcessState.Running)
        {
            await DisposeCliWrapProcessAsync(commandTask, cancellation);
        }
        else if (hookProcess is not null || _hooks is not null)
        {
            await DisposeHookProcessAsync(hookProcess, previousState == TargetAppProcessState.Running);
        }
    }

    private Command BuildCliWrapCommand()
    {
        var command = CliCommand.Wrap(_spec.FileName)
            .WithArguments(_spec.Arguments)
            .WithWorkingDirectory(_spec.WorkingDirectory)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.Create(
                (stream, cancellationToken) => ReadProcessLinesAsync(stream, RaiseOutputLineForTesting, cancellationToken)))
            .WithStandardErrorPipe(PipeTarget.Create(
                (stream, cancellationToken) => ReadProcessLinesAsync(stream, RaiseErrorLineForTesting, cancellationToken)));

        if (_spec.EnvironmentOverrides.Count > 0)
        {
            command = command.WithEnvironmentVariables(
                _spec.EnvironmentOverrides.ToDictionary(
                    pair => pair.Key,
                    pair => (string?)pair.Value,
                    StringComparer.Ordinal));
        }

        return command;
    }

    private static ProcessStartInfo BuildStartInfo(ProcessLaunchSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in spec.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var env in spec.EnvironmentOverrides)
        {
            startInfo.Environment[env.Key] = env.Value;
        }

        return startInfo;
    }

    internal static async Task ReadProcessLinesAsync(
        Stream stream,
        Action<string> lineReceived,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Console.OutputEncoding,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        var pending = new StringBuilder();
        var buffer = new char[1024];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var current = buffer[index];
                if (current == '\r')
                {
                    EmitPendingLine(pending, lineReceived);
                    if (index + 1 < read && buffer[index + 1] == '\n')
                    {
                        index++;
                    }

                    continue;
                }

                if (current == '\n')
                {
                    EmitPendingLine(pending, lineReceived);
                    continue;
                }

                pending.Append(current);
            }
        }

        if (pending.Length > 0)
        {
            EmitPendingLine(pending, lineReceived);
        }
    }

    private static void EmitPendingLine(StringBuilder pending, Action<string> lineReceived)
    {
        if (pending.Length > 0 && pending[^1] == '\r')
        {
            pending.Length--;
        }

        var line = pending.ToString();
        pending.Clear();
        if (!string.IsNullOrWhiteSpace(line))
        {
            lineReceived(line);
        }
    }

    private async Task ObserveCompletionAsync(
        CommandTask<CliCommandResult> commandTask,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await commandTask.Task;
            if (result.ExitCode != 0)
            {
                RaiseErrorLineForProcessExitCode(result.ExitCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposal requested process-tree termination.
        }
        catch (OperationCanceledException ex)
        {
            RaiseErrorLineForProcessFailure(ex);
        }
        catch (CommandExecutionException ex)
        {
            RaiseErrorLineForProcessFailure(ex);
        }
        catch (CliWrapException ex)
        {
            RaiseErrorLineForProcessFailure(ex);
        }
        catch (Win32Exception ex)
        {
            RaiseErrorLineForProcessFailure(ex);
        }
        catch (InvalidOperationException ex)
        {
            RaiseErrorLineForProcessFailure(ex);
        }
        catch (Exception ex) when (IsNonFatalException(ex))
        {
            // ObserveCompletionAsync is intentionally fire-and-forget; convert unexpected failures into diagnostics.
            RaiseErrorLineForProcessFailure(ex);
        }
        finally
        {
            MarkCompleting();
            RaiseExitedOnce();
        }
    }

    private async Task DisposeCliWrapProcessAsync(
        CommandTask<CliCommandResult> commandTask,
        CancellationTokenSource? cancellation)
    {
        if (!commandTask.Task.IsCompleted && cancellation is not null)
        {
            await cancellation.CancelAsync();
        }

        try
        {
            await commandTask.Task.WaitAsync(TimeSpan.FromMilliseconds(ProcessExitTimeoutMilliseconds));
        }
        catch (Exception ex) when (IsBestEffortCleanupException(ex))
        {
            // Best-effort cleanup should not hide the export error that caused disposal.
        }
    }

    private async Task DisposeHookProcessAsync(Process? hookProcess, bool started)
    {
        if (!started)
        {
            return;
        }

        var exitObserved = false;
        try
        {
            exitObserved = hookProcess is null || GetHasExited(hookProcess);
        }
        catch (Exception ex) when (IsBestEffortCleanupException(ex))
        {
            // The process is no longer associated with a running process, or state retrieval is unavailable.
            exitObserved = ex is InvalidOperationException or ObjectDisposedException;
        }

        if (!exitObserved)
        {
            try
            {
                KillProcessTree(hookProcess);
            }
            catch (Exception ex) when (IsBestEffortCleanupException(ex))
            {
                // The process may have exited between checks or kill may be unsupported.
                exitObserved = ex is InvalidOperationException;
            }

            if (!exitObserved)
            {
                try
                {
                    using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await WaitForProcessExitAsync(hookProcess, waitCts.Token);
                    exitObserved = true;
                }
                catch (Exception ex) when (IsBestEffortCleanupException(ex))
                {
                    // The wait may have timed out, the process may have exited concurrently, or waiting may be unsupported.
                    exitObserved = ex is InvalidOperationException;
                }
            }
        }

        if (exitObserved)
        {
            try
            {
                // WaitForExit flushes redirected stdout/stderr callbacks for short-lived hook processes
                // before the underlying Process is disposed.
                FlushProcessOutput(hookProcess);
            }
            catch (Exception ex) when (IsBestEffortCleanupException(ex))
            {
                // The process is no longer associated with an underlying OS process, or flush is unsupported.
            }
        }
    }

    private bool GetHasExited(Process process) => _hooks?.HasExitedOverride?.Invoke(process) ?? process.HasExited;

    private void KillProcessTree(Process? process)
    {
        if (process is null)
        {
            return;
        }

        if (_hooks?.KillProcessOverride is { } killProcessOverride)
        {
            killProcessOverride(process);
            return;
        }

        process.Kill(entireProcessTree: true);
    }

    private Task WaitForProcessExitAsync(Process? process, CancellationToken cancellationToken)
    {
        if (process is null)
        {
            return Task.CompletedTask;
        }

        return _hooks?.WaitForExitAsyncOverride?.Invoke(process, cancellationToken)
               ?? process.WaitForExitAsync(cancellationToken);
    }

    private void FlushProcessOutput(Process? process)
    {
        if (process is null)
        {
            return;
        }

        if (_hooks?.WaitForExitOverride is { } waitForExitOverride)
        {
            waitForExitOverride(process);
            return;
        }

        process.WaitForExit();
    }

    private void MarkCompleting()
    {
        lock (_gate)
        {
            if (_state == TargetAppProcessState.Running)
            {
                _state = TargetAppProcessState.Completing;
            }
        }
    }

    private void MarkCompletingAfterStartupFailure()
    {
        lock (_gate)
        {
            if (_state != TargetAppProcessState.Disposed)
            {
                _state = TargetAppProcessState.Completing;
            }
        }
    }

    private void ThrowIfCannotStart()
    {
        if (_state == TargetAppProcessState.Disposed)
        {
            throw new ObjectDisposedException(nameof(TargetAppProcess));
        }

        if (_state != TargetAppProcessState.NotStarted)
        {
            throw new InvalidOperationException("Target application process has already been started.");
        }
    }

    private void RaiseErrorLineForProcessExitCode(int exitCode)
    {
        RaiseErrorLineForTesting($"Target application process exited with code {exitCode} before export completed.");
    }

    private void RaiseErrorLineForProcessFailure(Exception exception)
    {
        RaiseErrorLineForTesting($"Target application process failed to start or complete: {exception.Message}");
    }

    private void RaiseExitedOnce()
    {
        lock (_gate)
        {
            if (_exitedRaised)
            {
                return;
            }

            _exitedRaised = true;
        }

        Exited?.Invoke();
    }

    private static bool IsBestEffortCleanupException(Exception exception)
    {
        return exception is InvalidOperationException
            or OperationCanceledException
            or TimeoutException
            or ObjectDisposedException
            or CliWrapException
            or Win32Exception
            or NotSupportedException;
    }

    private static bool IsStartupFailureException(Exception exception)
    {
        return exception is CliWrapException
            or Win32Exception
            or InvalidOperationException
            or FileNotFoundException
            or DirectoryNotFoundException;
    }

    private static bool IsNonFatalException(Exception exception)
    {
        return exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException;
    }

    private enum TargetAppProcessState
    {
        NotStarted,
        Running,
        Completing,
        Disposed
    }
}

/// <summary>
/// Optional process-operation overrides for <see cref="TargetAppProcess"/> tests.
/// </summary>
/// <remarks>
/// These hooks exist so tests can force cleanup branches such as unsupported kill operations, synthetic exit states,
/// or timeout handling without reflection or fragile platform-dependent child processes.
/// </remarks>
internal sealed class TargetAppProcessHooks
{
    /// <summary>
    /// Gets or sets an optional start override used in place of <see cref="Process.Start()"/> and output-reader setup.
    /// </summary>
    /// <remarks>
    /// Tests can use this to deterministically raise <see cref="ITargetAppProcess.Exited"/> and
    /// <see cref="ITargetAppProcess.OutputLineReceived"/> in a controlled order without depending on operating-system
    /// process timing.
    /// </remarks>
    public Action<TargetAppProcess>? StartOverride { get; init; }

    /// <summary>
    /// Gets or sets an optional exit-state override used in place of <see cref="Process.HasExited"/>.
    /// </summary>
    public Func<Process, bool>? HasExitedOverride { get; init; }

    /// <summary>
    /// Gets or sets an optional kill override used in place of <see cref="Process.Kill(bool)"/>.
    /// </summary>
    public Action<Process>? KillProcessOverride { get; init; }

    /// <summary>
    /// Gets or sets an optional asynchronous wait override used in place of <see cref="Process.WaitForExitAsync(CancellationToken)"/>.
    /// </summary>
    public Func<Process, CancellationToken, Task>? WaitForExitAsyncOverride { get; init; }

    /// <summary>
    /// Gets or sets an optional synchronous wait override used in place of <see cref="Process.WaitForExit()"/>.
    /// </summary>
    public Action<Process>? WaitForExitOverride { get; init; }
}
