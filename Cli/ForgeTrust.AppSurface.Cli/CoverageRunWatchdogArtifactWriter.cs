namespace ForgeTrust.AppSurface.Cli;

/// <summary>Writes one bounded watchdog artifact to its canonical or bootstrap destination.</summary>
internal interface ICoverageRunWatchdogArtifactWriter
{
    /// <summary>Attempts one atomic artifact replacement.</summary>
    /// <param name="destinationPath">Fully resolved destination path.</param>
    /// <param name="artifact">Normalized watchdog incident.</param>
    /// <param name="cancellationToken">Cancellation token that can revoke staging before canonical commit begins.</param>
    /// <returns>
    /// A result whose private staging phase is bounded and that never contains exception text.
    /// If the final same-directory rename has already begun, the call waits for that non-cancellable
    /// metadata operation so it cannot report a timeout followed by a late canonical publication.
    /// </returns>
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

    /// <summary>Gets the result returned when the two-second staging budget expires before commit reservation.</summary>
    public static CoverageRunWatchdogArtifactWriteResult TimedOut { get; } = new(false, "artifact-write-timeout");

    /// <summary>Gets a bounded result for a write failure that completed before the timeout.</summary>
    public static CoverageRunWatchdogArtifactWriteResult Failed { get; } = new(false, null);
}

/// <summary>
/// Serializes watchdog artifacts, bounds private staging, and atomically replaces a same-directory destination with one outstanding write at a time.
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
        Task timeoutTask;
        var permission = new CoverageRunWatchdogCommitPermission();
        lock (_gate)
        {
            if (_activeWrite is { IsCompleted: false })
            {
                return CoverageRunWatchdogArtifactWriteResult.WriterBusy;
            }

            byte[] bytes;
            try
            {
                bytes = CoverageRunWatchdogArtifactSerializer.Serialize(artifact);
            }
            catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
            {
                return CoverageRunWatchdogArtifactWriteResult.Failed;
            }

            timeoutTask = _delay.DelayAsync(WriteTimeout, CancellationToken.None);
            writeTask = Task.Run(
                () => WriteTemporaryAndCommitAsync(destinationPath, bytes, permission, cancellationToken),
                CancellationToken.None);
            _activeWrite = writeTask;
        }

        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(writeTask, timeoutTask, cancellationTask);
        if (completed == cancellationTask)
        {
            if (permission.TryRevoke())
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            completed = writeTask;
        }

        if (completed == timeoutTask && !writeTask.IsCompleted)
        {
            if (!permission.TryRevoke())
            {
                // The same-directory atomic replacement has already started. It cannot be
                // canceled safely, so do not report a timeout that could be followed by a
                // late canonical publication. Await the commit and report its real result.
            }
            else
            {
                ObserveFault(writeTask);
                return CoverageRunWatchdogArtifactWriteResult.TimedOut;
            }
        }

        try
        {
            await writeTask;
            cancellationToken.ThrowIfCancellationRequested();

            return permission.WasCommitted
                ? CoverageRunWatchdogArtifactWriteResult.Success
                : CoverageRunWatchdogArtifactWriteResult.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            permission.TryRevoke();
            throw;
        }
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            permission.TryRevoke();
            return CoverageRunWatchdogArtifactWriteResult.Failed;
        }
    }

    private async Task WriteTemporaryAndCommitAsync(
        string destinationPath,
        ReadOnlyMemory<byte> bytes,
        CoverageRunWatchdogCommitPermission permission,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            temporaryPath = await _storage.WriteTemporaryAsync(destinationPath, bytes, cancellationToken);
            if (permission.TryBeginCommit())
            {
                // No await or injectable callback may occur between reserving commit authority
                // and the same-directory atomic rename. A timeout can therefore revoke staging,
                // but can never be reported while a background task still has authority to publish.
                _storage.Commit(temporaryPath, destinationPath);
                permission.CompleteCommit();
            }
        }
        finally
        {
            if (temporaryPath is not null)
            {
                _storage.DeleteTemporary(temporaryPath);
            }
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
    /// <returns><see langword="true"/> when commit authority was revoked; otherwise the commit already began or completed.</returns>
    public bool TryRevoke() => Interlocked.CompareExchange(ref _state, Revoked, Allowed) == Allowed;
}

/// <summary>Abstracts same-directory temporary storage for deterministic artifact-writer tests.</summary>
internal interface ICoverageRunWatchdogArtifactStorage
{
    /// <summary>Writes and flushes one serialized artifact to a unique same-directory temporary path.</summary>
    /// <param name="destinationPath">Canonical destination path.</param>
    /// <param name="bytes">Bounded serialized artifact.</param>
    /// <param name="cancellationToken">External cancellation token.</param>
    /// <returns>The private temporary path after all bytes have been flushed.</returns>
    Task<string> WriteTemporaryAsync(
        string destinationPath,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken);

    /// <summary>Atomically renames one fully flushed same-directory temporary file to its canonical destination.</summary>
    /// <param name="temporaryPath">Fully flushed private temporary path.</param>
    /// <param name="destinationPath">Canonical destination path.</param>
    void Commit(string temporaryPath, string destinationPath);

    /// <summary>Best-effort deletes one private temporary file.</summary>
    /// <param name="temporaryPath">Private temporary path.</param>
    void DeleteTemporary(string temporaryPath);
}

/// <summary>Provides the production same-directory temporary-file and atomic-replacement behavior.</summary>
internal sealed class CoverageRunWatchdogArtifactStorage : ICoverageRunWatchdogArtifactStorage
{
    /// <inheritdoc />
    public async Task<string> WriteTemporaryAsync(
        string destinationPath,
        ReadOnlyMemory<byte> bytes,
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

            return temporaryPath;
        }
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            DeleteTemporary(temporaryPath);
            throw;
        }
    }

    /// <inheritdoc />
    public void Commit(string temporaryPath, string destinationPath)
        => File.Move(temporaryPath, Path.GetFullPath(destinationPath), overwrite: true);

    /// <inheritdoc />
    public void DeleteTemporary(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            // Temporary cleanup is best effort and never replaces the bounded public result.
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
