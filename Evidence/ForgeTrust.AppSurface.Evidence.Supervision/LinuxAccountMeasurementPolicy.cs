namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Pure account-state decisions over copied scalar data; this policy grants no native authority.</summary>
/// <remarks>
/// The snapshot is an ordinary data value and can be evaluated in every build variant. The private C4 caller
/// copies it only after its authentic owner and NSS checks. This policy performs no I/O, account operation,
/// ownership check, or admission decision; its result is meaningful only to that caller's existing flow.
/// </remarks>
internal static class SupervisionAccountStatePolicy
{
    /// <summary>A detached copy of the state fields used by the creation and close predicates.</summary>
    internal readonly record struct Snapshot(int Utilities, int SettledUtilities, int FailedUtilities,
        bool CloseCompleted, bool Quarantined, bool ReservationFailed, bool IdentitiesPublished,
        bool FirstFailurePresent);

    /// <summary>Checks the exact expected state immediately after successful account creation.</summary>
    internal static bool IsSuccessfulCreationState(Snapshot state) =>
        !(state.Utilities != 3 || state.SettledUtilities != 3 || state.FailedUtilities != 0
            || state.CloseCompleted || state.Quarantined || state.ReservationFailed
            || !state.IdentitiesPublished || state.FirstFailurePresent);

    /// <summary>Checks the exact expected state after the original shared close task has joined.</summary>
    internal static bool IsSuccessfulClosedState(Snapshot state) =>
        !(!state.CloseCompleted || state.Quarantined || state.ReservationFailed
            || state.FirstFailurePresent || state.FailedUtilities != 0
            || state.Utilities is < 6 or > 8
            || state.SettledUtilities != state.Utilities);
}
