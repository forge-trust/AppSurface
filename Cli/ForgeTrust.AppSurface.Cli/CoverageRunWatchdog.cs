using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliFx;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Controls how <c>coverage run</c> responds when an active operation produces no observable progress.
/// </summary>
internal enum CoverageRunWatchdogMode
{
    /// <summary>
    /// Report each newly observed stall and allow the run to continue.
    /// </summary>
    Warn,

    /// <summary>
    /// Report the first confirmed stall, cancel the run, and return exit code 124.
    /// </summary>
    Fail,

    /// <summary>
    /// Disable stall classification while retaining optional heartbeat output.
    /// </summary>
    Off,
}

/// <summary>
/// Parses the intentionally narrow duration grammar used by coverage watchdog options.
/// </summary>
internal static partial class CoverageRunDurationParser
{
    internal static readonly TimeSpan Maximum = TimeSpan.FromDays(30);

    /// <summary>
    /// Parses a lowercase integer duration such as <c>500ms</c>, <c>30s</c>, <c>10m</c>, or <c>1h</c>.
    /// </summary>
    /// <param name="value">Raw option value.</param>
    /// <param name="option">Option name used in diagnostics.</param>
    /// <param name="allowZero">Whether the exact value <c>0</c> is accepted.</param>
    /// <returns>The parsed duration.</returns>
    public static TimeSpan Parse(string? value, string option, bool allowZero)
    {
        if (allowZero && string.Equals(value, "0", StringComparison.Ordinal))
        {
            return TimeSpan.Zero;
        }

        var match = DurationPattern().Match(value ?? string.Empty);
        if (!match.Success
            || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity))
        {
            throw Invalid(option, value, allowZero);
        }

