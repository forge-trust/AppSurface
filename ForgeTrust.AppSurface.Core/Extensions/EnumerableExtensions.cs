using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Core.Extensions;

/// <summary>
/// Provides extension methods for <see cref="IEnumerable{T}"/> to perform parallel operations.
/// </summary>
public static partial class EnumerableExtensions
{
    /// <summary>
    /// Projects each element of a sequence into a new form with bounded concurrency and preserves the input order of results.
    /// </summary>
    /// <param name="source">The sequence of input items.</param>
    /// <param name="body">An asynchronous transform that receives an input item and a cancellation token and produces a result.</param>
    /// <param name="maxDegreeOfParallelism">The maximum number of concurrent invocations of <paramref name="body"/>; must be greater than zero.</param>
    /// <param name="cancellationToken">A token to observe while waiting for tasks to complete.</param>
    /// <returns>An <see cref="IEnumerable{T}"/> of results in the same order as the input sequence.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxDegreeOfParallelism"/> is less than or equal to zero.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled via <paramref name="cancellationToken"/>.</exception>
    /// <remarks>Exceptions thrown by the <paramref name="body"/> function propagate to the returned task.</remarks>
    public static async Task<IEnumerable<TResult>> ParallelSelectAsync<TSource, TResult>(
        this IEnumerable<TSource> source,
        Func<TSource, CancellationToken, Task<TResult>> body,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken = default)
    {
        return await ParallelSelectAsyncCore(
            source,
            body,
            maxDegreeOfParallelism,
            cancellationToken,
            logger: null);
    }

    /// <summary>
    /// Projects each element of a sequence into a new form with bounded concurrency, preserves the input order of results, observes <paramref name="cancellationToken"/>, and logs suppressed cleanup diagnostics through <paramref name="logger"/>.
    /// </summary>
    /// <param name="source">The sequence of input items.</param>
    /// <param name="body">An asynchronous transform that receives an input item and a cancellation token and produces a result.</param>
    /// <param name="maxDegreeOfParallelism">The maximum number of concurrent invocations of <paramref name="body"/>; must be greater than zero.</param>
    /// <param name="cancellationToken">A token to observe while waiting for tasks to complete.</param>
    /// <param name="logger">Logger for cleanup diagnostics that would otherwise be suppressed to preserve the primary exception.</param>
    /// <returns>An <see cref="IEnumerable{T}"/> of results in the same order as the input sequence.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/>, <paramref name="body"/>, or <paramref name="logger"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxDegreeOfParallelism"/> is less than or equal to zero.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled via <paramref name="cancellationToken"/>.</exception>
    /// <remarks>Exceptions thrown by the <paramref name="body"/> function propagate to the returned task.</remarks>
    public static async Task<IEnumerable<TResult>> ParallelSelectAsync<TSource, TResult>(
        this IEnumerable<TSource> source,
        Func<TSource, CancellationToken, Task<TResult>> body,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return await ParallelSelectAsyncCore(
            source,
            body,
            maxDegreeOfParallelism,
            cancellationToken,
            logger);
    }

