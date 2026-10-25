namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Calculates the producer runtime from the owner's current original job remainder.</summary>
/// <remarks>
/// This arithmetic creates no timer, owner, process or deadline. The caller retains its authenticated
/// owner and request validation, samples the original remainder once, and supplies the existing stopping,
/// cleanup and collection reservations. The resulting runtime still requires the native recipe and owner
/// checks. Never supply a fresh job duration or add a reservation back after an earlier operation.
/// </remarks>
internal static class SupervisionProducerRuntimeAllowance
{
    /// <summary>Subtracts all existing reservations and caps usable runtime at the unchanged producer maximum.</summary>
    /// <param name="originalRemaining">The authentic owner's current original job remainder.</param>
    /// <param name="stopping">The validated request's original stopping reservation.</param>
    /// <param name="cleanup">The original owner cleanup reservation.</param>
    /// <param name="collection">The validated request's original collection reservation.</param>
    /// <returns>A positive runtime no greater than <see cref="LinuxProducerUnit.MaximumRuntimeSeconds"/>.</returns>
    /// <exception cref="ForgeTrust.AppSurface.Evidence.Contracts.EvidenceAdmissionException">
    /// Fixed ASEVD410 when the original remainder cannot cover every reservation and positive runtime.
    /// </exception>
    internal static TimeSpan Calculate(TimeSpan originalRemaining, TimeSpan stopping,
        TimeSpan cleanup, TimeSpan collection)
    {
        var available = originalRemaining - stopping - cleanup - collection;
        var maximum = TimeSpan.FromSeconds(LinuxProducerUnit.MaximumRuntimeSeconds);
        var runtime = available > maximum ? maximum : available;
        if (runtime <= TimeSpan.Zero) throw LinuxSystemdBackend.InvalidControl();
        return runtime;
    }
}
