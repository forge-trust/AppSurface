using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Represents a started or startable external target application process.
/// </summary>
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
    bool HasExited { get; }

    /// <summary>
    /// Starts the process and begins asynchronous output capture.
    /// </summary>
    void Start();

    /// <summary>
    /// Performs best-effort asynchronous cleanup of the started target process.
    /// </summary>
    /// <remarks>
    /// Cleanup order is:
    /// <list type="number">
    /// <item><description>Check <see cref="HasExited"/> against the underlying process when startup completed.</description></item>
    /// <item><description>If the process is still running, issue a best-effort <c>Kill(entireProcessTree: true)</c> and then wait up to 5 seconds for exit, even when kill throws a recoverable cleanup exception.</description></item>
    /// <item><description>After exit is observed, call <c>WaitForExit()</c> to flush redirected stdout and stderr callbacks before returning.</description></item>
    /// </list>
    /// Pitfalls:
    /// <list type="bullet">
    /// <item><description>Short-lived processes can exit before their output callbacks are delivered, so disposal performs the final flush step to improve callback delivery timing.</description></item>
    /// <item><description>Cleanup swallows <see cref="InvalidOperationException"/>, timeout-driven <see cref="OperationCanceledException"/>, <see cref="ObjectDisposedException"/>, and recoverable kill, wait, or flush exceptions such as <see cref="Win32Exception"/> or <see cref="NotSupportedException"/> as part of best-effort disposal.</description></item>
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

[ExcludeFromCodeCoverage]
internal sealed class TargetAppProcess : ITargetAppProcess
{
    private const int ProcessExitTimeoutMilliseconds = 5000;

    private readonly Process _process;
    private readonly TargetAppProcessHooks? _hooks;
    private bool _started;
    private bool _disposed;

    public event Action<string>? OutputLineReceived;
    public event Action<string>? ErrorLineReceived;
    public event Action? Exited;

    public bool HasExited => !_started || _process.HasExited;

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

        _process = process ?? new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (process is not null && !started)
        {
            _process.StartInfo = startInfo;
            _process.EnableRaisingEvents = true;
        }

        _hooks = hooks;
        _started = started;
        _process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                OutputLineReceived?.Invoke(args.Data);
            }
        };
        _process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                ErrorLineReceived?.Invoke(args.Data);
            }
        };
        _process.Exited += (_, _) => Exited?.Invoke();
    }

    public void Start()
    {
        if (_hooks?.StartOverride is { } startOverride)
        {
            _started = true;
            startOverride(this);
            return;
        }

        _process.Start();
        _started = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
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
        Exited?.Invoke();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_started)
            {
                var exitObserved = false;
                try
                {
                    exitObserved = GetHasExited();
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
                        KillProcessTree();
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
                            await WaitForProcessExitAsync(waitCts.Token);
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
                        // WaitForExit flushes redirected stdout/stderr callbacks for short-lived processes
                        // before the underlying Process is disposed.
                        FlushProcessOutput();
                    }
                    catch (Exception ex) when (IsBestEffortCleanupException(ex))
                    {
                        // The process is no longer associated with an underlying OS process, or flush is unsupported.
                    }
                }
            }
        }
        finally
        {
            _disposed = true;
            _process.Dispose();
        }
    }

    private bool GetHasExited() => _hooks?.HasExitedOverride?.Invoke(_process) ?? _process.HasExited;

    private void KillProcessTree()
    {
        if (_hooks?.KillProcessOverride is { } killProcessOverride)
        {
            killProcessOverride(_process);
            return;
        }

        _process.Kill(entireProcessTree: true);
    }

    private Task WaitForProcessExitAsync(CancellationToken cancellationToken)
    {
        return _hooks?.WaitForExitAsyncOverride?.Invoke(_process, cancellationToken)
               ?? _process.WaitForExitAsync(cancellationToken);
    }

    private void FlushProcessOutput()
    {
        if (_hooks?.WaitForExitOverride is { } waitForExitOverride)
        {
            waitForExitOverride(_process);
            return;
        }

        _process.WaitForExit();
    }

    private static bool IsBestEffortCleanupException(Exception exception)
    {
        return exception is InvalidOperationException
            or OperationCanceledException
            or ObjectDisposedException
            or Win32Exception
            or NotSupportedException;
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
