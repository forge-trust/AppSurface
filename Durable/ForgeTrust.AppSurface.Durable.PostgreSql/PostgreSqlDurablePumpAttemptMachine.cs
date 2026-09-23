using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Identifies the linear phase reached by one PostgreSQL pump attempt.</summary>
internal enum PostgreSqlDurablePumpPhase
{
    /// <summary>The process-local pass slot has not yet been acquired.</summary>
    LocalSlotPending = 0,

    /// <summary>The process-local slot is owned and shutdown admission is being decided.</summary>
    ProcessAdmission = 1,

    /// <summary>Schema, epoch, worker generation, drain, and active-pass state are being decided.</summary>
    StoreAdmission = 2,

    /// <summary>The sole pass execution boundary has been entered.</summary>
    Executing = 3,

    /// <summary>Application execution returned and caller cancellation no longer owns the operation.</summary>
    ProviderReturned = 4,

    /// <summary>Terminal provider bookkeeping is running under its own bounded reserve.</summary>
    Finalizing = 5,

    /// <summary>Terminal bookkeeping completed.</summary>
    Completed = 6,
}

/// <summary>Identifies the typed pre-execution cause of a refused pump attempt.</summary>
internal enum PostgreSqlDurablePumpRefusal
{
    /// <summary>Another pass owns this pump instance's process-local slot.</summary>
    LocalPassOverlap = 0,

    /// <summary>The process admission gate closed before this attempt entered it.</summary>
    ProcessAdmissionClosed = 1,

    /// <summary>The authoritative worker row is draining.</summary>
    Draining = 2,

    /// <summary>The authoritative worker row already has an active pass.</summary>
    StorePassActive = 3,

    /// <summary>Another live process generation owns the worker identity.</summary>
    LostWorkerGeneration = 4,
}

/// <summary>Identifies the private outcome projected through the two public pump contracts.</summary>
internal enum PostgreSqlDurablePumpOutcomeKind
{
    /// <summary>Execution and terminal bookkeeping completed.</summary>
    Completed = 0,

    /// <summary>Admission was refused before execution.</summary>
    Refused = 1,

    /// <summary>The store could not be observed before execution.</summary>
    Unavailable = 2,

    /// <summary>The observed schema or epoch was incompatible.</summary>
    Incompatible = 3,
}

/// <summary>Invokes the sole transition into application execution for one bounded pass.</summary>
internal delegate ValueTask<DurableRuntimePumpResult> PostgreSqlDurablePassExecutor(
    DurableRuntimePumpRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Carries one private pump outcome, including the original exception required by the legacy projection.
/// </summary>
internal readonly record struct PostgreSqlDurablePumpOutcome(
    PostgreSqlDurablePumpOutcomeKind Kind,
    DurableRuntimePumpResult? Result,
    PostgreSqlDurablePumpRefusal? Refusal,
    string? ProblemCode,
    ExceptionDispatchInfo? LegacyException)
{
    /// <summary>Creates a completed outcome with the exact provider result.</summary>
    internal static PostgreSqlDurablePumpOutcome Completed(DurableRuntimePumpResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(
            PostgreSqlDurablePumpOutcomeKind.Completed,
            result,
            null,
            null,
            null);
    }

    /// <summary>Creates a typed refusal, optionally retaining the exact legacy exception.</summary>
    internal static PostgreSqlDurablePumpOutcome Refused(
        PostgreSqlDurablePumpRefusal refusal,
        ExceptionDispatchInfo? legacyException = null) =>
        new(
            PostgreSqlDurablePumpOutcomeKind.Refused,
            null,
            refusal,
            null,
            legacyException);

    /// <summary>Creates an unavailable result while retaining the original provider exception.</summary>
    internal static PostgreSqlDurablePumpOutcome Unavailable(
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new(
            PostgreSqlDurablePumpOutcomeKind.Unavailable,
            null,
            null,
            DurableProblemCodes.StoreUnavailable,
            ExceptionDispatchInfo.Capture(exception));
    }

    /// <summary>Creates an incompatible result while retaining the exact legacy exception.</summary>
    internal static PostgreSqlDurablePumpOutcome Incompatible(
        string problemCode,
        ExceptionDispatchInfo legacyException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problemCode);
        ArgumentNullException.ThrowIfNull(legacyException);
        return new(
            PostgreSqlDurablePumpOutcomeKind.Incompatible,
            null,
            null,
            problemCode,
            legacyException);
    }
}

/// <summary>Associates an original pump exception with the phase that produced it.</summary>
internal static class PostgreSqlDurablePumpFailureContext
{
    private static readonly ConditionalWeakTable<Exception, PhaseHolder> Phases = new();

    /// <summary>Marks an exception without changing its type, message, token, inner exception, or stack.</summary>
    internal static void Mark(Exception exception, PostgreSqlDurablePumpPhase phase)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Phases.AddOrUpdate(exception, new PhaseHolder(phase));
    }

    /// <summary>Gets whether terminal finalization, rather than the active pass token, produced this failure.</summary>
    internal static bool IsFinalizationFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Phases.TryGetValue(exception, out var holder)
            && holder.Phase == PostgreSqlDurablePumpPhase.Finalizing;
    }

    /// <summary>Gets whether the sole application execution boundary produced this failure.</summary>
    internal static bool IsExecutionFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Phases.TryGetValue(exception, out var holder)
            && holder.Phase == PostgreSqlDurablePumpPhase.Executing;
    }

    private sealed record PhaseHolder(PostgreSqlDurablePumpPhase Phase);
}

/// <summary>
/// Marks a store-admission failure that may have occurred after PostgreSQL accepted the pass-active mutation.
/// </summary>
internal static class PostgreSqlDurableAdmissionFailureContext
{
    private static readonly ConditionalWeakTable<Exception, Marker> IndeterminateFailures = new();

    /// <summary>Marks the original exception without wrapping it or changing its legacy projection.</summary>
    internal static void MarkIndeterminate(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        IndeterminateFailures.AddOrUpdate(exception, Marker.Instance);
    }

    /// <summary>
    /// Consumes whether ownership-scoped cleanup is required before this attempt projects the failure.
    /// </summary>
    internal static bool TakeIndeterminate(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!IndeterminateFailures.TryGetValue(exception, out _))
        {
            return false;
        }

        IndeterminateFailures.Remove(exception);
        return true;
    }

    private sealed class Marker
    {
        internal static Marker Instance { get; } = new();
    }
}
