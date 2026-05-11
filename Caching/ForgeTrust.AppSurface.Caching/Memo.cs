using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Caching.Memory;

namespace ForgeTrust.AppSurface.Caching;

/// <summary>
/// Memoizes async factory calls using <see cref="IMemoryCache"/> with automatic key generation
/// and per-key thundering-herd protection.
/// </summary>
public sealed class Memo : IMemo, IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<object, SemaphoreSlim> _locks = new();
    private readonly TimeSpan _failureCacheDuration;
    private int _disposed;

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(Memo));
        }
    }

    /// <summary>
    /// Default duration for which a factory failure is cached to prevent serial thundering herd
    /// on repeated failures.
    /// </summary>
    private static readonly TimeSpan DefaultFailureCacheDuration = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Sentinel wrapper stored in the cache when a factory invocation fails,
    /// allowing subsequent waiters to short-circuit instead of re-invoking the factory.
    /// </summary>
    private sealed class CachedFailure
    {
        public Exception Exception { get; }

        public CachedFailure(Exception exception)
        {
            Exception = exception;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Memo"/> class.
    /// </summary>
    /// <param name="cache">The underlying memory cache for storage.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cache"/> is null.</exception>
    public Memo(IMemoryCache cache)
        : this(cache, DefaultFailureCacheDuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Memo"/> class with a configurable failure cache duration.
    /// </summary>
    /// <param name="cache">The underlying memory cache for storage.</param>
    /// <param name="failureCacheDuration">
    /// Duration for which a factory failure is cached to prevent serial thundering herd on repeated failures.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cache"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="failureCacheDuration"/> is less than or equal to <see cref="TimeSpan.Zero"/>.
    /// </exception>
    public Memo(IMemoryCache cache, TimeSpan failureCacheDuration)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

        if (failureCacheDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureCacheDuration),
                failureCacheDuration,
                "Failure cache duration must be a positive value.");
        }

        _failureCacheDuration = failureCacheDuration;
    }

    // ── Zero arguments ──────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TResult> GetAsync<TResult>(
        Func<Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = (callerFilePath, callerLineNumber, typeof(TResult));

        return GetOrCreateCoreAsync(key, factory, static (f, _) => f(), policy, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TResult> GetAsync<TResult>(
        Func<CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = (callerFilePath, callerLineNumber, typeof(TResult));

        return GetOrCreateCoreAsync(
            key,
            (factory, cancellationToken), // cancellationToken is duplicated in state to allow static lambda access
            static (state, _) => state.factory(state.cancellationToken),
            policy,
            cancellationToken);
    }

    // ── One argument ────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg, TResult>(
        TArg arg,
        Func<TArg, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg>(callerFilePath, callerLineNumber, arg);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg),
            static (state, _) => state.factory(state.arg),
            policy,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg, TResult>(
        TArg arg,
        Func<TArg, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg>(callerFilePath, callerLineNumber, arg);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg, cancellationToken), // cancellationToken is duplicated in state to allow static lambda access
            static (state, _) => state.factory(state.arg, state.cancellationToken),
            policy,
            cancellationToken);
    }

    // ── Two arguments ───────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        Func<TArg1, TArg2, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2>(callerFilePath, callerLineNumber, arg1, arg2);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2),
            static (state, _) => state.factory(state.arg1, state.arg2),
            policy,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        Func<TArg1, TArg2, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2>(callerFilePath, callerLineNumber, arg1, arg2);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2,
                cancellationToken), // cancellationToken is duplicated in state to allow static lambda access
            static (state, _) => state.factory(state.arg1, state.arg2, state.cancellationToken),
            policy,
            cancellationToken);
    }

    // ── Three arguments ─────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TArg3, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        Func<TArg1, TArg2, TArg3, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2, TArg3>(callerFilePath, callerLineNumber, arg1, arg2, arg3);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2, arg3),
            static (state, _) => state.factory(state.arg1, state.arg2, state.arg3),
            policy,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TArg3, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        Func<TArg1, TArg2, TArg3, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2, TArg3>(callerFilePath, callerLineNumber, arg1, arg2, arg3);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2, arg3,
                cancellationToken), // cancellationToken is duplicated in state to allow static lambda access
            static (state, _) => state.factory(state.arg1, state.arg2, state.arg3, state.cancellationToken),
            policy,
            cancellationToken);
    }

    // ── Four arguments ──────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        Func<TArg1, TArg2, TArg3, TArg4, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2, TArg3, TArg4>(
            callerFilePath,
            callerLineNumber,
            arg1,
            arg2,
            arg3,
            arg4);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2, arg3, arg4),
            static (state, _) => state.factory(state.arg1, state.arg2, state.arg3, state.arg4),
            policy,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        Func<TArg1, TArg2, TArg3, TArg4, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2, TArg3, TArg4>(
            callerFilePath,
            callerLineNumber,
            arg1,
            arg2,
            arg3,
            arg4);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2, arg3, arg4,
                cancellationToken), // cancellationToken is duplicated in state to allow static lambda access
            static (state, _) => state.factory(state.arg1, state.arg2, state.arg3, state.arg4, state.cancellationToken),
            policy,
            cancellationToken);
    }

    // ── Five arguments ──────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        Func<TArg1, TArg2, TArg3, TArg4, TArg5, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2, TArg3, TArg4, TArg5>(
            callerFilePath,
            callerLineNumber,
            arg1,
            arg2,
            arg3,
            arg4,
            arg5);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2, arg3, arg4, arg5),
            static (state, _) => state.factory(state.arg1, state.arg2, state.arg3, state.arg4, state.arg5),
            policy,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        Func<TArg1, TArg2, TArg3, TArg4, TArg5, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2, TArg3, TArg4, TArg5>(
            callerFilePath,
            callerLineNumber,
            arg1,
            arg2,
            arg3,
            arg4,
            arg5);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2, arg3, arg4, arg5,
                cancellationToken), // cancellationToken is duplicated in state to allow static lambda access
            static (state, _) => state.factory(
                state.arg1,
                state.arg2,
                state.arg3,
                state.arg4,
                state.arg5,
                state.cancellationToken),
            policy,
            cancellationToken);
    }

    // ── Six arguments ───────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        TArg6 arg6,
        Func<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2, TArg3, TArg4, TArg5, TArg6>(
            callerFilePath,
            callerLineNumber,
            arg1,
            arg2,
            arg3,
            arg4,
            arg5,
            arg6);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2, arg3, arg4, arg5, arg6),
            static (state, _) => state.factory(state.arg1, state.arg2, state.arg3, state.arg4, state.arg5, state.arg6),
            policy,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        TArg6 arg6,
        Func<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2, TArg3, TArg4, TArg5, TArg6>(
            callerFilePath,
            callerLineNumber,
            arg1,
            arg2,
            arg3,
            arg4,
            arg5,
            arg6);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2, arg3, arg4, arg5, arg6,
                cancellationToken), // cancellationToken is duplicated in state to allow static lambda access
            static (state, _) => state.factory(
                state.arg1,
                state.arg2,
                state.arg3,
                state.arg4,
                state.arg5,
                state.arg6,
                state.cancellationToken),
            policy,
            cancellationToken);
    }

    // ── Seven arguments ─────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        TArg6 arg6,
        TArg7 arg7,
        Func<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7>(
            callerFilePath,
            callerLineNumber,
            arg1,
            arg2,
            arg3,
            arg4,
            arg5,
            arg6,
            arg7);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2, arg3, arg4, arg5, arg6, arg7),
            static (state, _) => state.factory(
                state.arg1,
                state.arg2,
                state.arg3,
                state.arg4,
                state.arg5,
                state.arg6,
                state.arg7),
            policy,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        TArg6 arg6,
        TArg7 arg7,
        Func<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7>(
            callerFilePath,
            callerLineNumber,
            arg1,
            arg2,
            arg3,
            arg4,
            arg5,
            arg6,
            arg7);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2, arg3, arg4, arg5, arg6, arg7,
                cancellationToken), // cancellationToken is duplicated in state to allow static lambda access
            static (state, _) => state.factory(
                state.arg1,
                state.arg2,
                state.arg3,
                state.arg4,
                state.arg5,
                state.arg6,
                state.arg7,
                state.cancellationToken),
            policy,
            cancellationToken);
    }

    // ── Eight arguments ─────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        TArg6 arg6,
        TArg7 arg7,
        TArg8 arg8,
        Func<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8>(
            callerFilePath,
            callerLineNumber,
            arg1,
            arg2,
            arg3,
            arg4,
            arg5,
            arg6,
            arg7,
            arg8);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8),
            static (state, _) => state.factory(
                state.arg1,
                state.arg2,
                state.arg3,
                state.arg4,
                state.arg5,
                state.arg6,
                state.arg7,
                state.arg8),
            policy,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        TArg6 arg6,
        TArg7 arg7,
        TArg8 arg8,
        Func<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(policy);

        var key = BuildKey<TResult, TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8>(
            callerFilePath,
            callerLineNumber,
            arg1,
            arg2,
            arg3,
            arg4,
            arg5,
            arg6,
            arg7,
            arg8);

        return GetOrCreateCoreAsync(
            key,
            (factory, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8,
                cancellationToken), // cancellationToken is duplicated in state to allow static lambda access
            static (state, _) => state.factory(
                state.arg1,
                state.arg2,
                state.arg3,
                state.arg4,
                state.arg5,
                state.arg6,
                state.arg7,
                state.arg8,
                state.cancellationToken),
            policy,
            cancellationToken);
    }

    // ── Core ────────────────────────────────────────────────────────────

    /// <summary>
    /// Core implementation that handles cache lookup, thundering-herd synchronization,
    /// exception caching, and entry creation with the configured policy.
    /// Uses a state-passing pattern to avoid closure allocations on the hot path.
    /// <paramref name="key"/> can be a <see cref="ValueTuple"/> for efficiency.
    /// </summary>
    /// <remarks>
    /// <c>null</c> results from the factory are treated as valid cached values for reference types.
    /// </remarks>
    private async Task<TResult> GetOrCreateCoreAsync<TState, TResult>(
        object key,
        TState state,
        Func<TState, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // Fast path: check cache without acquiring the lock.
        // The cache may contain either a TResult value or a CachedFailure sentinel.
        if (_cache.TryGetValue(key, out object? cached))
        {
            if (cached is CachedFailure failure)
            {
                ExceptionDispatchInfo.Capture(failure.Exception).Throw();
            }

            return (TResult)cached!;
        }

        // Slow path: acquire per-key lock to prevent thundering herd
        var keyLock = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        try
        {
            await keyLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // If WaitAsync is canceled, try to remove the lock entry from _locks
            // to prevent unbounded growth if this was the last waiter for this specific key.
            // We only remove if it's still the same instance we were waiting on.
            _locks.TryRemove(new KeyValuePair<object, SemaphoreSlim>(key, keyLock));

            throw;
        }

        ThrowIfDisposed();
        try
        {
            // Double-check after acquiring lock
            if (_cache.TryGetValue(key, out cached))
            {
                if (cached is CachedFailure failure)
                {
                    ExceptionDispatchInfo.Capture(failure.Exception).Throw();
                }

                return (TResult)cached!;
            }

            TResult result;
            try
            {
                result = await factory(state, cancellationToken);
                ThrowIfDisposed();
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
                // Cache the failure with a short TTL so subsequent waiters short-circuit
                // instead of re-invoking the factory (prevents serial thundering herd).
                var failureOptions = CreateEntryOptions(keyLock);
                failureOptions.AbsoluteExpirationRelativeToNow = _failureCacheDuration;
                _cache.Set(key, new CachedFailure(ex), failureOptions);

                throw;
            }

            var options = CreateEntryOptions(keyLock);

            if (policy.AbsoluteExpiration.HasValue)
            {
                options.AbsoluteExpirationRelativeToNow = policy.AbsoluteExpiration.Value;
            }

            if (policy.SlidingExpiration.HasValue)
            {
                options.SlidingExpiration = policy.SlidingExpiration.Value;
            }

            _cache.Set(key, result, options);

            // Eagerly remove the semaphore now that the value is cached.
            // Subsequent accesses hit the fast path and skip the lock entirely.
            // The eviction callback remains as a safety net for the failure-cache path.
            _locks.TryRemove(key, out _);

            return result;
        }
        finally
        {
            try
            {
                keyLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Ignore: the Memo instance or the lock was disposed concurrently.
            }
        }
    }

    /// <summary>
    /// Creates <see cref="MemoryCacheEntryOptions"/> with a post-eviction callback
    /// that removes the corresponding <see cref="SemaphoreSlim"/> from
    /// <see cref="_locks"/> to prevent unbounded growth.
    /// </summary>
    /// <param name="keyLock">The specific semaphore instance to capture for reference-safe eviction cleanup.</param>
    private MemoryCacheEntryOptions CreateEntryOptions(SemaphoreSlim keyLock)
    {
        var options = new MemoryCacheEntryOptions();
        options.PostEvictionCallbacks.Add(
            new PostEvictionCallbackRegistration
            {
                EvictionCallback = OnCacheEntryEvicted,
                State = (_locks, keyLock)
            });

        return options;
    }

    /// <summary>
    /// Post-eviction callback that cleans up the per-key semaphore from the locks dictionary.
    /// Only removes the exact semaphore instance that was captured when the cache entry was created,
    /// preserving any newer semaphore created by concurrent callers via <c>GetOrAdd</c>.
    /// </summary>
    private static void OnCacheEntryEvicted(
        object key,
        object? value,
        EvictionReason reason,
        object? state)
    {
        if (state is not (ConcurrentDictionary<object, SemaphoreSlim> locks, SemaphoreSlim captured))
        {
            return;
        }

        // Atomically remove only the exact (key, semaphore) pair that was captured
        // when this cache entry was created. If a concurrent GetOrAdd has since
        // replaced the semaphore, this is a no-op — the newer instance is preserved,
        // maintaining the thundering-herd guarantee.
        // We intentionally do not Dispose the semaphore because a concurrent
        // WaitAsync caller may still hold a reference.
        ((ICollection<KeyValuePair<object, SemaphoreSlim>>)locks)
            .Remove(new KeyValuePair<object, SemaphoreSlim>(key, captured));
    }

    /// <summary>
    /// Disposes the underlying locks.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var keyLock in _locks.Values)
        {
            keyLock.Dispose();
        }

        _locks.Clear();
    }

    // Uses ValueTuple objects (e.g., (prefix, arg1, arg2)) as cache keys.
    // These provide straightforward, type-safe identity for the cache.
    // Note: ValueTuples used as 'object' in the dictionary will be boxed,
    // resulting in one allocation per key construction during a cache miss or sync-point acquisition.
    // Equality and HashCode are handled naturally by the tuple implementation,
    // and collisions are resolved as per standard ConcurrentDictionary behavior.


    internal static object BuildKey<TResult, TArg>(string path, int line, TArg arg) =>
        (path, line, typeof(TResult), arg);

    internal static object BuildKey<TResult, TArg1, TArg2>(
        string path,
        int line,
        TArg1 arg1,
        TArg2 arg2) =>
        (path, line, typeof(TResult), arg1, arg2);

    internal static object BuildKey<TResult, TArg1, TArg2, TArg3>(
        string path,
        int line,
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3) =>
        (path, line, typeof(TResult), arg1, arg2, arg3);

    internal static object BuildKey<TResult, TArg1, TArg2, TArg3, TArg4>(
        string path,
        int line,
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4) =>
        (path, line, typeof(TResult), arg1, arg2, arg3, arg4);

    internal static object BuildKey<TResult, TArg1, TArg2, TArg3, TArg4, TArg5>(
        string path,
        int line,
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5) =>
        (path, line, typeof(TResult), arg1, arg2, arg3, arg4, arg5);

    internal static object BuildKey<TResult, TArg1, TArg2, TArg3, TArg4, TArg5, TArg6>(
        string path,
        int line,
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        TArg6 arg6) =>
        (path, line, typeof(TResult), arg1, arg2, arg3, arg4, arg5, arg6);

    internal static object BuildKey<TResult, TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7>(
        string path,
        int line,
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        TArg6 arg6,
        TArg7 arg7) =>
        (path, line, typeof(TResult), arg1, arg2, arg3, arg4, arg5, arg6, arg7);

    internal static object BuildKey<TResult, TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8>(
        string path,
        int line,
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        TArg6 arg6,
        TArg7 arg7,
        TArg8 arg8) =>
        (path, line, typeof(TResult), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8);
}
