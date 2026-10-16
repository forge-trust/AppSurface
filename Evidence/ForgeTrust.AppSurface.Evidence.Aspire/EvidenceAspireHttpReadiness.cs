using System.Runtime.ExceptionServices;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Readiness;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>One resource's native-health and HTTP readiness operation with joined asynchronous disposal.</summary>
internal sealed class EvidenceAspireHttpReadiness : IEvidenceResourceReadiness, IAsyncDisposable
{
    private const string CleanupFailureDataKey = "AppSurface.Readiness.CleanupFailure";

    private enum FlowKind
    {
        Readiness,
        Cleanup,
    }

    private sealed record FlowFrame(EvidenceAspireHttpReadiness Owner, FlowKind Kind, FlowFrame? Parent);

    private static readonly AsyncLocal<FlowFrame?> CurrentFlow = new();

    private readonly object _gate = new();
    private readonly string _probePath;
    private readonly TimeSpan _timeout;
    private readonly HttpReadinessSettings _settings;
    private readonly Func<CancellationToken, Task> _waitForHealth;
    private readonly Func<CancellationToken, Task<string?>> _resolveEndpoint;
    private readonly Func<Uri, HttpClient> _createClient;
    private readonly Func<ValueTask> _disposeLease;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stop = new();

    private int _activeWaits;
    private bool _admissionClosed;
    private TaskCompletionSource? _drained;
    private TaskCompletionSource? _disposeCompletion;

