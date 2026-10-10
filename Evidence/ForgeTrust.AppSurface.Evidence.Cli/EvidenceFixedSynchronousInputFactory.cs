using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>Implements the compile-selected private N11 synchronous input-factory stall.</summary>
/// <remarks>
/// <see cref="CreateForProtectedRole"/> returns an instance only in the N11 build; ordinary builds
/// return null and retain the normal input factory. The caller invokes <see cref="StallBeforeReturningTask"/>
/// inside the existing tracked Admission callback after authenticated READY. The method deliberately
/// blocks before returning a task, allowing the original lifecycle owner and external containment to
/// observe the failure. It adds no runtime selector, deadline, cancellation path or settlement authority.
/// </remarks>
internal sealed class EvidenceFixedSynchronousInputFactory
{
    private EvidenceFixedSynchronousInputFactory() { }

    /// <summary>Creates the fixed stall helper only when the whole image was compiled for N11.</summary>
    /// <returns>The N11 helper, or null for every other qualification and the ordinary build.</returns>
    internal static EvidenceFixedSynchronousInputFactory? CreateForProtectedRole() =>
        EvidenceNativeQualification.WorkerStallEnabled ? new EvidenceFixedSynchronousInputFactory() : null;

    /// <summary>Writes the fixed N11 entry marker, then blocks synchronously before returning an operation task.</summary>
    /// <returns>No value; the method blocks indefinitely in the intended private qualification image.</returns>
    /// <remarks>The marker is diagnostic data only. No successful task, cleanup or settlement is implied.</remarks>
    internal Task<(EvidencePolicy Policy, EvidencePlan Plan, byte[]? DiffBytes)> StallBeforeReturningTask()
    {
        Console.Error.WriteLine("FIXTURE_N11_INPUT_FACTORY_ENTERED");
        Thread.Sleep(Timeout.Infinite);
        throw new InvalidOperationException("The fixed synchronous input factory unexpectedly returned.");
    }
}
