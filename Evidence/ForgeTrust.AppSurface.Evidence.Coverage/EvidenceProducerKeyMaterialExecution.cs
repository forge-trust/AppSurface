using System.Collections.ObjectModel;
using System.Globalization;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Describes a bounded key-material wait without confusing a wait deadline with actual callback settlement.</summary>
/// <param name="Material">Local raw material only when the callback completed successfully before its deadline.</param>
/// <param name="Settled">Whether the actual callback task has terminated.</param>
/// <param name="CallerCancelled">Whether invocation cancellation requested this stop.</param>
/// <param name="Reason">Stable preparation reason.</param>
internal sealed record EvidenceProducerKeyMaterialPreparation(
    IReadOnlyDictionary<string, string>? Material,
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
    private readonly Task<IReadOnlyDictionary<string, string>> _actualTask;
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
        _actualTask = Task.Run(async () => CaptureMaterial(await callback.GetKeyMaterialAsync(context, _cancellation.Token).ConfigureAwait(false)));
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
        if (cleanupTimeout <= TimeSpan.Zero || cleanupTimeout > TimeSpan.FromMinutes(1))
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
                return new EvidenceProducerKeyMaterialPreparation(null, true, _callerCancellation.IsCancellationRequested,
                    _callerCancellation.IsCancellationRequested ? "reuse.material_cancelled" : "reuse.material_timed_out");
            // CaptureMaterial rejects null and returns an owned, non-null snapshot before the task completes.
            return new EvidenceProducerKeyMaterialPreparation(material, true, false, "reuse.material_ready");
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _cancellation.Cancel();
            try
            {
                await _actualTask.WaitAsync(_cleanupTimeout, _clock).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new EvidenceProducerKeyMaterialPreparation(null, false, _callerCancellation.IsCancellationRequested,
                    _callerCancellation.IsCancellationRequested ? "reuse.material_cancelled_active" : "reuse.material_unsettled");
            }
            catch (Exception exception) when (IsNonFatal(exception))
            {
                // A completed nonfatal cancellation/fault establishes settlement, never a usable late key.
            }

            return new EvidenceProducerKeyMaterialPreparation(null, true, _callerCancellation.IsCancellationRequested,
                _callerCancellation.IsCancellationRequested ? "reuse.material_cancelled" : "reuse.material_timed_out");
        }
        catch (KeyMaterialValidationException exception)
        {
            return new EvidenceProducerKeyMaterialPreparation(null, true, _callerCancellation.IsCancellationRequested,
                "reuse.material_invalid")
            {
                Diagnostic = exception.Diagnostic,
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
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return new EvidenceProducerKeyMaterialPreparation(null, true, _callerCancellation.IsCancellationRequested,
                "reuse.material_failed");
        }
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
        catch (Exception exception) when (IsNonFatal(exception))
        {
            // The preparation outcome already represents an invalid, cancelled or failed key callback.
        }
        finally
        {
            _cancellation.Dispose();
            _deadline.Dispose();
        }
    }

    private static bool IsNonFatal(Exception exception) => exception is not OutOfMemoryException
        and not StackOverflowException and not AccessViolationException and not AppDomainUnloadedException;

    private static IReadOnlyDictionary<string, string> CaptureMaterial(IReadOnlyDictionary<string, string> material)
    {
        if (material is null)
            throw new KeyMaterialValidationException("The key-material callback returned no material map. Return a non-null map within the declared limits.");
        var reportedCount = material.Count;
        if (reportedCount > EvidenceProducerIdentityBuilder.MaximumMaterialKeys)
            throw new KeyMaterialValidationException(
                $"Key material contains {FormatNumber(reportedCount)} keys; limit is {FormatNumber(EvidenceProducerIdentityBuilder.MaximumMaterialKeys)}. Remove keys and retry.");

        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalBytes = 0L;
        var observedCount = 0;
        foreach (var pair in material)
        {
            observedCount++;
            if (observedCount > EvidenceProducerIdentityBuilder.MaximumMaterialKeys)
                throw new KeyMaterialValidationException(
                    $"More than {FormatNumber(EvidenceProducerIdentityBuilder.MaximumMaterialKeys)} key-material entries were observed ({FormatNumber(observedCount)} observed). Remove keys and retry.");
            if (string.IsNullOrEmpty(pair.Key))
                throw new KeyMaterialValidationException("A key-material key name has length 0; minimum is 1. Provide a non-empty key name.");
            if (pair.Key.Length > EvidenceProducerIdentityBuilder.MaximumMaterialKeyLength)
                throw new KeyMaterialValidationException(
                    $"A key-material key name is {FormatNumber(pair.Key.Length)} UTF-16 code units; limit is {FormatNumber(EvidenceProducerIdentityBuilder.MaximumMaterialKeyLength)}. Shorten key names and retry.");
            if (pair.Value is null)
                throw new KeyMaterialValidationException("A key-material entry has a null value. Return non-null values within the declared limits.");
            if (snapshot.ContainsKey(pair.Key))
                throw new KeyMaterialValidationException("Key-material key names must be unique. Return a map with unique key names.");

            var valueBytes = GetUtf8ByteCount(pair.Value);
            if (valueBytes > EvidenceProducerIdentityBuilder.MaximumMaterialValueBytes)
                throw new KeyMaterialValidationException(
                    $"A key-material value is {FormatNumber(valueBytes)} UTF-8 bytes; limit is {FormatNumber(EvidenceProducerIdentityBuilder.MaximumMaterialValueBytes)}. Reduce the value and retry.");
            totalBytes += GetUtf8ByteCount(pair.Key) + valueBytes;
            if (totalBytes > EvidenceProducerIdentityBuilder.MaximumMaterialTotalBytes)
                throw new KeyMaterialValidationException(
                    $"Key material totals {FormatNumber(totalBytes)} UTF-8 bytes; limit is {FormatNumber(EvidenceProducerIdentityBuilder.MaximumMaterialTotalBytes)}. Reduce key and value sizes and retry.");
            snapshot.Add(pair.Key, pair.Value);
        }
        return new ReadOnlyDictionary<string, string>(snapshot);
    }

    private static long GetUtf8ByteCount(string value)
    {
        long bytes = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character <= 0x7f) bytes++;
            else if (character <= 0x7ff) bytes += 2;
            else if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                bytes += 4;
                index++;
            }
            else bytes += 3;
        }

        return bytes;
    }

    private static string FormatNumber(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class KeyMaterialValidationException(string diagnostic) : Exception
    {
        internal string Diagnostic { get; } = diagnostic;
    }
}
