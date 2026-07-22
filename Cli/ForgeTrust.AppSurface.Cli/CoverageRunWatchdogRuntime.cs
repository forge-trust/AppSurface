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

    /// <summary>Gets the task that completes after fail-mode cleanup and terminal evidence handling finish.</summary>
    public Task<CommandException> TerminalTask => _terminal.Task;

    /// <summary>Determines whether an exception is the already-published authoritative watchdog terminal result.</summary>
    /// <param name="exception">Exception currently being handled by the coverage workflow.</param>
    /// <returns><see langword="true"/> when completion would rethrow the same terminal exception instance.</returns>
    public bool IsTerminalException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return _terminal.Task.IsCompletedSuccessfully && ReferenceEquals(_terminal.Task.Result, exception);
    }

    /// <summary>Registers an operation with the underlying monotonic supervisor and wakes the monitor.</summary>
    /// <param name="operation">Unique run-scoped operation metadata.</param>
    /// <param name="state">Initial queued or active lifecycle state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The operation identity is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The identity is already registered or the supervisor is closed.</exception>
    /// <exception cref="OperationCanceledException">External run cancellation has been requested.</exception>
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

    /// <summary>Transitions an operation, records the transition as progress, and wakes the monitor.</summary>
    /// <param name="identity">Previously registered run-scoped operation identity.</param>
    /// <param name="state">New lifecycle state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">The identity is not registered.</exception>
    /// <exception cref="InvalidOperationException">The supervisor is closed.</exception>
    /// <exception cref="OperationCanceledException">External run cancellation has been requested.</exception>
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
    /// <param name="fileName">Executable passed to the retained CliWrap runner.</param>
    /// <param name="arguments">Already-tokenized command arguments.</param>
    /// <param name="workingDirectory">Process working directory.</param>
    /// <param name="operationId">Registered operation that receives output progress.</param>
    /// <param name="command">Privacy-normalized command metadata retained for incident evidence.</param>
    /// <param name="outputFile">Optional streamed log destination.</param>
    /// <returns>A request whose launch reservation is already visible to terminal cleanup.</returns>
    /// <exception cref="OperationCanceledException">Process registration is closed by terminal cleanup.</exception>
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
    /// <param name="outputDirectory">Canonical directory after the project-aware ownership guard succeeds.</param>
    /// <returns>A task that completes after any private bootstrap artifact is promoted or diagnosed.</returns>
    /// <exception cref="ArgumentException"><paramref name="outputDirectory"/> is empty or whitespace.</exception>
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

            var canonicalArtifact = Path.Join(_canonicalDirectory, "coverage-watchdog.json");
            var bytes = await File.ReadAllBytesAsync(bootstrapArtifact, _monitorCancellation.Token);
            if (bytes.Length > CoverageRunWatchdogArtifactSerializer.MaximumBytes)
            {
                promotionFailure = "bootstrap-promotion-failed";
            }
            else
            {
                var artifact = CoverageRunWatchdogArtifactSerializer.Deserialize(bytes);
                var promotion = await _artifactWriter.WriteAsync(canonicalArtifact, artifact, _monitorCancellation.Token);
                if (promotion.Written)
                {
                    File.Delete(bootstrapArtifact);
                    TryDeleteBootstrapDirectory();
                }
                else
                {
                    promotionFailure = string.Equals(promotion.Detail, "artifact-write-timeout", StringComparison.Ordinal)
                        ? "bootstrap-promotion-timeout"
                        : "bootstrap-promotion-failed";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Text.Json.JsonException or ArgumentOutOfRangeException)
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
            return;
        }
        finally
        {
            TryDeleteEmptyBootstrapDirectory();
            _externalCancellationRegistration.Dispose();
            DisposeCancellationSourcesAfterDispatch();
            _monitorCancellation.Dispose();
            _artifactBindingGate.Dispose();
            _monitorWake.Dispose();
        }
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
            return;
        }
    }

    /// <summary>Writes ordinary workflow output through the same bounded, serialized sink as heartbeats.</summary>
    /// <param name="text">Text to write with one trailing newline.</param>
    /// <returns>A task that completes after the bounded write succeeds, times out, or is abandoned.</returns>
    public Task WriteOutputLineAsync(string text) => WriteConsoleAsync(text, waitForTurn: true);

    /// <summary>Writes ordinary workflow output without appending a newline through the bounded sink.</summary>
    /// <param name="text">Text to write without adding a newline.</param>
    /// <returns>A task that completes after the bounded write succeeds, times out, or is abandoned.</returns>
    public Task WriteOutputAsync(string text) => WriteConsoleAsync(text, appendNewLine: false, waitForTurn: true);

    /// <summary>Writes ordinary workflow errors through the same bounded, serialized sink as incidents.</summary>
    /// <param name="text">Error text to write with one trailing newline.</param>
    /// <returns>A task that completes after the bounded write succeeds, times out, or is abandoned.</returns>
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
                if (_options.Mode == CoverageRunWatchdogMode.Fail &&
                    evaluation.NewlyStale.Count > 0 &&
                    TryClaimTerminal(evaluation.NewlyStale[0], out var terminal, out var terminalEvaluation))
                {
                    await HandleTerminalAsync(terminalEvaluation!, terminal!);
                    return;
                }

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
            }
        }
        catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested)
        {
            return;
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

            var artifactText = write.Success ? QuotePath(write.Path!) : $"unavailable ({write.Detail})";
            exception = CreateTerminalException(evaluation, terminal, cleanup, artifactText);
            await WriteConsoleAsync(exception.Message, error: true);
            await WriteConsoleAsync(RenderHeartbeat(evaluation.Snapshot, evaluation.NewlyStale));
        }
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            // Terminal classification is authoritative even when cleanup, evidence, or console IO fails.
        }
        finally
        {
            _terminal.TrySetResult(exception);
        }
    }

    private bool TryClaimTerminal(
        CoverageRunWatchdogOperationSnapshot candidate,
        out CoverageRunWatchdogOperationSnapshot? terminal,
        out CoverageRunWatchdogEvaluation? terminalEvaluation)
    {
        lock (_gate)
        {
            if (!_supervisor.TryClaimTerminal(candidate, out terminal, out terminalEvaluation))
            {
                return false;
            }

            _processRegistrationClosed = true;
            return true;
        }
    }

    /// <summary>
    /// Atomically promotes a same-directory staged file while the run terminal gate remains open.
    /// </summary>
    /// <param name="stagedPath">Unique private staged path.</param>
    /// <param name="destinationPath">Canonical AppSurface-owned destination.</param>
    /// <exception cref="OperationCanceledException">A terminal cause or external cancellation won before commit.</exception>
    public void CommitStagedFile(string stagedPath, string destinationPath)
        => CommitStagedFiles([(stagedPath, destinationPath)]);

    /// <summary>
    /// Atomically claims the terminal gate and promotes a related set of staged files without a terminal interleaving.
    /// </summary>
    /// <param name="files">Private staged paths paired with canonical AppSurface-owned destinations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="files"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A staged or destination path is empty or whitespace.</exception>
    /// <exception cref="CoverageRunStagedCommitPreflightException">A staged file is missing or a destination is a directory.</exception>
    /// <exception cref="IOException">A same-directory promotion fails.</exception>
    /// <exception cref="UnauthorizedAccessException">A canonical destination cannot be replaced.</exception>
    /// <exception cref="OperationCanceledException">A terminal cause or external cancellation won before commit.</exception>
    public void CommitStagedFiles(IReadOnlyList<(string StagedPath, string DestinationPath)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (var (stagedPath, destinationPath) in files)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
            if (!File.Exists(stagedPath))
            {
                throw new CoverageRunStagedCommitPreflightException(
                    "A staged coverage artifact was not available for commit.",
                    stagedPath);
            }

            if (Directory.Exists(destinationPath))
            {
                throw new CoverageRunStagedCommitPreflightException(
                    $"Coverage artifact destination is a directory: {destinationPath}",
                    destinationPath);
            }
        }

        lock (_gate)
        {
            if (_processRegistrationClosed || _supervisor.TerminalCause is not null || _externalCancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(RunCancellationToken);
            }

            foreach (var (stagedPath, destinationPath) in files)
            {
                // Same-directory atomic rename is the terminal critical section. There is no
                // await or callback between the gate check and commit, so abandoned async work
                // never retains publication authority after a terminal claim.
                File.Move(stagedPath, destinationPath, overwrite: true);
            }
        }
    }

    private static CommandException CreateTerminalException(
        CoverageRunWatchdogEvaluation evaluation,
        CoverageRunWatchdogOperationSnapshot terminal,
        CoverageRunWatchdogCleanup cleanup,
        string artifactText)
    {
        var project = terminal.Project is null ? $"Operation {QuotePath(Kind(terminal.Kind))}" : $"Project {QuotePath(terminal.Project)}";
        var concurrentCount = Math.Max(0, evaluation.NewlyStale.Count - 1);
        var concurrentText = concurrentCount == 1
            ? "1 additional operation was concurrently stale."
            : $"{concurrentCount.ToString(CultureInfo.InvariantCulture)} additional operations were concurrently stale.";
        var diagnostic = CoverageRunDiagnostics.Create(
            "ASCOV121",
            "Coverage run stalled.",
            $"{project} produced no observable progress for {WholeSeconds(terminal.NoProgress)}s; " +
            concurrentText,
            "Inspect the local project log, raise --no-progress-timeout for intentionally quiet tests, or rerun with --watchdog warn.",
            "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-watchdog",
            terminal.Log,
            exitCode: 124);
        return new CommandException(
            $"{diagnostic.Message} Artifact: {artifactText} Cleanup: {cleanup.Status}",
            124,
            showHelp: false);
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
            return new CoverageRunWatchdogCleanup("deadline-exceeded", "kill-failed");
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
        var concurrent = stale
            .Skip(1)
            .Take(CoverageRunWatchdogArtifactSerializer.MaximumConcurrentOperations)
            .Select(ToIncident)
            .ToArray();
        return new CoverageRunWatchdogArtifact(
            1,
            Interlocked.Increment(ref _incidentOrdinal),
            outcome,
            diagnosticCode,
            _options.Mode,
            checked((long)_options.HeartbeatInterval.TotalMilliseconds),
            checked((long)_options.NoProgressTimeout.TotalMilliseconds),
            checked((long)evaluation.Snapshot.RunElapsed.TotalMilliseconds),
            evaluation.Snapshot.CapturedAtUtc,
            ToIncident(primary),
            concurrent,
            Math.Max(0, stale.Count - 1 - concurrent.Length),
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
            // Preserve caller cancellation even though canceling the losing wait is expected.
            cancellationToken.ThrowIfCancellationRequested();
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
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
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
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            // Bootstrap cleanup is best effort and must not replace the run's terminal result.
            return;
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
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            // Bootstrap cleanup is best effort and must not replace the run's terminal result.
            return;
        }
    }

    private static long WholeSeconds(TimeSpan value) => checked((long)Math.Floor(value.TotalSeconds));
    private static string Kind(CoverageRunWatchdogOperationKind kind) => kind.ToString().ToLowerInvariant();
    private static string QuotePath(string? value) => value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value.Length <= 512 ? value : value[..512]);

    private sealed class ProcessReservation
    {
        private readonly object _syncRoot = new();

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
            lock (_syncRoot)
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

                        // Process.Kill(true) requests descendant termination but does not wait
                        // for it. Waiting for the captured root here ensures cleanup is not
                        // reported complete while the owned root is still alive. Descendant
                        // guarantees remain bounded by the platform API's documented limits.
                        process.WaitForExit();

                        return true;
                    }
                    catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
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
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            // Reservation disposal in finally is authoritative; cleanup task failures are not.
            return;
        }
        finally
        {
            DisposeReservations(reservations);
        }
    }

    private sealed record ArtifactWriteResult(bool Success, string? Path, string? Detail);
}

/// <summary>
/// Reports a staged-set validation failure detected before any canonical coverage artifact is promoted.
/// </summary>
internal sealed class CoverageRunStagedCommitPreflightException : IOException
{
    /// <summary>Initializes a preflight failure with its safe public message and rejected path.</summary>
    /// <param name="message">Bounded failure description.</param>
    /// <param name="path">Staged or destination path rejected before commit.</param>
    public CoverageRunStagedCommitPreflightException(string message, string path)
        : base(message)
    {
        Path = path;
    }

    /// <summary>Gets the path rejected before any file in the set was promoted.</summary>
    public string Path { get; }
}
