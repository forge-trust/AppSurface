namespace ForgeTrust.AppSurface.Cli;

/// <summary>Writes one bounded watchdog artifact to its canonical or bootstrap destination.</summary>
internal interface ICoverageRunWatchdogArtifactWriter
{
    /// <summary>Attempts one atomic artifact replacement.</summary>
    /// <param name="destinationPath">Fully resolved destination path.</param>
    /// <param name="artifact">Normalized watchdog incident.</param>
    /// <param name="cancellationToken">External run cancellation token.</param>
    /// <returns>A bounded result that never contains exception text.</returns>
    Task<CoverageRunWatchdogArtifactWriteResult> WriteAsync(
        string destinationPath,
        CoverageRunWatchdogArtifact artifact,
        CancellationToken cancellationToken);
}

/// <summary>Describes the outcome of one watchdog artifact write attempt.</summary>
/// <param name="Written">Whether the canonical destination was atomically replaced.</param>
/// <param name="Detail">Allowlisted detail such as <c>writer-busy</c> or <c>artifact-write-timeout</c>.</param>
internal sealed record CoverageRunWatchdogArtifactWriteResult(bool Written, string? Detail)
{
    /// <summary>Gets a successful write result.</summary>
    public static CoverageRunWatchdogArtifactWriteResult Success { get; } = new(true, null);

    /// <summary>Gets the result returned while an earlier writer remains outstanding.</summary>
    public static CoverageRunWatchdogArtifactWriteResult WriterBusy { get; } = new(false, "writer-busy");

    /// <summary>Gets the result returned when the two-second write budget expires.</summary>
    public static CoverageRunWatchdogArtifactWriteResult TimedOut { get; } = new(false, "artifact-write-timeout");

    /// <summary>Gets a bounded result for a write failure that completed before the timeout.</summary>
    public static CoverageRunWatchdogArtifactWriteResult Failed { get; } = new(false, null);
}

