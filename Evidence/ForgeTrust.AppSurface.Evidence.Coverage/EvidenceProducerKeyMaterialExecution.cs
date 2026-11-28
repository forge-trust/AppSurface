using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Describes a bounded key-material wait without confusing a wait deadline with actual callback settlement.</summary>
/// <param name="Material">The validated snapshot when the callback and capture both completed before the deadline.</param>
/// <param name="Settled">Whether the actual callback task has terminated.</param>
/// <param name="CallerCancelled">Whether invocation cancellation requested this stop.</param>
/// <param name="Reason">Stable preparation reason.</param>
internal sealed record EvidenceProducerKeyMaterialPreparation(
    EvidenceKeyMaterialSnapshot? Material,
    bool Settled,
    bool CallerCancelled,
    string Reason)
{
    /// <summary>Gets safe bounded detail for framework-detected ineligibility, never callback exception text or raw material.</summary>
    internal string? Diagnostic { get; init; }
}

/// <summary>Retains an actual optional key-material callback until it terminates, including synchronous callback work.</summary>
/// <remarks>
/// The prepared session owns this object, its producer registration and every acquired lease until DisposeAsync
/// finishes. A preparation timeout permits fresh fallback only when Settled is true. Raw returned material never
/// enters diagnostics or persistent state; identity computation hashes bounded values separately.
/// </remarks>
internal sealed class EvidenceProducerKeyMaterialExecution : IAsyncDisposable
{
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task<EvidenceKeyMaterialSnapshot> _actualTask;
    private readonly TimeSpan _cleanupTimeout;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _callerCancellation;
    private readonly object _disposeSync = new();
    private Task? _disposal;

    /// <summary>Gets whether the actual callback, rather than only its bounded wait, has terminated.</summary>
    internal bool IsSettled => _actualTask.IsCompleted;

    private EvidenceProducerKeyMaterialExecution(
        IEvidenceProducerKeyMaterial callback,
        EvidenceProducerKeyMaterialContext context,
        TimeSpan timeout,
        TimeSpan cleanupTimeout,
        CancellationToken callerCancellation)
    {
        _clock = context.TimeProvider;
        _cleanupTimeout = cleanupTimeout;
        _callerCancellation = callerCancellation;
        _deadline = new CancellationTokenSource(timeout, _clock);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation, _deadline.Token);
        // Calling an arbitrary ValueTask method can itself block before it returns its awaitable. Own that work too.
        var callbackToken = _cancellation.Token;
        _actualTask = Task.Run(async () =>
        {
            callbackToken.ThrowIfCancellationRequested();
            var material = await callback.GetKeyMaterialAsync(context, callbackToken).ConfigureAwait(false);
            return EvidenceKeyMaterialSnapshot.Capture(material, callbackToken);
        }, callbackToken);
        _ = _actualTask.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Starts one side-effect-free callback under its declared timeout capped at thirty seconds.</summary>
    /// <param name="callback">Registered producer's optional key-material capability.</param>
    /// <param name="context">Frozen plan, declaration and clock; no writer or ready-resource access.</param>
    /// <param name="cleanupTimeout">Separate positive grace period, at most one minute.</param>
    /// <param name="cancellationToken">Invocation cancellation, retained until actual settlement.</param>
    /// <returns>The owned actual callback invocation.</returns>
    internal static EvidenceProducerKeyMaterialExecution Start(
        IEvidenceProducerKeyMaterial callback,
        EvidenceProducerKeyMaterialContext context,
        TimeSpan cleanupTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Producer.TimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(context));
        if (!EvidenceExecutionTimeouts.IsValidProducerCleanup(cleanupTimeout))
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
        cancellationToken.ThrowIfCancellationRequested();
        return new EvidenceProducerKeyMaterialExecution(callback, context,
            TimeSpan.FromSeconds(Math.Min(30, context.Producer.TimeoutSeconds)), cleanupTimeout, cancellationToken);
    }

    /// <summary>Waits through the callback deadline and cleanup grace, retaining late actual work for its owner.</summary>
    /// <returns>A fresh-fallback decision only after actual settlement, or Unavailable while work remains active.</returns>
    internal async Task<EvidenceProducerKeyMaterialPreparation> WaitAsync()
    {
        try
        {
            var material = await _actualTask.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            if (_cancellation.IsCancellationRequested)
                return CreateStoppedPreparation(settled: true);
            return new EvidenceProducerKeyMaterialPreparation(material, true, false, "reuse.material_ready");
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _cancellation.Cancel();
            try
            {
                await _actualTask.WaitAsync(_cleanupTimeout, _clock).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!_actualTask.IsCompleted)
            {
                return CreateStoppedPreparation(settled: false);
            }
            catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
            {
                // A completed nonfatal cancellation/fault establishes settlement, never a usable late key.
            }

            return CreateStoppedPreparation(settled: true);
        }
        catch (EvidenceKeyMaterialValidationException exception)
        {
            return new EvidenceProducerKeyMaterialPreparation(null, true, _callerCancellation.IsCancellationRequested,
                "reuse.material_invalid")
            {
                Diagnostic = exception.Message,
            };
        }
        catch (InvalidDataException)
        {
            return new EvidenceProducerKeyMaterialPreparation(null, true, _callerCancellation.IsCancellationRequested,
                "reuse.material_invalid")
            {
                Diagnostic = "The key-material callback returned invalid data. Return a non-null map with unique, non-empty keys within the declared limits.",
            };
        }
        catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
        {
            return new EvidenceProducerKeyMaterialPreparation(null, true, _callerCancellation.IsCancellationRequested,
                "reuse.material_failed");
        }
    }

    private EvidenceProducerKeyMaterialPreparation CreateStoppedPreparation(bool settled)
    {
        var callerCancelled = _callerCancellation.IsCancellationRequested;
        var reason = (settled, callerCancelled) switch
        {
            (true, true) => "reuse.material_cancelled",
            (true, false) => "reuse.material_timed_out",
            (false, true) => "reuse.material_cancelled_active",
            (false, false) => "reuse.material_unsettled",
        };
        return new(null, settled, callerCancelled, reason);
    }

    /// <summary>Joins the actual callback and releases its cancellation sources only after termination.</summary>
    /// <returns>The actual settlement operation, with late fatal exceptions propagated to the owner.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _actualTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
        {
            // The preparation outcome already represents an invalid, cancelled or failed key callback.
        }
        finally
        {
            _cancellation.Dispose();
            _deadline.Dispose();
        }
    }

}