        try
        {
            var milliseconds = match.Groups[2].Value switch
            {
                "ms" => quantity,
                "s" => checked(quantity * 1000),
                "m" => checked(quantity * 60 * 1000),
                "h" => checked(quantity * 60 * 60 * 1000),
                _ => throw new InvalidOperationException("Unreachable duration suffix."),
            };
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            if (duration <= TimeSpan.Zero || duration > Maximum)
            {
                throw Invalid(option, value, allowZero);
            }

            return duration;
        }
        catch (OverflowException)
        {
            throw Invalid(option, value, allowZero);
        }
    }

    private static CommandException Invalid(string option, string? value, bool allowZero)
    {
        var zero = allowZero ? " or use 0 to disable it" : string.Empty;
        return CoverageRunDiagnostics.Create(
            "ASCOV101",
            $"{option} has an invalid duration.",
            $"Received '{value ?? string.Empty}'. Durations use 0 or a positive lowercase integer followed by ms, s, m, or h and cannot exceed 30 days.",
            $"Use a value such as 500ms, 30s, 10m, or 1h{zero}.",
            "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-watchdog");
    }

    [GeneratedRegex("^([1-9][0-9]*)(ms|s|m|h)$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
}

/// <summary>
/// Serializes coverage-run console messages so heartbeat and incident blocks do not interleave.
/// </summary>
internal sealed class CoverageRunConsoleSink : IDisposable
{
    private readonly IConsole _console;
    private readonly TimeSpan _writeTimeout;
    private readonly object _sync = new();
    private Task _writeTail = Task.CompletedTask;
    private bool _disposed;

    /// <summary>
    /// Creates a bounded run-scoped console sink.
    /// </summary>
    public CoverageRunConsoleSink(IConsole console, TimeSpan? writeTimeout = null)
    {
        _console = console;
        _writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Writes one serialized block to standard output.
    /// </summary>
    /// <param name="text">Complete output block.</param>
    /// <param name="cancellationToken">Bounds how long the caller waits without cancelling an accepted write.</param>
    /// <param name="appendNewLine">Whether to append the platform newline.</param>
    /// <param name="coalesceIfWritePending">Whether to omit this periodic update when an earlier write is still pending. Diagnostic and terminal blocks must leave this disabled.</param>
    /// <returns>A task that completes when the write finishes or the caller's bounded wait ends.</returns>
    public Task WriteOutputAsync(
        string text,
        CancellationToken cancellationToken = default,
        bool appendNewLine = true,
        bool coalesceIfWritePending = false)
        => WriteAsync(_console.Output, text, appendNewLine, coalesceIfWritePending, cancellationToken);

    /// <summary>
    /// Writes one serialized block to standard error.
    /// </summary>
    public Task WriteErrorAsync(
        string text,
        CancellationToken cancellationToken = default,
        bool appendNewLine = true)
        => WriteAsync(_console.Error, text, appendNewLine, coalesceIfWritePending: false, cancellationToken);

    /// <summary>
    /// Writes a terminal diagnostic in FIFO order when possible, then bypasses a blocked queue after the bounded wait.
    /// The fallback can interleave with a hung earlier output block, but atomically abandons its queued continuation so the
    /// critical diagnostic is emitted at most once.
    /// </summary>
    /// <param name="text">Complete error block.</param>
    /// <param name="cancellationToken">Bounds how long the caller waits without cancelling an accepted write.</param>
    /// <param name="appendNewLine">Whether to append the platform newline.</param>
    /// <returns>A task that completes after the ordered attempt or bounded direct fallback.</returns>
    public async Task WriteCriticalErrorAsync(
        string text,
        CancellationToken cancellationToken = default,
        bool appendNewLine = true)
    {
        var state = 0;
        Task queuedWrite;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            queuedWrite = _writeTail.ContinueWith(
                async _ =>
                {
                    if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                    {
                        return;
                    }

                    await WriteBlockAsync(_console.Error, text, appendNewLine);
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
            _writeTail = queuedWrite;
        }

        ObserveFault(queuedWrite);
        try
        {
            await queuedWrite.WaitAsync(_writeTimeout, cancellationToken);
            return;
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref state, 2, 0);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref state, 2, 0) != 0)
        {
            return;
        }

        var directWrite = Task.Run(() => WriteBlockAsync(_console.Error, text, appendNewLine));
        ObserveFault(directWrite);
        await AwaitBoundedAsync(directWrite, cancellationToken);
    }

    private Task WriteAsync(
        TextWriter writer,
        string text,
        bool appendNewLine,
        bool coalesceIfWritePending,
        CancellationToken cancellationToken)
    {
        Task write;
        lock (_sync)
        {
            if (_disposed || (coalesceIfWritePending && !_writeTail.IsCompleted))
            {
                return Task.CompletedTask;
            }

            write = _writeTail.ContinueWith(
                _ => WriteBlockAsync(writer, text, appendNewLine),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
            _writeTail = write;
        }

        ObserveFault(write);
        return AwaitBoundedAsync(write, cancellationToken);
    }

    private static async Task WriteBlockAsync(TextWriter writer, string text, bool appendNewLine)
    {
        if (appendNewLine)
        {
            await writer.WriteLineAsync(text);
        }
        else
        {
            await writer.WriteAsync(text);
        }
    }

    private static void ObserveFault(Task write)
        => _ = write.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task AwaitBoundedAsync(Task write, CancellationToken cancellationToken)
    {
        try
        {
            await write.WaitAsync(_writeTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// Tracks one active coverage-run operation and exposes bounded progress updates.
/// </summary>
internal sealed class CoverageRunOperation : IDisposable
{
    private readonly CoverageRunWatchdogSupervisor _owner;
    private int _completed;

    internal CoverageRunOperation(CoverageRunWatchdogSupervisor owner, long id)
    {
        _owner = owner;
        Id = id;
    }

    internal long Id { get; }

    /// <summary>
    /// Records a positive count of observed child-process bytes as progress.
    /// </summary>
    public void ObserveBytes(int count)
    {
        if (count > 0)
        {
            _owner.ObserveBytes(Id, count);
        }
    }

    /// <summary>
    /// Records an explicit operation state transition as progress.
    /// </summary>
    public void Transition(string state) => _owner.Transition(Id, state);

    /// <summary>
    /// Reserves a supervisor-owned process lease before invoking the process runner.
    /// </summary>
    public CoverageRunProcessLease ReserveProcess() => _owner.ReserveProcess();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _owner.Complete(Id);
        }
    }
}

/// <summary>
/// Tracks a callback-delivered root process and guarantees late attachments observe terminal closure.
/// </summary>
internal sealed class CoverageRunProcessLease
{
    private readonly CoverageRunWatchdogSupervisor? _owner;
    private readonly object _sync = new();
    private readonly TaskCompletionSource<Process?> _resolution = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private bool _terminationRequested;
    private bool _completed;

    internal CoverageRunProcessLease(CoverageRunWatchdogSupervisor? owner)
    {
        _owner = owner;
    }

    internal static CoverageRunProcessLease Detached() => new(null);

    /// <summary>
    /// Attaches the root process delivered by CliWrap's start callback.
    /// </summary>
    public void Attach(Process process)
    {
        var terminate = false;
        lock (_sync)
        {
            if (_completed)
            {
                terminate = true;
            }
            else
            {
                _process = process;
                terminate = _terminationRequested;
                _resolution.TrySetResult(process);
            }
        }

        if (terminate)
        {
            TryKill(process);
        }
    }

    /// <summary>
    /// Marks the command complete and unregisters the lease.
    /// </summary>
    public void Complete()
    {
        lock (_sync)
        {
            _completed = true;
            _resolution.TrySetResult(_process);
            _process = null;
        }

        _owner?.ReleaseProcess(this);
    }

    internal async Task TerminateAsync()
    {
        Process? process;
        lock (_sync)
        {
            _terminationRequested = true;
            process = _process;
        }

        if (process is null)
        {
            process = await _resolution.Task;
            if (process is null)
            {
                return;
            }
        }

        await Task.Run(() => TryKill(process));
        try
        {
            await process.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }
    }
}

/// <summary>
/// Supervises coverage-run orchestration using per-operation monotonic progress clocks.
/// </summary>
internal sealed class CoverageRunWatchdogSupervisor : IAsyncDisposable
{
    private const string ArtifactName = "coverage-watchdog.json";
    private const int MaximumConcurrentArtifactOperations = 32;
    private const int MaximumArtifactTextLength = 1024;
    private const int MaximumArtifactCommandOptions = 32;
    private const int MaximumArtifactBytes = 64 * 1024;
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly CoverageRunConsoleSink _console;
    private readonly CoverageRunWatchdogMode _mode;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _watchdogCancellation = new();
    private readonly CancellationTokenSource _linkedCancellation;
    private readonly SemaphoreSlim _stateChanged = new(0, 1);
    private readonly object _artifactWriteSync = new();
    private readonly SemaphoreSlim _artifactCommitGate = new(1, 1);
    private readonly TimeSpan _artifactCommitTimeout;
    private readonly TimeSpan _artifactWriteTimeout;
    private readonly Action? _artifactStaged;
    private readonly Action? _artifactIncidentQueued;
    private readonly Action? _artifactResourcesDisposed;
    private readonly List<OperationState> _operations = [];
    private readonly HashSet<CoverageRunProcessLease> _processLeases = [];
    private readonly TaskCompletionSource _terminalCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _runStarted;
    private readonly Task _monitor;
    private readonly string _bootstrapDirectory;
    private string? _outputDirectory;
    private CoverageRunIncident? _terminalIncident;
    private CoverageRunIncident? _pendingArtifactIncident;
    private Task? _activeArtifactWrite;
    private CancellationTokenSource? _activeArtifactWriteCancellation;
    private long _nextId;
    private int _incidentOrdinal;
    private int _artifactResourcesDisposeState;

    /// <summary>
    /// Initializes and starts a run-scoped watchdog monitor.
    /// </summary>
    /// <param name="mode">Whether stalls are ignored, warned, or terminate the run.</param>
    /// <param name="heartbeatInterval">Interval between progress heartbeats; zero disables them.</param>
    /// <param name="timeout">Maximum observable no-progress duration for an active operation.</param>
    /// <param name="console">Console receiving serialized heartbeat and incident output.</param>
    /// <param name="timeProvider">Clock and timer source used for monotonic stall classification.</param>
    /// <param name="externalCancellation">Caller cancellation linked to supervised work.</param>
    /// <param name="artifactStaged">Optional test seam invoked after an incident artifact is staged.</param>
    /// <param name="artifactCommitTimeout">Optional bound for bootstrap artifact promotion.</param>
    /// <param name="artifactWriteTimeout">Optional bound for waiting on one incident write.</param>
    /// <param name="artifactIncidentQueued">Optional test seam invoked when a newer incident is queued behind an active write.</param>
    /// <param name="artifactResourcesDisposed">Optional test seam invoked after deferred artifact resources are released.</param>
    public CoverageRunWatchdogSupervisor(
        CoverageRunWatchdogMode mode,
        TimeSpan heartbeatInterval,
        TimeSpan timeout,
        IConsole console,
        TimeProvider timeProvider,
        CancellationToken externalCancellation,
        Action? artifactStaged = null,
        TimeSpan? artifactCommitTimeout = null,
        TimeSpan? artifactWriteTimeout = null,
        Action? artifactIncidentQueued = null,
        Action? artifactResourcesDisposed = null)
    {
        _mode = mode;
        _heartbeatInterval = heartbeatInterval;
        _timeout = timeout;
        _console = new CoverageRunConsoleSink(console);
        _timeProvider = timeProvider;
        _artifactStaged = artifactStaged;
        _artifactCommitTimeout = artifactCommitTimeout ?? TimeSpan.FromSeconds(2);
        _artifactWriteTimeout = artifactWriteTimeout ?? TimeSpan.FromSeconds(2);
        _artifactIncidentQueued = artifactIncidentQueued;
        _artifactResourcesDisposed = artifactResourcesDisposed;
        _runStarted = timeProvider.GetTimestamp();
        _bootstrapDirectory = Directory.CreateTempSubdirectory("appsurface-coverage-watchdog-").FullName;
        _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation, _watchdogCancellation.Token);
        _monitor = MonitorAsync(_stop.Token);
    }

    /// <summary>
    /// Gets the token shared by all supervised coverage work.
    /// </summary>
    public CancellationToken CancellationToken => _linkedCancellation.Token;

    /// <summary>
    /// Gets the run-scoped bounded console sink used by all coverage workflow messages.
    /// </summary>
    public CoverageRunConsoleSink Console => _console;

    /// <summary>
    /// Binds watchdog artifacts to an AppSurface-owned output directory after full output validation.
    /// </summary>
    /// <param name="outputDirectory">Absolute prepared output directory for canonical watchdog artifacts.</param>
    /// <remarks>Promotion failures are reported as <c>ASCOV122</c> and do not replace the watchdog's terminal outcome.</remarks>
    public void BindOutputDirectory(string outputDirectory)
    {
        var destination = Path.Join(outputDirectory, ArtifactName);
        if (!_artifactCommitGate.Wait(_artifactCommitTimeout))
        {
            lock (_sync)
            {
                _outputDirectory = outputDirectory;
            }

            _ = _console.WriteCriticalErrorAsync(
                "ASCOV122 Coverage watchdog bootstrap promotion is delayed by an active artifact write. Cause: artifact-write-timeout. Fix: Inspect the canonical output directory after the write completes. Docs: Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-watchdog");
            return;
        }

        try
        {
            lock (_sync)
            {
                _outputDirectory = outputDirectory;
            }

            var bootstrapArtifact = Path.Join(_bootstrapDirectory, ArtifactName);
            if (File.Exists(bootstrapArtifact))
            {
                File.Move(bootstrapArtifact, destination, overwrite: true);
            }

            if (Directory.Exists(_bootstrapDirectory))
            {
                Directory.Delete(_bootstrapDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _console.WriteCriticalErrorAsync(
                "ASCOV122 Coverage watchdog bootstrap artifact could not be promoted. Cause: artifact-write-failed. Fix: Use a writable dedicated --output directory. Docs: Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-watchdog");
        }
        finally
        {
            _artifactCommitGate.Release();
        }
    }

    /// <summary>
    /// Starts a supervised operation. Queued work should call this only when it becomes active.
    /// </summary>
    /// <param name="kind">Stable operation kind used for ordering and diagnostics.</param>
    /// <param name="project">Optional solution-relative project path.</param>
    /// <param name="order">Stable execution order among operations of the same kind.</param>
    /// <param name="state">Initial operation state.</param>
    /// <param name="log">Optional output-relative log path.</param>
    /// <param name="commandOptions">Safe command option names; values must already be excluded.</param>
    /// <returns>A disposable operation that marks completion and exposes progress/process seams.</returns>
    /// <exception cref="OperationCanceledException">Thrown after the watchdog claims terminal ownership.</exception>
    public CoverageRunOperation Start(
        string kind,
        string? project = null,
        int order = 0,
        string state = "running",
        string? log = null,
        IReadOnlyList<string>? commandOptions = null)
    {
        lock (_sync)
        {
            ThrowIfTerminalLocked();
            var id = ++_nextId;
            var now = _timeProvider.GetTimestamp();
            _operations.Add(new OperationState(id, kind, project, order, state, log, commandOptions ?? [], now));
            SignalStateChanged();
            return new CoverageRunOperation(this, id);
        }
    }

    /// <summary>
    /// Reserves a process lease before a child-process start callback can attach its root process.
    /// </summary>
    /// <returns>A lease registered for terminal cleanup.</returns>
    /// <exception cref="OperationCanceledException">Thrown after the watchdog claims terminal ownership.</exception>
    internal CoverageRunProcessLease ReserveProcess()
    {
        lock (_sync)
        {
            ThrowIfTerminalLocked();
            var lease = new CoverageRunProcessLease(this);
            _processLeases.Add(lease);
            return lease;
        }
    }

    /// <summary>
    /// Releases a completed process lease from terminal cleanup tracking.
    /// </summary>
    /// <param name="lease">The completed lease.</param>
    internal void ReleaseProcess(CoverageRunProcessLease lease)
    {
        lock (_sync)
        {
            _processLeases.Remove(lease);
        }
    }

    /// <summary>
    /// Records positive observed output bytes for an active operation.
    /// </summary>
    /// <param name="id">Operation identifier.</param>
    /// <param name="count">Positive byte count reported by the process observer.</param>
    internal void ObserveBytes(long id, int count)
    {
        lock (_sync)
        {
            if (Find(id) is { Completed: false } operation)
            {
                operation.OutputBytes = checked(operation.OutputBytes + count);
                MarkProgress(operation);
            }
        }
    }

    /// <summary>
    /// Records an explicit state transition as operation progress.
    /// </summary>
    /// <param name="id">Operation identifier.</param>
    /// <param name="state">New stable state.</param>
    internal void Transition(long id, string state)
    {
        lock (_sync)
        {
            if (Find(id) is { Completed: false } operation)
            {
                operation.State = state;
                MarkProgress(operation);
            }
        }
    }

    /// <summary>
    /// Marks an active operation complete exactly once.
    /// </summary>
    /// <param name="id">Operation identifier.</param>
    internal void Complete(long id)
    {
        lock (_sync)
        {
            if (Find(id) is { Completed: false } operation)
            {
                operation.State = "complete";
                operation.Completed = true;
                MarkProgress(operation);
            }
        }
    }

    /// <summary>
    /// Converts watchdog-owned cancellation into the stable <c>ASCOV121</c> exit-124 diagnostic.
    /// </summary>
    /// <exception cref="CommandException">Thrown with exit code 124 after terminal cleanup and the bounded incident-write attempt finish.</exception>
    public void ThrowIfFailed()
    {
        CoverageRunIncident? incident;
        lock (_sync)
        {
            incident = _terminalIncident;
        }

        if (incident is null)
        {
            return;
        }

        _terminalCompletion.Task.GetAwaiter().GetResult();
        lock (_sync)
        {
            incident = _terminalIncident!;
        }

        var subject = incident.Primary.Project is null
            ? $"Operation '{incident.Primary.Kind}'"
            : $"Project '{incident.Primary.Project}'";
        var message = FormattableString.Invariant(
            $"ASCOV121 Coverage run stalled. Cause: {subject} produced no observable progress for {(long)incident.Primary.NoProgress.TotalSeconds}s; {incident.Concurrent.Count} additional operation(s) were concurrently stale. Fix: Inspect the local project log, raise --no-progress-timeout for intentionally quiet tests, or rerun with --watchdog warn. Docs: Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-watchdog Artifact: {DisplayArtifactPath(incident.ArtifactPath)} Cleanup: {incident.CleanupStatus}");
        throw new CommandException(message, 124);
    }

    /// <summary>
    /// Revalidates that the watchdog has not claimed terminal ownership before an atomic artifact commit.
    /// </summary>
    /// <param name="commit">Synchronous canonical replacement performed while terminal ownership is locked.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="commit"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the watchdog already owns the terminal outcome.</exception>
    public void Commit(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_sync)
        {
            ThrowIfTerminalLocked();
            commit();
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        var nextHeartbeat = _heartbeatInterval > TimeSpan.Zero
            ? _timeProvider.GetUtcNow() + _heartbeatInterval
            : DateTimeOffset.MaxValue;

        try
        {
            while (true)
            {
                var delay = ComputeDelay(nextHeartbeat);
                await WaitForStateChangeOrDeadlineAsync(delay, cancellationToken);

                var nowUtc = _timeProvider.GetUtcNow();
                var snapshot = Snapshot();
                var stale = snapshot.Where(item => !item.WarningLatched && item.NoProgress >= _timeout).ToArray();

                if (nowUtc >= nextHeartbeat)
                {
                    await WriteHeartbeatBoundedAsync(snapshot, cancellationToken);
                    nextHeartbeat = nowUtc + _heartbeatInterval;
                }

                if (_mode == CoverageRunWatchdogMode.Off || stale.Length == 0)
                {
                    continue;
                }

                if (!TryLatch(stale, out var confirmed))
                {
                    continue;
                }

                var incident = CreateIncident(confirmed, _mode == CoverageRunWatchdogMode.Fail);
                if (_mode == CoverageRunWatchdogMode.Fail)
                {
                    lock (_sync)
                    {
                        _terminalIncident ??= incident;
                    }

                    try
                    {
                        RequestWatchdogCancellation();
                        var cleanup = await CleanupProcessesAsync();
                        incident = incident with { CleanupStatus = cleanup };
                        lock (_sync)
                        {
                            _terminalIncident = incident;
                        }

                        await WriteIncidentBoundedAsync(incident);
                    }
                    finally
                    {
                        _terminalCompletion.TrySetResult();
                    }

                    return;
                }

                await WriteIncidentBoundedAsync(incident);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WriteIncidentBoundedAsync(CoverageRunIncident incident)
    {
        Task write;
        lock (_artifactWriteSync)
        {
            if (_activeArtifactWrite is not null)
            {
                if (_pendingArtifactIncident is null
                    || incident.Outcome == "terminated"
                    || _pendingArtifactIncident.Outcome != "terminated")
                {
                    _pendingArtifactIncident = incident;
                    _artifactIncidentQueued?.Invoke();
                }

                return;
            }

            write = StartArtifactWriteLocked(incident);
        }

        using var deadline = new CancellationTokenSource(_artifactWriteTimeout);
        try
        {
            await write.WaitAsync(_artifactWriteTimeout, deadline.Token);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            deadline.Cancel();
        }
    }

    private Task StartArtifactWriteLocked(CoverageRunIncident incident)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var write = Task.Run(() => WriteIncidentAsync(incident, cancellation.Token));
        _activeArtifactWriteCancellation = cancellation;
        _activeArtifactWrite = write;
        _ = write.ContinueWith(
            CompleteArtifactWrite,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return write;
    }

    private void CompleteArtifactWrite(Task completed)
    {
        _ = completed.Exception;
        lock (_artifactWriteSync)
        {
            if (!ReferenceEquals(_activeArtifactWrite, completed))
            {
                return;
            }

            _activeArtifactWrite = null;
            _activeArtifactWriteCancellation?.Dispose();
            _activeArtifactWriteCancellation = null;
            if (!_stop.IsCancellationRequested && _pendingArtifactIncident is { } pending)
            {
                _pendingArtifactIncident = null;
                StartArtifactWriteLocked(pending);
            }
            else
            {
                _pendingArtifactIncident = null;
            }
        }
    }

    private async Task<bool> DrainArtifactWriteAsync()
    {
        Task? active;
        lock (_artifactWriteSync)
        {
            _pendingArtifactIncident = null;
            _activeArtifactWriteCancellation?.Cancel();
            active = _activeArtifactWrite;
        }

        if (active is null)
        {
            return true;
        }

        try
        {
            await active.WaitAsync(_artifactWriteTimeout);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return active.IsCompleted;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private async Task WriteHeartbeatBoundedAsync(IReadOnlyList<OperationSnapshot> snapshot, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await WriteHeartbeatAsync(snapshot, deadline.Token).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<string> CleanupProcessesAsync()
    {
        CoverageRunProcessLease[] leases;
        lock (_sync)
        {
            leases = _processLeases.ToArray();
        }

        try
        {
            await Task.WhenAll(leases.Select(lease => lease.TerminateAsync())).WaitAsync(TimeSpan.FromSeconds(10));
            return "complete";
        }
        catch (TimeoutException)
        {
            return "deadline-exceeded";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "failed";
        }
    }

    private void RequestWatchdogCancellation()
    {
        var cancellation = _watchdogCancellation.CancelAsync();
        _ = cancellation.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private TimeSpan ComputeDelay(DateTimeOffset nextHeartbeat)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var heartbeatDelay = nextHeartbeat == DateTimeOffset.MaxValue ? TimeSpan.MaxValue : nextHeartbeat - nowUtc;
        var stallDelay = Snapshot()
            .Where(operation => _mode != CoverageRunWatchdogMode.Off && !operation.WarningLatched)
            .Select(operation => _timeout - operation.NoProgress)
            .DefaultIfEmpty(TimeSpan.MaxValue)
            .Min();

        var delay = heartbeatDelay < stallDelay ? heartbeatDelay : stallDelay;
        if (delay == TimeSpan.MaxValue)
        {
            delay = TimeSpan.FromHours(24);
        }

        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(1);
        }

        return delay;
    }

    private async Task WaitForStateChangeOrDeadlineAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = Task.Delay(delay, _timeProvider, waitCancellation.Token);
        var stateChange = _stateChanged.WaitAsync(waitCancellation.Token);
        var completed = await Task.WhenAny(deadline, stateChange);
        waitCancellation.Cancel();
        await completed;
    }

    private void SignalStateChanged()
    {
        if (_stateChanged.CurrentCount == 0)
        {
            _stateChanged.Release();
        }
    }

    private OperationSnapshot[] Snapshot()
    {
        lock (_sync)
        {
            var now = _timeProvider.GetTimestamp();
            return _operations
                .Where(operation => !operation.Completed)
                .OrderBy(operation => OperationKindOrder(operation.Kind))
                .ThenBy(operation => operation.Order)
                .Select(operation => operation.Snapshot(_timeProvider.GetElapsedTime(operation.Started, now), _timeProvider.GetElapsedTime(operation.LastProgress, now)))
                .ToArray();
        }
    }

    private bool TryLatch(IReadOnlyList<OperationSnapshot> candidates, out OperationSnapshot[] confirmed)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetTimestamp();
            var result = new List<OperationSnapshot>();
            foreach (var candidate in candidates)
            {
                var operation = Find(candidate.Id);
                if (operation is null || operation.Completed || operation.WarningLatched || operation.Sequence != candidate.Sequence)
                {
                    continue;
                }

                var noProgress = _timeProvider.GetElapsedTime(operation.LastProgress, now);
                if (noProgress < _timeout)
                {
                    continue;
                }

                operation.WarningLatched = true;
                result.Add(operation.Snapshot(_timeProvider.GetElapsedTime(operation.Started, now), noProgress));
            }

            confirmed = result.ToArray();
            return confirmed.Length > 0;
        }
    }

    private CoverageRunIncident CreateIncident(IReadOnlyList<OperationSnapshot> stale, bool terminated)
    {
        return new CoverageRunIncident(
            Interlocked.Increment(ref _incidentOrdinal),
            terminated ? "terminated" : "warning",
            stale[0],
            stale.Skip(1).ToArray(),
            ArtifactPath: null);
    }

    private string ResolveArtifactPath()
    {
        lock (_sync)
        {
            if (_outputDirectory is not null)
            {
                return Path.Join(_outputDirectory, ArtifactName);
            }
        }

        return Path.Join(_bootstrapDirectory, ArtifactName);
    }

    private async Task WriteHeartbeatAsync(IReadOnlyList<OperationSnapshot> snapshot, CancellationToken cancellationToken)
    {
        if (_heartbeatInterval <= TimeSpan.Zero)
        {
            return;
        }

        var elapsed = (long)_timeProvider.GetElapsedTime(_runStarted).TotalSeconds;
        var lines = new List<string>
        {
            FormattableString.Invariant($"Coverage heartbeat: elapsed={elapsed}s; running={snapshot.Count(item => item.State == "running")}; finalizing={snapshot.Count(item => item.State == "finalizing")}; complete={CompletedCount()}")
        };
        lines.AddRange(snapshot.Select(item => FormattableString.Invariant(
            $"  operation={JsonSerializer.Serialize(item.Kind)}; project={JsonSerializer.Serialize(item.Project)}; state={item.State}; elapsed={(long)item.Elapsed.TotalSeconds}s; no-progress={(long)item.NoProgress.TotalSeconds}s; output-bytes={item.OutputBytes}")));
        await _console.WriteOutputAsync(
            string.Join(Environment.NewLine, lines),
            cancellationToken,
            coalesceIfWritePending: true);
    }

    private async Task WriteIncidentAsync(CoverageRunIncident incident, CancellationToken cancellationToken)
    {
        var initialArtifactPath = ResolveArtifactPath();
        var directory = Path.GetDirectoryName(initialArtifactPath)!;
        string? reportedArtifactPath = null;
        string? staged = null;
        var gateHeld = false;
        try
        {
            await _artifactCommitGate.WaitAsync(cancellationToken);
            gateHeld = true;
            Directory.CreateDirectory(directory);
            var concurrent = incident.Concurrent.Take(MaximumConcurrentArtifactOperations).ToArray();
            var payload = new
            {
                schemaVersion = 1,
                incidentOrdinal = incident.Ordinal,
                outcome = incident.Outcome,
                diagnosticCode = incident.Outcome == "terminated" ? "ASCOV121" : null,
                watchdogMode = _mode.ToString().ToLowerInvariant(),
                heartbeatIntervalMilliseconds = (long)_heartbeatInterval.TotalMilliseconds,
                noProgressTimeoutMilliseconds = (long)_timeout.TotalMilliseconds,
                runElapsedMilliseconds = (long)_timeProvider.GetElapsedTime(_runStarted).TotalMilliseconds,
                classifiedAtUtc = _timeProvider.GetUtcNow(),
                primary = ToArtifactRecord(incident.Primary),
                concurrentlyStale = concurrent.Select(ToArtifactRecord),
                concurrentlyStaleOmitted = incident.Concurrent.Count - concurrent.Length,
                cleanup = new { status = incident.CleanupStatus, detail = (string?)null },
            };
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(payload, options) + Environment.NewLine;
            if (Encoding.UTF8.GetByteCount(json) > MaximumArtifactBytes)
            {
                var minimalPayload = new
                {
                    schemaVersion = 1,
                    incidentOrdinal = incident.Ordinal,
                    outcome = incident.Outcome,
                    diagnosticCode = incident.Outcome == "terminated" ? "ASCOV121" : null,
                    watchdogMode = _mode.ToString().ToLowerInvariant(),
                    heartbeatIntervalMilliseconds = (long)_heartbeatInterval.TotalMilliseconds,
                    noProgressTimeoutMilliseconds = (long)_timeout.TotalMilliseconds,
                    runElapsedMilliseconds = (long)_timeProvider.GetElapsedTime(_runStarted).TotalMilliseconds,
                    classifiedAtUtc = _timeProvider.GetUtcNow(),
                    primary = ToMinimalArtifactRecord(incident.Primary),
                    concurrentlyStale = Array.Empty<object>(),
                    concurrentlyStaleOmitted = incident.Concurrent.Count,
                    cleanup = new { status = incident.CleanupStatus, detail = "artifact-truncated" },
                };
                json = JsonSerializer.Serialize(minimalPayload, options) + Environment.NewLine;
            }

            staged = Path.Join(directory, $".{ArtifactName}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(staged, json, cancellationToken);
            _artifactStaged?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var destination = ResolveArtifactPath();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(staged, destination, overwrite: true);
            staged = null;
            reportedArtifactPath = destination;
            lock (_sync)
            {
                if (_terminalIncident?.Ordinal == incident.Ordinal)
                {
                    _terminalIncident = _terminalIncident with { ArtifactPath = destination };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _console.WriteCriticalErrorAsync(
                "ASCOV122 Coverage watchdog artifact unavailable. Cause: artifact-write-failed. Fix: Use a writable dedicated --output directory. Docs: Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-watchdog",
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _console.WriteCriticalErrorAsync(
                "ASCOV122 Coverage watchdog artifact unavailable. Cause: artifact-write-cancelled. Fix: Inspect the coverage run terminal diagnostic and rerun with a writable dedicated --output directory. Docs: Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-watchdog",
                CancellationToken.None);
        }
        finally
        {
            if (staged is not null)
            {
                try
                {
                    File.Delete(staged);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            if (gateHeld)
            {
                _artifactCommitGate.Release();
            }
        }

        var primary = incident.Primary;
        var subject = primary.Project is null ? $"operation={primary.Kind}" : $"project={JsonSerializer.Serialize(primary.Project)}";
        var prefix = incident.Outcome == "terminated" ? "Coverage watchdog termination" : "Coverage watchdog warning";
        if (!cancellationToken.IsCancellationRequested)
        {
            await _console.WriteCriticalErrorAsync(FormattableString.Invariant(
                $"{prefix}: {subject}; no-progress={(long)primary.NoProgress.TotalSeconds}s; concurrent-stalls={incident.Concurrent.Count}; artifact={JsonSerializer.Serialize(DisplayArtifactPath(reportedArtifactPath))}"), cancellationToken);
        }
    }

    private object ToArtifactRecord(OperationSnapshot item) => new
    {
        kind = BoundArtifactText(item.Kind),
        project = BoundArtifactText(item.Project),
        state = BoundArtifactText(item.State),
        elapsedMilliseconds = (long)item.Elapsed.TotalMilliseconds,
        noProgressMilliseconds = (long)item.NoProgress.TotalMilliseconds,
        lastProgressAtUtc = _timeProvider.GetUtcNow() - item.NoProgress,
        progressSequence = item.Sequence,
        outputBytes = item.OutputBytes,
        log = BoundArtifactText(item.Log),
        command = item.CommandOptions.Count == 0
            ? null
            : new
            {
                executable = "dotnet",
                options = item.CommandOptions
                    .Take(MaximumArtifactCommandOptions)
                    .Select(option => BoundArtifactText(option, 256)),
            },
    };

    private object ToMinimalArtifactRecord(OperationSnapshot item) => new
    {
        kind = BoundArtifactText(item.Kind, 128),
        project = BoundArtifactText(item.Project, 128),
        state = BoundArtifactText(item.State, 64),
        elapsedMilliseconds = (long)item.Elapsed.TotalMilliseconds,
        noProgressMilliseconds = (long)item.NoProgress.TotalMilliseconds,
        lastProgressAtUtc = _timeProvider.GetUtcNow() - item.NoProgress,
        progressSequence = item.Sequence,
        outputBytes = item.OutputBytes,
        log = BoundArtifactText(item.Log, 128),
        command = (object?)null,
    };

    private static string DisplayArtifactPath(string? path)
        => path is null
            ? "unavailable"
            : Path.GetRelativePath(Directory.GetCurrentDirectory(), path).Replace('\\', '/');

    private static string? BoundArtifactText(string? value, int maximumLength = MaximumArtifactTextLength)
        => value is null || value.Length <= maximumLength ? value : value[..maximumLength];

    private int CompletedCount()
    {
        lock (_sync)
        {
            return _operations.Count(operation => operation.Completed);
        }
    }

    private void MarkProgress(OperationState operation)
    {
        operation.LastProgress = _timeProvider.GetTimestamp();
        operation.Sequence++;
        operation.WarningLatched = false;
        SignalStateChanged();
    }

    private OperationState? Find(long id) => _operations.FirstOrDefault(operation => operation.Id == id);

    private void ThrowIfTerminalLocked()
    {
        if (_terminalIncident is not null)
        {
            throw new OperationCanceledException("Coverage watchdog already owns the terminal outcome.");
        }
    }

    private static int OperationKindOrder(string kind) => kind switch
    {
        "discovery" => 0,
        "build" => 1,
        "project" => 2,
        "merge" => 3,
        "diagnostics" => 4,
        "artifacts" => 5,
        _ => 6,
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _monitor;
        var artifactWriteDrained = await DrainArtifactWriteAsync();
        _linkedCancellation.Dispose();
        _watchdogCancellation.Dispose();
        _stop.Dispose();
        _stateChanged.Dispose();
        if (!artifactWriteDrained)
        {
            ScheduleDeferredArtifactCleanup();
            return;
        }

        DisposeArtifactResources();
    }

    private void ScheduleDeferredArtifactCleanup()
    {
        Task? active;
        lock (_artifactWriteSync)
        {
            active = _activeArtifactWrite;
        }

        if (active is null)
        {
            DisposeArtifactResources();
            return;
        }

        _ = active.ContinueWith(
            _ => DisposeArtifactResources(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void DisposeArtifactResources()
    {
        if (Interlocked.Exchange(ref _artifactResourcesDisposeState, 1) != 0)
        {
            return;
        }

        _artifactCommitGate.Dispose();
        _console.Dispose();
        try
        {
            if (Directory.Exists(_bootstrapDirectory))
            {
                Directory.Delete(_bootstrapDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _artifactResourcesDisposed?.Invoke();
    }

    private sealed class OperationState(
        long id,
        string kind,
        string? project,
        int order,
        string state,
        string? log,
        IReadOnlyList<string> commandOptions,
        long started)
    {
        public long Id { get; } = id;
        public string Kind { get; } = kind;
        public string? Project { get; } = project;
        public int Order { get; } = order;
        public string State { get; set; } = state;
        public string? Log { get; } = log;
        public IReadOnlyList<string> CommandOptions { get; } = commandOptions;
        public long Started { get; } = started;
        public long LastProgress { get; set; } = started;
        public long Sequence { get; set; } = 1;
        public long OutputBytes { get; set; }
        public bool WarningLatched { get; set; }
        public bool Completed { get; set; }

        public OperationSnapshot Snapshot(TimeSpan elapsed, TimeSpan noProgress)
            => new(Id, Kind, Project, State, Log, CommandOptions, elapsed, noProgress, Sequence, OutputBytes, WarningLatched);
    }

    private sealed record OperationSnapshot(
        long Id,
        string Kind,
        string? Project,
        string State,
        string? Log,
        IReadOnlyList<string> CommandOptions,
        TimeSpan Elapsed,
        TimeSpan NoProgress,
        long Sequence,
        long OutputBytes,
        bool WarningLatched);

    private sealed record CoverageRunIncident(
        int Ordinal,
        string Outcome,
        OperationSnapshot Primary,
        IReadOnlyList<OperationSnapshot> Concurrent,
        string? ArtifactPath,
        string CleanupStatus = "not-requested");
}
