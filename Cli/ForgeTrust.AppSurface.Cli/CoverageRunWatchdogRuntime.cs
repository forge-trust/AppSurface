using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CliFx;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Runs the coverage watchdog monitor and owns process leases, local incident evidence, and terminal classification.
/// </summary>
internal sealed class CoverageRunWatchdogRuntime : IAsyncDisposable
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultConsoleTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly IConsole _console;
    private readonly TimeProvider _timeProvider;
    private readonly CoverageRunWatchdogOptions _options;
    private readonly TimeSpan _consoleTimeout;
    private readonly CoverageRunWatchdogSupervisor _supervisor;
    private readonly ICoverageRunWatchdogArtifactWriter _artifactWriter;
    private readonly CancellationTokenSource _monitorCancellation = new();
    private readonly CancellationTokenSource _watchdogCancellation = new();
    private readonly CancellationTokenSource _runCancellation;
    private readonly CancellationToken _externalCancellation;
    private readonly CancellationTokenRegistration _externalCancellationRegistration;
    private readonly ConcurrentDictionary<string, ProcessReservation> _processes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CoverageRunSafeCommand> _commands = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _artifactBindingGate = new(1, 1);
    private readonly SemaphoreSlim _monitorWake = new(0, 1);
    private readonly string _bootstrapDirectory;
    private readonly TaskCompletionSource<CommandException> _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _monitor;
    private string? _canonicalDirectory;
    private Task? _activeConsoleWrite;
    private Task? _watchdogCancellationDispatch;
    private bool _activeConsoleWriteAbandoned;
    private bool _processRegistrationClosed;
    private long _incidentOrdinal;

    /// <summary>Initializes a run-scoped watchdog and starts its deadline-driven monitor.</summary>
    /// <param name="console">Console used for bounded heartbeat and incident messages.</param>
    /// <param name="timeProvider">Monotonic and wall-clock time source.</param>
    /// <param name="options">Validated heartbeat and no-progress policy.</param>
    /// <param name="externalCancellation">Caller cancellation linked to watchdog cancellation.</param>
    /// <param name="consoleTimeout">Optional test seam for the maximum wait on one console write.</param>
    /// <param name="artifactWriter">Optional test seam for bounded incident persistence.</param>
    /// <param name="bootstrapDirectory">Optional test seam for the private pre-bind artifact directory.</param>
    public CoverageRunWatchdogRuntime(
        IConsole console,
        TimeProvider timeProvider,
        CoverageRunWatchdogOptions options,
        CancellationToken externalCancellation,
        TimeSpan? consoleTimeout = null,
        ICoverageRunWatchdogArtifactWriter? artifactWriter = null,
        string? bootstrapDirectory = null)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _consoleTimeout = consoleTimeout ?? DefaultConsoleTimeout;
        if (_consoleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(consoleTimeout), "Console timeout must be greater than zero.");
        }
        _supervisor = new CoverageRunWatchdogSupervisor(timeProvider, options);
        _artifactWriter = artifactWriter ?? new CoverageRunWatchdogArtifactWriter(timeProvider);
        _externalCancellation = externalCancellation;
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation, _watchdogCancellation.Token);
        _bootstrapDirectory = bootstrapDirectory
            ?? Path.Join(Path.GetTempPath(), "appsurface-coverage-watchdog", Guid.NewGuid().ToString("N"));

        _externalCancellationRegistration = externalCancellation.Register(_supervisor.ClaimExternalCancellation);
        _monitor = MonitorAsync(externalCancellation);
    }

    /// <summary>Gets the token canceled by either the caller or fail-mode watchdog classification.</summary>
    public CancellationToken RunCancellationToken => _runCancellation.Token;

    /// <summary>Registers an operation with the underlying monotonic supervisor.</summary>
    public void Register(CoverageRunWatchdogOperation operation, CoverageRunWatchdogOperationState state)
    {
        _externalCancellation.ThrowIfCancellationRequested();
        try
        {
            _supervisor.Register(operation, state);
            SignalMonitor();
        }
        catch (InvalidOperationException) when (_externalCancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(_externalCancellation);
        }
    }

    /// <summary>Transitions an operation and records the transition as progress.</summary>
    public void Transition(string identity, CoverageRunWatchdogOperationState state)
    {
        _externalCancellation.ThrowIfCancellationRequested();
        try
        {
            _supervisor.Transition(identity, state);
            SignalMonitor();
        }
        catch (InvalidOperationException) when (_externalCancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(_externalCancellation);
        }
    }

    /// <summary>
    /// Creates a structured process request and reserves its lifecycle before process creation begins.
    /// </summary>
    public CoverageRunProcessRequest CreateProcessRequest(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string operationId,
        CoverageRunSafeCommand command,
        string? outputFile = null)
    {
        var reservation = new ProcessReservation(operationId);
        lock (_gate)
        {
            if (_processRegistrationClosed || !_processes.TryAdd(reservation.Id, reservation))
            {
                throw new OperationCanceledException(RunCancellationToken);
            }

            _commands[operationId] = command;
        }

        return new CoverageRunProcessRequest(
            fileName,
            arguments,
            workingDirectory,
            outputFile,
            bytes => ObserveOutput(operationId, bytes),
            process => ProcessStarted(reservation, process),
            () => ProcessCompleted(reservation));
    }

    /// <summary>
    /// Binds incident evidence to a fully validated AppSurface-owned output directory.
    /// </summary>
    public async Task BindCanonicalOutputAsync(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        await _artifactBindingGate.WaitAsync();
        string? promotionFailure = null;
        try
        {
            _canonicalDirectory = Path.GetFullPath(outputDirectory);
            var bootstrapArtifact = Path.Join(_bootstrapDirectory, "coverage-watchdog.json");
            if (!File.Exists(bootstrapArtifact))
            {
                return;
            }

            Directory.CreateDirectory(_canonicalDirectory);
            var canonicalArtifact = Path.Join(_canonicalDirectory, "coverage-watchdog.json");
            var promotion = Task.Run(() => File.Copy(bootstrapArtifact, canonicalArtifact, overwrite: true));
            if (await Task.WhenAny(promotion, Task.Delay(TimeSpan.FromSeconds(2), _timeProvider)) != promotion)
            {
                ObserveFault(promotion);
                promotionFailure = "bootstrap-promotion-timeout";
            }
            else
            {
                try
                {
                    await promotion;
                    File.Delete(bootstrapArtifact);
                    TryDeleteBootstrapDirectory();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    promotionFailure = "bootstrap-promotion-failed";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            promotionFailure = "bootstrap-promotion-failed";
        }
        finally
        {
            _artifactBindingGate.Release();
        }

        if (promotionFailure is not null)
        {
            await WriteArtifactFailureAsync(promotionFailure);
        }
    }

    /// <summary>Completes normal monitoring and waits for the monitor loop to stop.</summary>
    public async ValueTask DisposeAsync()
    {
        _supervisor.Stop();
        await _monitorCancellation.CancelAsync();
        try
        {
            await _monitor;
        }
        catch (OperationCanceledException) when (_monitorCancellation.IsCancellationRequested)
        {
        }

        TryDeleteEmptyBootstrapDirectory();
        _externalCancellationRegistration.Dispose();
        DisposeCancellationSourcesAfterDispatch();
        _monitorCancellation.Dispose();
        _artifactBindingGate.Dispose();
        _monitorWake.Dispose();
    }

    /// <summary>Throws the authoritative watchdog diagnostic after a watchdog-caused cancellation.</summary>
    public async Task ThrowIfWatchdogTerminalAsync()
    {
        if (_supervisor.TerminalCause is null)
        {
            return;
        }

        throw await _terminal.Task;
    }

    /// <summary>
    /// Atomically claims normal or ordinary-failure completion, or throws an already-claimed watchdog result.
    /// </summary>
    public async Task CompleteAsync()
    {
        if (!_supervisor.TryComplete(out _, out var externalCancellationClaimed))
        {
            if (externalCancellationClaimed)
            {
                throw new OperationCanceledException(_externalCancellation);
            }

            throw await _terminal.Task;
        }

        await _monitorCancellation.CancelAsync();
        try
        {
            await _monitor;
        }
        catch (OperationCanceledException) when (_monitorCancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>Writes ordinary workflow output through the same bounded, serialized sink as heartbeats.</summary>
    public Task WriteOutputLineAsync(string text) => WriteConsoleAsync(text, waitForTurn: true);

    /// <summary>Writes ordinary workflow output without appending a newline through the bounded sink.</summary>
    public Task WriteOutputAsync(string text) => WriteConsoleAsync(text, appendNewLine: false, waitForTurn: true);

    /// <summary>Writes ordinary workflow errors through the same bounded, serialized sink as incidents.</summary>
    public Task WriteErrorLineAsync(string text) => WriteConsoleAsync(text, error: true, waitForTurn: true);

    private async Task MonitorAsync(CancellationToken externalCancellation)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            externalCancellation,
            _monitorCancellation.Token);
        try
        {
            while (true)
            {
                var delay = _supervisor.GetNextDelay();
                if (delay > TimeSpan.Zero && !await WaitForEvaluationAsync(delay, delayCancellation.Token))
                {
                    continue;
                }

                var evaluation = _supervisor.Evaluate();
                if (evaluation.HeartbeatDue)
                {
                    await WriteConsoleAsync(RenderHeartbeat(evaluation.Snapshot, evaluation.NewlyStale));
                }

                if (evaluation.NewlyStale.Count == 0)
                {
                    continue;
                }

                if (_options.Mode == CoverageRunWatchdogMode.Warn)
                {
                    await HandleWarningAsync(evaluation);
                    continue;
                }

                if (_options.Mode == CoverageRunWatchdogMode.Fail &&
                    _supervisor.TryClaimTerminal(evaluation.NewlyStale[0], out var terminal) &&
                    terminal is not null)
                {
                    await HandleTerminalAsync(evaluation, terminal);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task HandleWarningAsync(CoverageRunWatchdogEvaluation evaluation)
    {
        var artifact = CreateArtifact(evaluation, evaluation.NewlyStale[0], "warning", null, new("not-requested", null));
        var write = await WriteArtifactAsync(artifact);
        var primary = evaluation.NewlyStale[0];
        var artifactText = write.Success ? QuotePath(write.Path!) : $"unavailable ({write.Detail})";
        await WriteConsoleAsync(
            $"Coverage watchdog warning: operation={Kind(primary.Kind)}; project={QuotePath(primary.Project)}; " +
            $"no-progress={WholeSeconds(primary.NoProgress)}s; concurrent-stalls={(evaluation.NewlyStale.Count - 1).ToString(CultureInfo.InvariantCulture)}; artifact={artifactText}");
        if (!write.Success)
        {
            await WriteArtifactFailureAsync(write.Detail);
        }
    }

    private async Task HandleTerminalAsync(
        CoverageRunWatchdogEvaluation evaluation,
        CoverageRunWatchdogOperationSnapshot terminal)
    {
        var cleanup = new CoverageRunWatchdogCleanup("deadline-exceeded", "cleanup-incomplete");
        var exception = CreateTerminalException(evaluation, terminal, cleanup, "unavailable (terminal-handler-failed)");
        ProcessReservation[] reservations;
        lock (_gate)
        {
            _processRegistrationClosed = true;
            reservations = _processes.Values.ToArray();
            foreach (var reservation in reservations)
            {
                reservation.CleanupOwnsDisposal = true;
            }
        }

        try
        {
            // Dispatch tree termination while callback-delivered Process instances are still valid,
            // then signal CliWrap/in-process cancellation without allowing a blocking callback to
            // hold the authoritative terminal outcome open.
            var cleanupTask = KillProcessesAsync(reservations);
            _watchdogCancellationDispatch = _watchdogCancellation.CancelAsync();
            ObserveFault(_watchdogCancellationDispatch);
            cleanup = await cleanupTask;
            var artifact = CreateArtifact(evaluation, terminal, "terminated", "ASCOV121", cleanup);
            var write = await WriteArtifactAsync(artifact);
            if (!write.Success)
            {
                await WriteArtifactFailureAsync(write.Detail);
            }

            var artifactText = write.Success ? write.Path! : $"unavailable ({write.Detail})";
            exception = CreateTerminalException(evaluation, terminal, cleanup, artifactText);
            await WriteConsoleAsync(exception.Message, error: true);
            await WriteConsoleAsync(RenderHeartbeat(evaluation.Snapshot, evaluation.NewlyStale));
        }
        catch
        {
            // Terminal classification is authoritative even when cleanup, evidence, or console IO fails.
        }
        finally
        {
            _terminal.TrySetResult(exception);
        }
    }

    private static CommandException CreateTerminalException(
        CoverageRunWatchdogEvaluation evaluation,
        CoverageRunWatchdogOperationSnapshot terminal,
        CoverageRunWatchdogCleanup cleanup,
        string artifactText)
    {
        var project = terminal.Project is null ? $"Operation {QuotePath(Kind(terminal.Kind))}" : $"Project {QuotePath(terminal.Project)}";
        return CoverageRunDiagnostics.Create(
            "ASCOV121",
            "Coverage run produced no observable progress.",
            $"{project} produced no observable progress for {WholeSeconds(terminal.NoProgress)}s; " +
            $"{Math.Max(0, evaluation.NewlyStale.Count - 1).ToString(CultureInfo.InvariantCulture)} additional operation(s) were concurrently stale. " +
            $"Artifact: {artifactText}. Cleanup: {cleanup.Status}.",
            "Inspect the local project log, raise --no-progress-timeout for intentionally quiet tests, or rerun with --watchdog warn.",
            "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-watchdog",
            terminal.Log,
            exitCode: 124);
    }

    private async Task<CoverageRunWatchdogCleanup> KillProcessesAsync(IReadOnlyList<ProcessReservation> reservations)
    {
        foreach (var reservation in reservations)
        {
            _ = reservation.StartKill();
        }

        var allCompleted = Task.WhenAll(reservations.Select(reservation => reservation.Completion.Task));
        var deadline = Task.Delay(CleanupTimeout, _timeProvider);
        if (await Task.WhenAny(allCompleted, deadline) != allCompleted)
        {
            _ = DisposeReservationsWhenFinishedAsync(reservations, allCompleted);
            return new CoverageRunWatchdogCleanup("deadline-exceeded", "root-timeout");
        }

        var killTasks = reservations.Select(reservation => reservation.KillTask).Where(task => task is not null).Cast<Task<bool>>().ToArray();
        var allKilled = Task.WhenAll(killTasks);
        if (await Task.WhenAny(allKilled, deadline) != allKilled)
        {
            _ = DisposeReservationsWhenFinishedAsync(reservations, allCompleted);
            return new CoverageRunWatchdogCleanup("deadline-exceeded", "kill-timeout");
        }

        var killSucceeded = await allKilled;
        DisposeReservations(reservations);
        return killSucceeded.All(succeeded => succeeded)
            ? new CoverageRunWatchdogCleanup("complete", null)
            : new CoverageRunWatchdogCleanup("failed", "kill-failed");
    }

    private CoverageRunWatchdogArtifact CreateArtifact(
        CoverageRunWatchdogEvaluation evaluation,
        CoverageRunWatchdogOperationSnapshot primary,
        string outcome,
        string? diagnosticCode,
        CoverageRunWatchdogCleanup cleanup)
    {
        var stale = evaluation.NewlyStale;
        return new CoverageRunWatchdogArtifact(
            1,
            Interlocked.Increment(ref _incidentOrdinal),
            outcome,
            diagnosticCode,
            _options.Mode,
            checked((long)_options.HeartbeatInterval.TotalMilliseconds),
            checked((long)_options.NoProgressTimeout.TotalMilliseconds),
            checked((long)evaluation.Snapshot.RunElapsed.TotalMilliseconds),
            _timeProvider.GetUtcNow(),
            ToIncident(primary),
            stale.Skip(1).Select(ToIncident).ToArray(),
            0,
            cleanup);
    }

    private CoverageRunWatchdogIncidentOperation ToIncident(CoverageRunWatchdogOperationSnapshot operation)
    {
        CoverageRunSafeCommand? safeCommand;
        lock (_gate)
        {
            _commands.TryGetValue(operation.Identity, out safeCommand);
        }

        return new CoverageRunWatchdogIncidentOperation(
            operation.Kind,
            operation.Project,
            operation.State,
            checked((long)operation.Elapsed.TotalMilliseconds),
            checked((long)operation.NoProgress.TotalMilliseconds),
            operation.LastProgressAtUtc,
            operation.ProgressSequence,
            operation.OutputBytes,
            operation.Log,
            safeCommand is null ? null : new CoverageRunWatchdogCommand(safeCommand.Executable, safeCommand.Options));
    }

    private async Task<ArtifactWriteResult> WriteArtifactAsync(CoverageRunWatchdogArtifact artifact)
    {
        await _artifactBindingGate.WaitAsync(_monitorCancellation.Token);
        try
        {
            if (_canonicalDirectory is null && !TryCreateBootstrapDirectory())
            {
                return new ArtifactWriteResult(false, null, "bootstrap-unavailable");
            }

            var destination = Path.Join(_canonicalDirectory ?? _bootstrapDirectory, "coverage-watchdog.json");
            var result = await _artifactWriter.WriteAsync(destination, artifact, _monitorCancellation.Token);
            return result.Written
                ? new ArtifactWriteResult(true, destination, null)
                : new ArtifactWriteResult(false, null, result.Detail ?? "artifact-write-failed");
        }
        finally
        {
            _artifactBindingGate.Release();
        }
    }

    private bool TryCreateBootstrapDirectory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(_bootstrapDirectory);
            }
            else
            {
                Directory.CreateDirectory(
                    _bootstrapDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private Task WriteArtifactFailureAsync(string? detail)
        => WriteConsoleAsync(
            $"ASCOV122 Coverage watchdog artifact unavailable ({detail ?? "artifact-write-failed"}). Fix: Use a writable dedicated --output directory and rerun.",
            error: true);

    private void ProcessStarted(ProcessReservation reservation, Process process)
    {
        var killImmediately = false;
        lock (_gate)
        {
            reservation.Process = process;
            killImmediately = _processRegistrationClosed;
        }

        if (killImmediately)
        {
            reservation.StartKill();
        }
    }

    private void ProcessCompleted(ProcessReservation reservation)
    {
        lock (_gate)
        {
            _processes.TryRemove(reservation.Id, out _);
            reservation.Completion.TrySetResult();
            if (!reservation.CleanupOwnsDisposal)
            {
                reservation.Process?.Dispose();
            }
        }
    }

    private void ObserveOutput(string identity, int bytes)
    {
        try
        {
            if (_supervisor.ObserveOutput(identity, bytes))
            {
                SignalMonitor();
            }
        }
        catch (InvalidOperationException)
        {
            // A terminal claim closes progress mutation before pipe drainage necessarily completes.
        }
    }

    private async Task<bool> WaitForEvaluationAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var iterationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(delay, _timeProvider, iterationCancellation.Token);
        var wakeTask = _monitorWake.WaitAsync(iterationCancellation.Token);
        var completed = await Task.WhenAny(delayTask, wakeTask);
        await iterationCancellation.CancelAsync();
        try
        {
            await Task.WhenAll(delayTask, wakeTask);
        }
        catch (OperationCanceledException) when (iterationCancellation.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ReferenceEquals(completed, delayTask) && delayTask.IsCompletedSuccessfully;
    }

    private void SignalMonitor()
    {
        try
        {
            _monitorWake.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake is enough because the monitor always recomputes from current state.
        }
    }

    private static void ObserveFault(Task task)
        => _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void DisposeCancellationSourcesAfterDispatch()
    {
        var dispatch = _watchdogCancellationDispatch;
        if (dispatch is null || dispatch.IsCompleted)
        {
            _runCancellation.Dispose();
            _watchdogCancellation.Dispose();
            return;
        }

        _ = dispatch.ContinueWith(
            _ =>
            {
                _runCancellation.Dispose();
                _watchdogCancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WriteConsoleAsync(
        string text,
        bool error = false,
        bool appendNewLine = true,
        bool waitForTurn = false)
    {
        Task write;
        while (true)
        {
            Task? activeWrite;
            bool activeWriteAbandoned;
            lock (_gate)
            {
                activeWrite = _activeConsoleWrite is { IsCompleted: false } ? _activeConsoleWrite : null;
                activeWriteAbandoned = activeWrite is not null && _activeConsoleWriteAbandoned;
                if (activeWrite is null)
                {
                    write = Task.Run(async () =>
                    {
                        if (error)
                        {
                            if (appendNewLine)
                            {
                                await _console.Error.WriteLineAsync(text);
                            }
                            else
                            {
                                await _console.Error.WriteAsync(text);
                            }
                        }
                        else
                        {
                            if (appendNewLine)
                            {
                                await _console.Output.WriteLineAsync(text);
                            }
                            else
                            {
                                await _console.Output.WriteAsync(text);
                            }
                        }
                    });
                    _activeConsoleWrite = write;
                    _activeConsoleWriteAbandoned = false;
                    _ = write.ContinueWith(
                        completed =>
                        {
                            _ = completed.Exception;
                            lock (_gate)
                            {
                                if (ReferenceEquals(_activeConsoleWrite, completed))
                                {
                                    _activeConsoleWrite = null;
                                    _activeConsoleWriteAbandoned = false;
                                }
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    break;
                }
            }

            if (!waitForTurn || activeWriteAbandoned)
            {
                return;
            }

            if (!await WaitForConsoleWriteAsync(activeWrite))
            {
                MarkConsoleWriteAbandoned(activeWrite);
                return;
            }
        }

        try
        {
            if (!await WaitForConsoleWriteAsync(write))
            {
                MarkConsoleWriteAbandoned(write);
            }
        }
        catch
        {
            // Console failures cannot disable deadline evaluation or terminal classification.
        }
    }

    private async Task<bool> WaitForConsoleWriteAsync(Task write)
    {
        var completed = await Task.WhenAny(write, Task.Delay(_consoleTimeout, _timeProvider, _monitorCancellation.Token));
        return completed == write;
    }

    private void MarkConsoleWriteAbandoned(Task write)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeConsoleWrite, write))
            {
                _activeConsoleWriteAbandoned = true;
            }
        }
    }

    private static string RenderHeartbeat(
        CoverageRunWatchdogSnapshot snapshot,
        IReadOnlyList<CoverageRunWatchdogOperationSnapshot> stale)
    {
        var staleIds = stale.Select(operation => operation.Identity).ToHashSet(StringComparer.Ordinal);
        var lines = new List<string>
        {
            $"Coverage heartbeat: elapsed={WholeSeconds(snapshot.RunElapsed)}s; queued={snapshot.Queued}; running={snapshot.Running}; finalizing={snapshot.Finalizing}; complete={snapshot.Complete}",
        };
        foreach (var operation in snapshot.Operations.Where(operation => operation.State is CoverageRunWatchdogOperationState.Running or CoverageRunWatchdogOperationState.Finalizing))
        {
            var identity = operation.Project is null ? $"operation={QuotePath(Kind(operation.Kind))}" : $"project={QuotePath(operation.Project)}";
            lines.Add(
                $"  {identity}; state={operation.State.ToString().ToLowerInvariant()}; elapsed={WholeSeconds(operation.Elapsed)}s; " +
                $"no-progress={WholeSeconds(operation.NoProgress)}s; output-bytes={operation.OutputBytes.ToString(CultureInfo.InvariantCulture)}" +
                (staleIds.Contains(operation.Identity) ? "; stale=true" : string.Empty));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void TryDeleteBootstrapDirectory()
    {
        try
        {
            if (Directory.Exists(_bootstrapDirectory))
            {
                Directory.Delete(_bootstrapDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private void TryDeleteEmptyBootstrapDirectory()
    {
        try
        {
            if (Directory.Exists(_bootstrapDirectory) && !Directory.EnumerateFileSystemEntries(_bootstrapDirectory).Any())
            {
                Directory.Delete(_bootstrapDirectory);
            }
        }
        catch
        {
        }
    }

    private static long WholeSeconds(TimeSpan value) => checked((long)Math.Floor(value.TotalSeconds));
    private static string Kind(CoverageRunWatchdogOperationKind kind) => kind.ToString().ToLowerInvariant();
    private static string QuotePath(string? value) => value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value.Length <= 512 ? value : value[..512]);

    private sealed class ProcessReservation
    {
        public ProcessReservation(string operationId)
        {
            Id = operationId + ":" + Guid.NewGuid().ToString("N");
        }

        public string Id { get; }
        public Process? Process { get; set; }
        public Task<bool>? KillTask { get; private set; }
        public bool CleanupOwnsDisposal { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool>? StartKill()
        {
            lock (this)
            {
                if (KillTask is not null || Process is null)
                {
                    return KillTask;
                }

                var process = Process;
                KillTask = Task.Run(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }

                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                });
                return KillTask;
            }
        }
    }

    private static void DisposeReservations(IEnumerable<ProcessReservation> reservations)
    {
        foreach (var reservation in reservations)
        {
            reservation.Process?.Dispose();
        }
    }

    private static async Task DisposeReservationsWhenFinishedAsync(
        IReadOnlyList<ProcessReservation> reservations,
        Task allCompleted)
    {
        try
        {
            await allCompleted;
            var killTasks = reservations.Select(reservation => reservation.KillTask).Where(task => task is not null).Cast<Task<bool>>();
            await Task.WhenAll(killTasks);
        }
        catch
        {
        }
        finally
        {
            DisposeReservations(reservations);
        }
    }

    private sealed record ArtifactWriteResult(bool Success, string? Path, string? Detail);
}
