using System.Collections.Concurrent;
using System.Diagnostics;

namespace DurableWorkerTemplate.Tests;

internal sealed class SetupOperationLifetime
{
    private readonly ConcurrentDictionary<Task, string> _operations = new();

    internal int PendingCount => _operations.Count;

    internal async Task AwaitAsync(string phase, Func<Task> operationFactory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(operationFactory);

        // Testcontainers and other external adapters can block before returning their Task.
        // Move invocation itself off the caller so the phase token can always bound the wait.
        var operation = Task.Run(operationFactory, CancellationToken.None);
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
                    // Invoke the stop seam off-thread too: Docker process startup can block before it returns a Task.
                    stopOperation = Task.Run(() => stopOwnerAsync(stopBudget), CancellationToken.None);
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
}