    private static async Task<IEnumerable<TResult>> ParallelSelectAsyncCore<TSource, TResult>(
        IEnumerable<TSource> source,
        Func<TSource, CancellationToken, Task<TResult>> body,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken,
        ILogger? logger)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (maxDegreeOfParallelism <= 0) throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));

        var results = new ConcurrentDictionary<long, TResult>();
        using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);
        var tasks = new List<Task>();
        long index = 0;

        try
        {
            foreach (var item in source)
            {
                var capturedIndex = index++;
                await semaphore.WaitAsync(cancellationToken);

                tasks.Add(
                    Task.Run(
                        async () =>
                        {
                            try
                            {
                                var result = await body(item, cancellationToken);
                                results.TryAdd(capturedIndex, result);
                            }
                            finally
                            {
                                // ReSharper disable once AccessToDisposedClosure
                                semaphore.Release();
                            }
                        },
                        cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            // JOIN ALL ACTIVE TASKS before disposing the semaphore
            // This ensures no background work is left unobserved and avoids NREs/ODEs
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                if (logger != null && ShouldLogCleanupFailure(ex, cancellationToken))
                {
                    LogParallelSelectAsyncTaskCleanupFailure(logger, ex);
                }
            }

            throw;
        }

        return results.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value);
    }

    /// <summary>
    /// Produces an async sequence of results projected from each element of <paramref name="source"/>, yielding results in the original input order while limiting concurrent selector executions.
    /// </summary>
    /// <param name="source">The input sequence to project.</param>
    /// <param name="body">A selector that produces a <see cref="Task{TResult}"/> for an input element; this selector does not receive or observe the supplied <paramref name="cancellationToken"/>.</param>
    /// <param name="maxDegreeOfParallelism">The maximum number of selector tasks allowed to run concurrently; must be greater than zero.</param>
    /// <param name="cancellationToken">A token to observe for request to cancel the overall enumeration.</param>
    /// <returns>The sequence of transformed elements in the original input order; concurrency is limited to <paramref name="maxDegreeOfParallelism"/>.</returns>
    public static IAsyncEnumerable<TResult> ParallelSelectAsync<TSource, TResult>(
        this IEnumerable<TSource> source,
        Func<TSource, Task<TResult>> body,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken = default)
    {
        return source.ParallelSelectAsyncEnumerable(
            (item, _) => body(item),
            maxDegreeOfParallelism,
            bufferMultiplier: 4,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Produces an asynchronous sequence of results by applying <paramref name="body"/> to each element of <paramref name="source"/>, limiting concurrency to <paramref name="maxDegreeOfParallelism"/> and preserving the input order.
    /// The internal channel capacity is capped at <c>maxDegreeOfParallelism * bufferMultiplier</c>.
    /// </summary>
    /// <param name="source">The input sequence to project; must not be null.</param>
    /// <param name="body">A selector that projects an element to a <see cref="Task{TResult}"/>; it receives the element and a <see cref="CancellationToken"/> that is signaled when the operation is canceled.</param>
    /// <param name="maxDegreeOfParallelism">The maximum number of selector tasks that may run concurrently; must be greater than zero.</param>
    /// <param name="bufferMultiplier">A multiplier used to compute the internal channel capacity as <c>maxDegreeOfParallelism * bufferMultiplier</c> (capped to <see cref="int.MaxValue"/>); must be at least 1.</param>
    /// <param name="cancellationToken">Token to observe for cooperative cancellation of the overall operation.</param>
    /// <returns>An <see cref="IAsyncEnumerable{TResult}"/> that yields projected results in the same order as the source sequence.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="source"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="maxDegreeOfParallelism"/> is less than or equal to zero or if <paramref name="bufferMultiplier"/> is less than 1.</exception>
    public static async IAsyncEnumerable<TResult> ParallelSelectAsyncEnumerable<TSource, TResult>(
        this IEnumerable<TSource> source,
        Func<TSource, CancellationToken, Task<TResult>> body,
        int maxDegreeOfParallelism,
        int bufferMultiplier = 4,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var result in ParallelSelectAsyncEnumerableCore(
                           source,
                           body,
                           maxDegreeOfParallelism,
                           bufferMultiplier,
                           cancellationToken,
                           logger: null))
        {
            yield return result;
        }
    }

    /// <summary>
    /// Produces an asynchronous sequence of results by applying <paramref name="body"/> to each element of <paramref name="source"/>, limiting concurrency, preserving input order, observing <paramref name="cancellationToken"/>, and logging suppressed cleanup diagnostics through <paramref name="logger"/>.
    /// </summary>
    /// <param name="source">The input sequence to project; must not be null.</param>
    /// <param name="body">A selector that projects an element to a <see cref="Task{TResult}"/>; it receives the element and a cancellation token that is signaled when the operation is canceled.</param>
    /// <param name="maxDegreeOfParallelism">The maximum number of selector tasks that may run concurrently; must be greater than zero.</param>
    /// <param name="bufferMultiplier">A multiplier used to compute the internal channel capacity as <c>maxDegreeOfParallelism * bufferMultiplier</c> (capped to <see cref="int.MaxValue"/>); must be at least 1.</param>
    /// <param name="cancellationToken">Token to observe for cooperative cancellation of the overall operation.</param>
    /// <param name="logger">Logger for cleanup diagnostics that would otherwise be suppressed to preserve the primary exception.</param>
    /// <returns>An <see cref="IAsyncEnumerable{TResult}"/> that yields projected results in the same order as the source sequence.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="source"/>, <paramref name="body"/>, or <paramref name="logger"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="maxDegreeOfParallelism"/> is less than or equal to zero or if <paramref name="bufferMultiplier"/> is less than 1.</exception>
    public static IAsyncEnumerable<TResult> ParallelSelectAsyncEnumerable<TSource, TResult>(
        this IEnumerable<TSource> source,
        Func<TSource, CancellationToken, Task<TResult>> body,
        int maxDegreeOfParallelism,
        int bufferMultiplier,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return ParallelSelectAsyncEnumerableCore(
            source,
            body,
            maxDegreeOfParallelism,
            bufferMultiplier,
            cancellationToken: cancellationToken,
            logger: logger);
    }

    private static async IAsyncEnumerable<TResult> ParallelSelectAsyncEnumerableCore<TSource, TResult>(
        IEnumerable<TSource> source,
        Func<TSource, CancellationToken, Task<TResult>> body,
        int maxDegreeOfParallelism,
        int bufferMultiplier,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        ILogger? logger)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (body == null)
        {
            throw new ArgumentNullException(nameof(body));
        }

        if (maxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        }

        if (bufferMultiplier < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferMultiplier));
        }

        // Calculate capacity using long to prevent integer overflow
        long capacity = (long)maxDegreeOfParallelism * bufferMultiplier;
        if (capacity > int.MaxValue)
        {
            capacity = int.MaxValue;
        }

        var channel = Channel.CreateBounded<Task<TResult>>(
            new BoundedChannelOptions((int)capacity)
            {
                SingleWriter = true,
                SingleReader = true
            });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);
        var activeTasks = new ConcurrentBag<Task>();

        // Producer: Schedules tasks and writes them to the channel
        // We use Task.Run to offload the scheduling loop so it doesn't block the consumer
        var producerTask = Task.Run(
            async () =>
            {
                try
                {
                    foreach (var item in source)
                    {
                        // ReSharper disable AccessToDisposedClosure
                        if (cts.Token.IsCancellationRequested)
                        {
                            break;
                        }

                        await semaphore.WaitAsync(cts.Token);
                        // ReSharper restore AccessToDisposedClosure

                        // Start the task. 
                        // We wrap matching the semaphore release to the task completion.
                        var task = Task.Run(
                            async () =>
                            {
                                try
                                {
                                    // ReSharper disable AccessToDisposedClosure
                                    return await body(item, cts.Token);
                                    // ReSharper restore AccessToDisposedClosure
                                }
                                finally
                                {
                                    // ReSharper disable once AccessToDisposedClosure
                                    semaphore.Release();
                                }
                            },
                            // ReSharper disable AccessToDisposedClosure
                            cts.Token);
                        // ReSharper restore AccessToDisposedClosure

                        activeTasks.Add(task);

                        // Write the task (future result) to the channel
                        // If the consumer is slow, this will block once the channel is full,
                        // providing backpressure but ensuring we have maxDegreeOfParallelism active tasks.
                        // ReSharper disable AccessToDisposedClosure
                        await channel.Writer.WriteAsync(task, cts.Token);
                        // ReSharper restore AccessToDisposedClosure
                    }

                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    channel.Writer.Complete(ex);
                }
            },
            cancellationToken);

        try
        {
            // Consumer: Yields results in order
            await foreach (var task in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return await task;
            }
        }
        finally
        {
            // Signal producer to stop if it's still running (e.g. on break or exception)
            await cts.CancelAsync();

            try
            {
                // Ensure producer task is joined before semaphore is disposed
                await producerTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }

            // JOIN ALL ACTIVE TASKS before disposing the semaphore and CTS
            // This is critical because they capture the semaphore and CTS in their closures.
            try
            {
                await Task.WhenAll(activeTasks);
            }
            catch (Exception ex)
            {
                if (logger != null && ShouldLogCleanupFailure(ex, cts.Token))
                {
                    LogParallelSelectAsyncEnumerableTaskCleanupFailure(logger, ex);
                }
            }
        }
    }

    private static bool ShouldLogCleanupFailure(Exception exception, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested || exception is not OperationCanceledException;
    }

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Debug,
        Message = "ParallelSelectAsync suppressed task cleanup failures while preserving the primary failure.")]
    private static partial void LogParallelSelectAsyncTaskCleanupFailure(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1105,
        Level = LogLevel.Debug,
        Message = "ParallelSelectAsyncEnumerable suppressed task cleanup failures while disposing concurrency resources.")]
    private static partial void LogParallelSelectAsyncEnumerableTaskCleanupFailure(
        ILogger logger,
        Exception exception);

    /// <summary>
    /// Projects each element of <paramref name="source"/> using the provided task-returning selector with bounded concurrency and yields the results in the same order as the source.
    /// </summary>
    /// <param name="source">The input sequence to project.</param>
    /// <param name="body">A selector that produces a <see cref="Task{TResult}"/> for an input element. This overload does not pass or observe a <see cref="CancellationToken"/> to the selector.</param>
    /// <param name="maxDegreeOfParallelism">The maximum number of selector tasks allowed to run concurrently; must be greater than zero.</param>
    /// <param name="bufferMultiplier">Multiplier applied to <paramref name="maxDegreeOfParallelism"/> to determine internal channel capacity; must be at least 1.</param>
    /// <param name="cancellationToken">Token to cancel iteration and background work.</param>
    /// <returns>An <see cref="IAsyncEnumerable{TResult}"/> that yields projected results in the input order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDegreeOfParallelism"/> is less than or equal to 0, or <paramref name="bufferMultiplier"/> is less than 1.</exception>
    public static IAsyncEnumerable<TResult> ParallelSelectAsyncEnumerable<TSource, TResult>(
        this IEnumerable<TSource> source,
        Func<TSource, Task<TResult>> body,
        int maxDegreeOfParallelism,
        int bufferMultiplier = 4,
        CancellationToken cancellationToken = default)
    {
        return source.ParallelSelectAsyncEnumerable(
            (item, _) => body(item),
            maxDegreeOfParallelism,
            bufferMultiplier,
            cancellationToken);
    }
}
