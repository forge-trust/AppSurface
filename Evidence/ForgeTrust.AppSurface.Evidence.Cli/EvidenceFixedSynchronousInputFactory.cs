using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>Implements the compile-selected private N11 synchronous input-factory stall.</summary>
/// <remarks>
/// <see cref="CreateForProtectedRole"/> returns an instance only in the N11 build; ordinary builds
/// return null and retain the normal input factory. The N11 caller invokes its private blocking method
/// inside the existing tracked Admission callback after authenticated READY. Before entering that callback,
/// the caller registers this helper's disposer with the existing <see cref="EvidenceWorkerExecution"/>.
/// The fixed ARM marker is written only after registration succeeds; the registered callback writes its
/// INVOKED marker before doing any work and at most once. The stall method then writes the existing entry
/// marker and deliberately blocks before returning a task. These markers are fixture diagnostics only:
/// they establish neither successful cleanup nor settlement. This helper adds no runtime selector, deadline,
/// cancellation path or settlement authority.
/// </remarks>
internal sealed class EvidenceFixedSynchronousInputFactory
{
    private int _disposerRegistrationAttempted;
    private int _disposerInvoked;

    internal EvidenceFixedSynchronousInputFactory() { }

    /// <summary>Creates the fixed stall helper only when the whole image was compiled for N11.</summary>
    /// <returns>The N11 helper, or null for every other qualification and the ordinary build.</returns>
    internal static EvidenceFixedSynchronousInputFactory? CreateForProtectedRole() =>
#if EVIDENCE_PRIVATE_N11
        EvidenceNativeQualification.WorkerStallEnabled ? new EvidenceFixedSynchronousInputFactory() : null;
#else
        null;
#endif

    /// <summary>Registers the fixed marker callback with the actual worker lifecycle before tracked admission.</summary>
    /// <param name="execution">The existing lifecycle that owns worker cleanup.</param>
    /// <param name="errorWriter">The private diagnostic stream used for fixed fixture markers.</param>
    /// <returns><see langword="true"/> only when the lifecycle accepted the disposer; each helper is single-use.</returns>
    /// <remarks>
    /// Registration rejection writes no ARM marker and is fail-closed. The registered callback writes its
    /// INVOKED marker at most once before returning; neither marker grants cleanup or evidence authority.
    /// </remarks>
    internal bool RegisterDisposer(EvidenceWorkerExecution execution, TextWriter errorWriter)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(errorWriter);
        if (Interlocked.Exchange(ref _disposerRegistrationAttempted, 1) != 0) return false;

        if (!execution.RegisterDisposer(_ =>
            {
                if (Interlocked.Exchange(ref _disposerInvoked, 1) == 0)
                    errorWriter.WriteLine("FIXTURE_N11_DISPOSER_INVOKED");
                return ValueTask.CompletedTask;
            }))
            return false;

        errorWriter.WriteLine("FIXTURE_N11_DISPOSER_ARMED");
        return true;
    }

    /// <summary>Writes the fixed factory-entry marker through the same seam used by the blocking callback.</summary>
    /// <param name="errorWriter">The private diagnostic stream used for fixed fixture markers.</param>
    /// <remarks>This metadata-only seam does not enter the deliberate stall or imply worker execution.</remarks>
    internal void WriteInputFactoryEnteredMarker(TextWriter errorWriter)
    {
        ArgumentNullException.ThrowIfNull(errorWriter);
        errorWriter.WriteLine("FIXTURE_N11_INPUT_FACTORY_ENTERED");
    }

#if EVIDENCE_PRIVATE_N11
    /// <summary>Writes the fixed N11 entry marker, then blocks synchronously before returning an operation task.</summary>
    /// <returns>No value; the method blocks indefinitely in the intended private qualification image.</returns>
    /// <remarks>The marker is diagnostic data only. No successful task, cleanup or settlement is implied.</remarks>
    internal Task<(EvidencePolicy Policy, EvidencePlan Plan, byte[]? DiffBytes)> StallBeforeReturningTask()
    {
        WriteInputFactoryEnteredMarker(Console.Error);
        Thread.Sleep(Timeout.Infinite);
        throw new InvalidOperationException("The fixed synchronous input factory unexpectedly returned.");
    }
#endif
}