/// <summary>
/// Serializes watchdog artifacts and atomically replaces a same-directory destination with one outstanding write at a time.
/// </summary>
internal sealed class CoverageRunWatchdogArtifactWriter : ICoverageRunWatchdogArtifactWriter
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly ICoverageRunWatchdogArtifactStorage _storage;
    private readonly ICoverageRunWatchdogDelay _delay;
    private Task? _activeWrite;

    /// <summary>Initializes a production writer.</summary>
    /// <param name="timeProvider">Clock used for the independent two-second write deadline.</param>
    public CoverageRunWatchdogArtifactWriter(TimeProvider timeProvider)
        : this(new CoverageRunWatchdogArtifactStorage(), new CoverageRunWatchdogDelay(timeProvider))
    {
    }

    /// <summary>Initializes a writer with deterministic storage and delay seams.</summary>
    /// <param name="storage">Atomic same-directory storage seam.</param>
    /// <param name="delay">Deadline delay seam.</param>
    internal CoverageRunWatchdogArtifactWriter(
        ICoverageRunWatchdogArtifactStorage storage,
        ICoverageRunWatchdogDelay delay)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    /// <inheritdoc />
    public async Task<CoverageRunWatchdogArtifactWriteResult> WriteAsync(
        string destinationPath,
        CoverageRunWatchdogArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(artifact);

        Task writeTask;
        var permission = new CoverageRunWatchdogCommitPermission();
        lock (_gate)
        {
            if (_activeWrite is { IsCompleted: false })
            {
                return CoverageRunWatchdogArtifactWriteResult.WriterBusy;
            }

            var bytes = CoverageRunWatchdogArtifactSerializer.Serialize(artifact);
            writeTask = _storage.WriteTemporaryAndCommitAsync(destinationPath, bytes, permission, cancellationToken);
            _activeWrite = writeTask;
        }

        var timeoutTask = _delay.DelayAsync(WriteTimeout, CancellationToken.None);
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(writeTask, timeoutTask, cancellationTask);
        if (completed == cancellationTask)
        {
            permission.Revoke();
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (completed == timeoutTask && !writeTask.IsCompleted)
        {
            permission.Revoke();
            ObserveFault(writeTask);
            return CoverageRunWatchdogArtifactWriteResult.TimedOut;
        }

        try
        {
            await writeTask;
            return permission.WasCommitted
                ? CoverageRunWatchdogArtifactWriteResult.Success
                : CoverageRunWatchdogArtifactWriteResult.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            permission.Revoke();
            throw;
        }
        catch
        {
            permission.Revoke();
            return CoverageRunWatchdogArtifactWriteResult.Failed;
        }
    }

    private static void ObserveFault(Task writeTask)
        => _ = writeTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

/// <summary>Allows a timed writer to revoke a late task's authority to replace the destination.</summary>
internal sealed class CoverageRunWatchdogCommitPermission
{
    private const int Allowed = 0;
    private const int Committing = 1;
    private const int Revoked = 2;
    private const int Committed = 3;

    private int _state;

    /// <summary>Gets whether the destination replacement completed.</summary>
    public bool WasCommitted => Volatile.Read(ref _state) == Committed;

    /// <summary>Atomically reserves commit authority unless the deadline already revoked it.</summary>
    /// <returns>Whether the caller owns the commit.</returns>
    public bool TryBeginCommit() => Interlocked.CompareExchange(ref _state, Committing, Allowed) == Allowed;

    /// <summary>Marks a reserved destination replacement complete.</summary>
    public void CompleteCommit()
    {
        if (Interlocked.CompareExchange(ref _state, Committed, Committing) != Committing)
        {
            throw new InvalidOperationException("Watchdog artifact commit was not reserved.");
        }
    }

    /// <summary>Revokes a write that has not begun its atomic destination replacement.</summary>
    public void Revoke() => Interlocked.CompareExchange(ref _state, Revoked, Allowed);
}

/// <summary>Abstracts same-directory temporary storage for deterministic artifact-writer tests.</summary>
internal interface ICoverageRunWatchdogArtifactStorage
{
    /// <summary>Writes, flushes, and conditionally commits one serialized artifact.</summary>
    /// <param name="destinationPath">Canonical destination path.</param>
    /// <param name="bytes">Bounded serialized artifact.</param>
    /// <param name="permission">Revocable destination commit permission.</param>
    /// <param name="cancellationToken">External cancellation token.</param>
    /// <returns>A task that completes after commit or temporary cleanup.</returns>
    Task WriteTemporaryAndCommitAsync(
        string destinationPath,
        ReadOnlyMemory<byte> bytes,
        CoverageRunWatchdogCommitPermission permission,
        CancellationToken cancellationToken);
}

/// <summary>Provides the production same-directory temporary-file and atomic-replacement behavior.</summary>
internal sealed class CoverageRunWatchdogArtifactStorage : ICoverageRunWatchdogArtifactStorage
{
    /// <inheritdoc />
    public async Task WriteTemporaryAndCommitAsync(
        string destinationPath,
        ReadOnlyMemory<byte> bytes,
        CoverageRunWatchdogCommitPermission permission,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The watchdog artifact destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Join(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (permission.TryBeginCommit())
            {
                File.Move(temporaryPath, fullPath, overwrite: true);
                permission.CompleteCommit();
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Temporary cleanup is best effort and never replaces the bounded public result.
            }
        }
    }
}

/// <summary>Abstracts the artifact deadline for deterministic tests.</summary>
internal interface ICoverageRunWatchdogDelay
{
    /// <summary>Waits for a deadline.</summary>
    /// <param name="delay">Requested delay.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task completing at the deadline.</returns>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Implements artifact deadlines with the injected run clock.</summary>
internal sealed class CoverageRunWatchdogDelay : ICoverageRunWatchdogDelay
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a delay provider.</summary>
    /// <param name="timeProvider">Clock used to create timers.</param>
    public CoverageRunWatchdogDelay(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, _timeProvider, cancellationToken);
}
