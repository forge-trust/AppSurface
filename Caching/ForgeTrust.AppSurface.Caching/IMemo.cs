using System.Runtime.CompilerServices;

namespace ForgeTrust.AppSurface.Caching;

/// <summary>
/// Provides memoized, cache-backed access to expensive computations with automatic
/// key generation and thundering-herd protection.
/// </summary>
public interface IMemo
{
    // ── Zero arguments ──────────────────────────────────────────────────

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path and line number.
    /// </summary>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="factory">The async factory to compute the value on a cache miss.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel the cache lock acquisition.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TResult>(
        Func<Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0);

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path and line number.
    /// The <see cref="CancellationToken"/> is forwarded to the factory for cooperative cancellation.
    /// </summary>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="factory">The async factory to compute the value on a cache miss. Receives a <see cref="CancellationToken"/>.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel both lock acquisition and factory invocation.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TResult>(
        Func<CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0);

    // ── One argument ────────────────────────────────────────────────────

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and <paramref name="arg"/>.
    /// </summary>
    /// <typeparam name="TArg">The type of the argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg">The argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel the cache lock acquisition.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg, TResult>(
        TArg arg,
        Func<TArg, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0);

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and <paramref name="arg"/>.
    /// The <see cref="CancellationToken"/> is forwarded to the factory for cooperative cancellation.
    /// </summary>
    /// <typeparam name="TArg">The type of the argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg">The argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss. Receives a <see cref="CancellationToken"/>.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel both lock acquisition and factory invocation.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg, TResult>(
        TArg arg,
        Func<TArg, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0);

    // ── Two arguments ───────────────────────────────────────────────────

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel the cache lock acquisition.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        Func<TArg1, TArg2, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0);

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// The <see cref="CancellationToken"/> is forwarded to the factory for cooperative cancellation.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss. Receives a <see cref="CancellationToken"/>.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel both lock acquisition and factory invocation.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        Func<TArg1, TArg2, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0);

    // ── Three arguments ─────────────────────────────────────────────────

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TArg3">The type of the third argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg3">The third argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel the cache lock acquisition.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TArg3, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        Func<TArg1, TArg2, TArg3, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0);

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// The <see cref="CancellationToken"/> is forwarded to the factory for cooperative cancellation.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TArg3">The type of the third argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg3">The third argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss. Receives a <see cref="CancellationToken"/>.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel both lock acquisition and factory invocation.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TArg3, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        Func<TArg1, TArg2, TArg3, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0);

    // ── Four arguments ──────────────────────────────────────────────────

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TArg3">The type of the third argument.</typeparam>
    /// <typeparam name="TArg4">The type of the fourth argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg3">The third argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg4">The fourth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel the cache lock acquisition.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        Func<TArg1, TArg2, TArg3, TArg4, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0);

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// The <see cref="CancellationToken"/> is forwarded to the factory for cooperative cancellation.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TArg3">The type of the third argument.</typeparam>
    /// <typeparam name="TArg4">The type of the fourth argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg3">The third argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg4">The fourth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss. Receives a <see cref="CancellationToken"/>.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel both lock acquisition and factory invocation.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        Func<TArg1, TArg2, TArg3, TArg4, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0);

    // ── Five arguments ──────────────────────────────────────────────────

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TArg3">The type of the third argument.</typeparam>
    /// <typeparam name="TArg4">The type of the fourth argument.</typeparam>
    /// <typeparam name="TArg5">The type of the fifth argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg3">The third argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg4">The fourth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg5">The fifth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel the cache lock acquisition.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        Func<TArg1, TArg2, TArg3, TArg4, TArg5, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0);

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// The <see cref="CancellationToken"/> is forwarded to the factory for cooperative cancellation.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TArg3">The type of the third argument.</typeparam>
    /// <typeparam name="TArg4">The type of the fourth argument.</typeparam>
    /// <typeparam name="TArg5">The type of the fifth argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg3">The third argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg4">The fourth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg5">The fifth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss. Receives a <see cref="CancellationToken"/>.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel both lock acquisition and factory invocation.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TResult>(
        TArg1 arg1,
        TArg2 arg2,
        TArg3 arg3,
        TArg4 arg4,
        TArg5 arg5,
        Func<TArg1, TArg2, TArg3, TArg4, TArg5, CancellationToken, Task<TResult>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0);

    // ── Six arguments ───────────────────────────────────────────────────

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TArg3">The type of the third argument.</typeparam>
    /// <typeparam name="TArg4">The type of the fourth argument.</typeparam>
    /// <typeparam name="TArg5">The type of the fifth argument.</typeparam>
    /// <typeparam name="TArg6">The type of the sixth argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg3">The third argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg4">The fourth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg5">The fifth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg6">The sixth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel the cache lock acquisition.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TResult>(
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
        [CallerLineNumber] int callerLineNumber = 0);

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// The <see cref="CancellationToken"/> is forwarded to the factory for cooperative cancellation.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TArg3">The type of the third argument.</typeparam>
    /// <typeparam name="TArg4">The type of the fourth argument.</typeparam>
    /// <typeparam name="TArg5">The type of the fifth argument.</typeparam>
    /// <typeparam name="TArg6">The type of the sixth argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg3">The third argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg4">The fourth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg5">The fifth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg6">The sixth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss. Receives a <see cref="CancellationToken"/>.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel both lock acquisition and factory invocation.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TResult>(
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
        [CallerLineNumber] int callerLineNumber = 0);

    // ── Seven arguments ─────────────────────────────────────────────────

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TArg3">The type of the third argument.</typeparam>
    /// <typeparam name="TArg4">The type of the fourth argument.</typeparam>
    /// <typeparam name="TArg5">The type of the fifth argument.</typeparam>
    /// <typeparam name="TArg6">The type of the sixth argument.</typeparam>
    /// <typeparam name="TArg7">The type of the seventh argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg3">The third argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg4">The fourth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg5">The fifth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg6">The sixth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg7">The seventh argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel the cache lock acquisition.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TResult>(
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
        [CallerLineNumber] int callerLineNumber = 0);

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// The <see cref="CancellationToken"/> is forwarded to the factory for cooperative cancellation.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TArg3">The type of the third argument.</typeparam>
    /// <typeparam name="TArg4">The type of the fourth argument.</typeparam>
    /// <typeparam name="TArg5">The type of the fifth argument.</typeparam>
    /// <typeparam name="TArg6">The type of the sixth argument.</typeparam>
    /// <typeparam name="TArg7">The type of the seventh argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg3">The third argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg4">The fourth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg5">The fifth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg6">The sixth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg7">The seventh argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss. Receives a <see cref="CancellationToken"/>.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel both lock acquisition and factory invocation.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TResult>(
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
        [CallerLineNumber] int callerLineNumber = 0);

    // ── Eight arguments ─────────────────────────────────────────────────

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TArg3">The type of the third argument.</typeparam>
    /// <typeparam name="TArg4">The type of the fourth argument.</typeparam>
    /// <typeparam name="TArg5">The type of the fifth argument.</typeparam>
    /// <typeparam name="TArg6">The type of the sixth argument.</typeparam>
    /// <typeparam name="TArg7">The type of the seventh argument.</typeparam>
    /// <typeparam name="TArg8">The type of the eighth argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg3">The third argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg4">The fourth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg5">The fifth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg6">The sixth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg7">The seventh argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg8">The eighth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel the cache lock acquisition.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8, TResult>(
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
        [CallerLineNumber] int callerLineNumber = 0);

    /// <summary>
    /// Returns a cached result or invokes <paramref name="factory"/> to compute and cache it.
    /// The cache key is automatically derived from the caller file path, line number, and the provided arguments.
    /// The <see cref="CancellationToken"/> is forwarded to the factory for cooperative cancellation.
    /// </summary>
    /// <typeparam name="TArg1">The type of the first argument.</typeparam>
    /// <typeparam name="TArg2">The type of the second argument.</typeparam>
    /// <typeparam name="TArg3">The type of the third argument.</typeparam>
    /// <typeparam name="TArg4">The type of the fourth argument.</typeparam>
    /// <typeparam name="TArg5">The type of the fifth argument.</typeparam>
    /// <typeparam name="TArg6">The type of the sixth argument.</typeparam>
    /// <typeparam name="TArg7">The type of the seventh argument.</typeparam>
    /// <typeparam name="TArg8">The type of the eighth argument.</typeparam>
    /// <typeparam name="TResult">The type of the cached value.</typeparam>
    /// <param name="arg1">The first argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg2">The second argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg3">The third argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg4">The fourth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg5">The fifth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg6">The sixth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg7">The seventh argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="arg8">The eighth argument passed to the factory and incorporated into the cache key.</param>
    /// <param name="factory">The async factory to compute the value on a cache miss. Receives a <see cref="CancellationToken"/>.</param>
    /// <param name="policy">The cache expiration policy.</param>
    /// <param name="cancellationToken">A token to cancel both lock acquisition and factory invocation.</param>
    /// <param name="callerFilePath">Compiler-injected caller file path. Do not supply manually.</param>
    /// <param name="callerLineNumber">Compiler-injected caller line number. Do not supply manually.</param>
    /// <returns>The cached or freshly computed value.</returns>
    Task<TResult> GetAsync<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TArg7, TArg8, TResult>(
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
        [CallerLineNumber] int callerLineNumber = 0);
}
