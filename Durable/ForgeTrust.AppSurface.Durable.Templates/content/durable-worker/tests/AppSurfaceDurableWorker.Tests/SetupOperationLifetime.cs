using System.Collections.Concurrent;
using System.Diagnostics;

namespace DurableWorkerTemplate.Tests;

/// <summary>Tracks owned adapter tasks while bounding cancellation and cleanup observation.</summary>
/// <remarks>Factories may block before returning a task, so invocation uses a dedicated thread rather than
/// the worker pool needed by deadline continuations. The returned task remains tracked through its full
/// asynchronous lifetime. Observation expiry does not abort a factory or certify cleanup; late faults
/// are observed and pending operations remain owned until they settle.</remarks>
internal sealed class SetupOperationLifetime
{
    private readonly ConcurrentDictionary<Task, string> _operations = new();

    /// <summary>Counts unfinished operations, independently of completion-notification scheduling.</summary>
    /// <remarks>A completed or faulted task is settled even if its bookkeeping continuation has not run yet.</remarks>
    internal int PendingCount => _operations.Count(static pair => !pair.Key.IsCompleted);

    /// <summary>Invokes one owned adapter away from the worker pool and observes it until cancellation.</summary>
    /// <param name="phase">Safe nonempty resource label retained while the operation is pending.</param>
    /// <param name="operationFactory">Factory invoked once, even when observation is already canceled.</param>
    /// <param name="cancellationToken">Bounds observation; it does not cancel or discard the owned operation.</param>
    /// <returns>A task preserving completion, fault or observation cancellation.</returns>
    internal async Task AwaitAsync(string phase, Func<Task> operationFactory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(operationFactory);

        // Testcontainers and other external adapters can block before returning their Task.
        // A dedicated invocation thread keeps a synchronous adapter from occupying the pool workers
        // needed to observe cancellation and cleanup deadlines. Unwrap tracks its entire async lifetime.
        var operation = InvokeBlockingFactory(operationFactory);
        _operations.TryAdd(operation, phase);
        _ = operation.ContinueWith(
            completed =>
            {
                _operations.TryRemove(completed, out _);
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Attempts one owned cleanup operation under the caller's original monotonic deadline.</summary>
    /// <param name="phase">Safe resource label used in bounded failure diagnostics.</param>
    /// <param name="operationFactory">Owned teardown factory invoked off-thread, even after observation expires.</param>
    /// <param name="startedAt">The original cleanup timestamp from <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="totalBudget">Positive total allowance shared by every operation in this cleanup.</param>
    /// <param name="failures">Receives safe exhaustion or exception-type diagnostics; failures never end the resource walk.</param>
    /// <returns>A task completing when observation ends; unfinished operations remain tracked and cannot certify cleanup.</returns>
    /// <remarks>The factory receives only the original remaining allowance, with a one-millisecond minimum for APIs
    /// requiring a positive attempt. That minimum grants no new observation window; callers must reject pending work.</remarks>
    internal async Task AwaitCleanupAsync(string phase, Func<TimeSpan, Task> operationFactory,
        long startedAt, TimeSpan totalBudget, ICollection<string> failures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(operationFactory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalBudget, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(failures);
        var remaining = totalBudget - Stopwatch.GetElapsedTime(startedAt);
        using var observation = new CancellationTokenSource();
        if (remaining <= TimeSpan.Zero)
        {
            failures.Add($"{phase}-cleanup-budget-exhausted");
            observation.Cancel();
        }
        else
        {
            observation.CancelAfter(remaining);
        }
        try
        {
            await AwaitAsync(phase, () =>
            {
                var originalRemaining = totalBudget - Stopwatch.GetElapsedTime(startedAt);
                return operationFactory(originalRemaining > TimeSpan.Zero ? originalRemaining : TimeSpan.FromMilliseconds(1));
            }, observation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add($"{phase}-{exception.GetType().Name}");
        }
    }

    /// <summary>Stops the owner and observes pending setup using one shared cleanup allowance.</summary>
    /// <param name="stopOwnerAsync">Optional owner-stop factory, isolated during synchronous invocation.</param>
    /// <param name="totalBudget">Positive shared allowance for owner-stop and pending-task observation.</param>
    /// <param name="childTerminationBudget">Positive observation reservation subtracted before owner-stop.</param>
    /// <param name="failures">Collects safe failures without abandoning pending-operation observation.</param>
    /// <returns>Whether the captured pending setup tasks settled; callers must also inspect stop failures.</returns>
    /// <remarks>The factory receives the original stop allowance. A late owner stop remains observed without
    /// extending the deadline. Empty pending setup returns true without invoking owner stop.</remarks>
    internal async Task<bool> StopAndObservePendingAsync(
        Func<TimeSpan, Task<bool>>? stopOwnerAsync,
        TimeSpan totalBudget,
        TimeSpan childTerminationBudget,
        ICollection<string> failures)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(childTerminationBudget, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(failures);

        var pending = _operations
            .Where(static pair => !pair.Key.IsCompleted)
            .ToArray();
        if (pending.Length == 0)
        {
            return true;
        }

        var startedAt = Stopwatch.GetTimestamp();
        if (stopOwnerAsync is not null)
        {
            var remaining = totalBudget - Stopwatch.GetElapsedTime(startedAt);
            var stopBudget = remaining - childTerminationBudget;
            if (stopBudget <= TimeSpan.Zero)
            {
                failures.Add("owned-resource-stop-budget-exhausted");
            }
            else
            {
                Task<bool> stopOperation;
                try
                {
                    // Docker process startup can block before returning a Task; keep deadline workers available.
                    stopOperation = InvokeBlockingFactory(() => stopOwnerAsync(stopBudget));
                }
                catch (Exception exception)
                {
                    failures.Add($"owned-resource-stop-{exception.GetType().Name}");
                    stopOperation = Task.FromResult(false);
                }

                try
                {
                    if (!await stopOperation.WaitAsync(stopBudget).ConfigureAwait(false))
                    {
                        failures.Add("owned-resource-stop-failed");
                    }
                }
                catch (TimeoutException)
                {
                    ObserveLateCompletion(stopOperation);
                    failures.Add("owned-resource-stop-unfinished");
                }
                catch (Exception exception)
                {
                    failures.Add($"owned-resource-stop-{exception.GetType().Name}");
                }
            }
        }

        var observationRemaining = totalBudget - Stopwatch.GetElapsedTime(startedAt);
        if (observationRemaining <= TimeSpan.Zero)
        {
            failures.Add("setup-operation-observation-budget-exhausted");
            return false;
        }

        var observationBudget = observationRemaining < childTerminationBudget
            ? observationRemaining
            : childTerminationBudget;
        var allPending = Task.WhenAll(pending.Select(static pair => pair.Key));
        try
        {
            await allPending.WaitAsync(observationBudget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveLateCompletion(allPending);
            failures.Add($"setup-operation-unfinished:{string.Join(",", pending.Select(static pair => pair.Value))}");
            return false;
        }
        catch (Exception) when (allPending.IsCompleted)
        {
            // The operation fault is observed here; its setup caller owns the primary failure.
        }

        return allPending.IsCompleted;
    }

    private static void ObserveLateCompletion(Task operation)
    {
        _ = operation.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Invokes one potentially blocking owned factory on a dedicated thread.</summary>
    /// <param name="factory">Factory whose synchronous entry must not occupy a deadline worker.</param>
    /// <returns>The unwrapped full asynchronous lifetime, including factory faults and cancellation.</returns>
    /// <remarks>The caller owns observation and cleanup. Invocation has no abort or timeout; asynchronous
    /// continuations keep their normal scheduler after the dedicated invocation thread returns.</remarks>
    internal static Task InvokeBlockingFactory(Func<Task> factory) => Task.Factory.StartNew(factory,
        CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
        TaskScheduler.Default).Unwrap();

    private static Task<T> InvokeBlockingFactory<T>(Func<Task<T>> factory) => Task.Factory.StartNew(factory,
        CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
        TaskScheduler.Default).Unwrap();
}