    /// <summary>Creates one adapter with independent per-wait deadlines and client ownership.</summary>
    /// <param name="id">The declared EvidenceHost resource identity.</param>
    /// <param name="probePath">The validated relative or root-relative HTTP path.</param>
    /// <param name="timeout">The finite positive budget applied independently to each admitted wait.</param>
    /// <param name="settings">The immutable validated HTTP policy snapshot.</param>
    /// <param name="waitForHealth">The native-health phase completed before endpoint allocation.</param>
    /// <param name="resolveEndpoint">The named endpoint value phase, invoked after health succeeds.</param>
    /// <param name="createClient">Creates a fresh client owned by the individual wait.</param>
    /// <param name="disposeLease">Disposes the existing application lease after all waits and clients drain.</param>
    /// <param name="timeProvider">The clock and timer provider used by each wait.</param>
    /// <exception cref="ArgumentException">The id or probe path is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is unsupported.</exception>
    /// <exception cref="ArgumentNullException">A required callback, snapshot, or time provider is null.</exception>
    /// <remarks>Callbacks execute outside the admission lock. A callback must not dispose this adapter from its own wait.</remarks>
    internal EvidenceAspireHttpReadiness(
        string id,
        string probePath,
        TimeSpan timeout,
        HttpReadinessSettings settings,
        Func<CancellationToken, Task> waitForHealth,
        Func<CancellationToken, Task<string?>> resolveEndpoint,
        Func<Uri, HttpClient> createClient,
        Func<ValueTask> disposeLease,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        HttpReadinessProbe.ValidateProbePath(probePath);
        ReadinessDeadline.ValidateDuration(timeout, nameof(timeout));
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(waitForHealth);
        ArgumentNullException.ThrowIfNull(resolveEndpoint);
        ArgumentNullException.ThrowIfNull(createClient);
        ArgumentNullException.ThrowIfNull(disposeLease);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Id = id;
        _probePath = probePath;
        _timeout = timeout;
        _settings = settings;
        _waitForHealth = waitForHealth;
        _resolveEndpoint = resolveEndpoint;
        _createClient = createClient;
        _disposeLease = disposeLease;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>Admits one independent wait unless cleanup has atomically closed admission.</summary>
    /// <param name="cancellationToken">The original caller token kept distinct from deadline and adapter-stop cancellation.</param>
    /// <returns>A task completing after readiness and per-wait client cleanup settle.</returns>
    /// <exception cref="ObjectDisposedException">The adapter has begun cleanup.</exception>
    /// <inheritdoc />
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_admissionClosed, this);
            _activeWaits++;
        }

        var wait = RunWaitAsync(cancellationToken);
        _ = wait.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                ((EvidenceAspireHttpReadiness)state!).FinishWait();
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return wait;
    }

    /// <summary>Closes admission, cancels and drains admitted waits, then disposes the existing application lease once.</summary>
    /// <returns>The shared cleanup task observed by every external disposal caller.</returns>
    /// <remarks>Reentry from a cleanup callback does not wait on its own cleanup flow.</remarks>
    public ValueTask DisposeAsync()
    {
        var flow = FindFlow(this);
        if (flow?.Kind == FlowKind.Readiness)
        {
            throw new InvalidOperationException("An Aspire readiness callback cannot dispose its own readiness adapter.");
        }

        if (flow?.Kind == FlowKind.Cleanup)
        {
            return ValueTask.CompletedTask;
        }

        TaskCompletionSource? startCleanup = null;
        TaskCompletionSource? completeDrain = null;
        Task cleanupTask;
        Task drainTask;
        lock (_gate)
        {
            if (_disposeCompletion is null)
            {
                _admissionClosed = true;
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_activeWaits == 0)
                {
                    completeDrain = _drained;
                }

                startCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeCompletion = startCleanup;
            }

            cleanupTask = _disposeCompletion.Task;
            drainTask = _drained?.Task ?? Task.CompletedTask;
        }

        completeDrain?.TrySetResult();
        if (startCleanup is not null)
        {
            _ = CompleteCleanupAsync(startCleanup, drainTask);
        }

        return new ValueTask(cleanupTask);
    }

    internal static Uri ResolveEndpointUri(string? endpointValue)
    {
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new ReadinessException(ReadinessPhase.EndpointResolution, ReadinessFailureCategory.EndpointUnavailable);
        }

        return endpoint;
    }

    private async Task RunWaitAsync(CancellationToken callerToken)
    {
        var previousFlow = CurrentFlow.Value;
        CurrentFlow.Value = new FlowFrame(this, FlowKind.Readiness, previousFlow);
        ReadinessDeadline? deadline = null;
        HttpClient? client = null;
        Exception? operationFailure = null;
        Exception? clientCleanupFailure = null;

        try
        {
            deadline = new ReadinessDeadline(_timeout, _timeProvider, callerToken, _stop.Token);
            deadline.Check(ReadinessPhase.ResourceHealth);
            try
            {
                await _waitForHealth(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                deadline.Check(ReadinessPhase.ResourceHealth);
                throw;
            }

            deadline.Check(ReadinessPhase.ResourceHealth);
            string? endpointValue;
            try
            {
                endpointValue = await _resolveEndpoint(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                deadline.Check(ReadinessPhase.EndpointResolution);
                throw;
            }

            deadline.Check(ReadinessPhase.EndpointResolution);
            var endpoint = ResolveEndpointUri(endpointValue);
            try
            {
                client = _createClient(endpoint);
            }
            catch (OperationCanceledException)
            {
                deadline.Check(ReadinessPhase.Http);
                throw;
            }

            ArgumentNullException.ThrowIfNull(client);
            deadline.Check(ReadinessPhase.Http);
            await HttpReadinessProbe.WaitCoreAsync(client, endpoint, _probePath, _settings, deadline).ConfigureAwait(false);
            deadline.Check(ReadinessPhase.Http);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        finally
        {
            try
            {
                client?.Dispose();
            }
            catch (Exception exception)
            {
                clientCleanupFailure = exception;
            }

            if (operationFailure is null && deadline is not null)
            {
                try
                {
                    deadline.Check(ReadinessPhase.Http);
                }
                catch (Exception exception)
                {
                    operationFailure = exception;
                }
            }
        }

        try
        {
            deadline?.Dispose();
        }
        finally
        {
            CurrentFlow.Value = previousFlow;
        }

        if (clientCleanupFailure is not null)
        {
            if (operationFailure is null)
            {
                throw new ReadinessException(ReadinessPhase.Cleanup, ReadinessFailureCategory.CleanupFailure,
                    cleanupExceptionType: clientCleanupFailure.GetType());
            }

            RecordSafeSecondaryCleanup(operationFailure);
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }
    }

    private async Task CompleteCleanupAsync(TaskCompletionSource completion, Task drainTask)
    {
        var previousFlow = CurrentFlow.Value;
        CurrentFlow.Value = new FlowFrame(this, FlowKind.Cleanup, previousFlow);
        List<Exception>? failures = null;
        try
        {
            try
            {
                _stop.Cancel();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                await drainTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            finally
            {
                try
                {
                    _stop.Dispose();
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }

                try
                {
                    await _disposeLease().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            if (failures is null)
            {
                completion.TrySetResult();
            }
            else if (failures.Count == 1)
            {
                completion.TrySetException(failures[0]);
            }
            else
            {
                completion.TrySetException(new AggregateException("Aspire readiness cleanup encountered multiple failures.", failures));
            }
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            CurrentFlow.Value = previousFlow;
        }
    }

    private void FinishWait()
    {
        TaskCompletionSource? completeDrain = null;
        lock (_gate)
        {
            _activeWaits--;
            if (_admissionClosed && _activeWaits == 0)
            {
                completeDrain = _drained;
            }
        }

        completeDrain?.TrySetResult();
    }

    private static void RecordSafeSecondaryCleanup(Exception primaryFailure)
    {
        try
        {
            primaryFailure.Data[CleanupFailureDataKey] = ReadinessFailureCategory.CleanupFailure;
        }
        catch (Exception)
        {
            // Safe metadata must not replace the original primary error.
        }
    }

    private static FlowFrame? FindFlow(EvidenceAspireHttpReadiness owner)
    {
        for (var current = CurrentFlow.Value; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current.Owner, owner) && current.Kind == FlowKind.Readiness)
            {
                return current;
            }
        }

        for (var current = CurrentFlow.Value; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current.Owner, owner))
            {
                return current;
            }
        }

        return null;
    }
}
