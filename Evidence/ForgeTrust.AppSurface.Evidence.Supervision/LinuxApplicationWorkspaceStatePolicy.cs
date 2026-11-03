namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Copied workspace state flags used only by the original locked state gates.</summary>
internal readonly record struct LinuxApplicationWorkspaceStateFacts(
    bool Initialized,
    bool Quarantined,
    bool Sealed,
    bool HandlesClosed,
    bool SealAttempted,
    bool ApplicationRetentionAttempted,
    bool ScratchMounted,
    bool ReleaseGuardPassed);

/// <summary>Data-only workspace state predicates; no owner, handle, lock, deadline or native authority is created.</summary>
internal static class LinuxApplicationWorkspaceStatePolicy
{
    /// <summary>Enforces the identical initialized, unquarantined bundle-use gate used by recheck and ordinary access.</summary>
    /// <param name="facts">State copied by the locked native caller at the original gate.</param>
    internal static void RequireBundleUse(LinuxApplicationWorkspaceStateFacts facts)
    {
        if (!facts.Initialized || facts.Quarantined || facts.Sealed || facts.HandlesClosed || facts.SealAttempted)
            Invalid();
    }

    /// <summary>Enforces the pre-dispatch application-retention state gate.</summary>
    /// <param name="facts">State copied by the locked native caller at the original gate.</param>
    internal static void RequireRetainApplication(LinuxApplicationWorkspaceStateFacts facts)
    {
        if (!facts.Initialized || facts.Quarantined || facts.SealAttempted || facts.ApplicationRetentionAttempted)
            Invalid();
    }

    /// <summary>Enforces the retained control-owner state gate without checking native identity.</summary>
    /// <param name="facts">State copied by the locked native caller at the original gate.</param>
    internal static void RequireControlOwnedBy(LinuxApplicationWorkspaceStateFacts facts)
    {
        if (!facts.Initialized || facts.Quarantined || facts.Sealed || facts.HandlesClosed)
            Invalid();
    }

    /// <summary>Enforces the state flags required before the authentic seal procedure begins.</summary>
    /// <param name="facts">State copied by the locked native caller at the original gate.</param>
    internal static void RequireSealCore(LinuxApplicationWorkspaceStateFacts facts)
    {
        if (!facts.Initialized || facts.Quarantined || facts.HandlesClosed || !facts.ScratchMounted)
            Invalid();
    }

    /// <summary>Enforces active workspace state while preserving the caller's owner and deadline checks.</summary>
    /// <param name="facts">State copied by the locked native caller at the original gate.</param>
    internal static void RequireActive(LinuxApplicationWorkspaceStateFacts facts)
    {
        if (facts.Quarantined || facts.SealAttempted || facts.Sealed || facts.HandlesClosed)
            Invalid();
    }

    /// <summary>Enforces the sealed state required before account-release rechecks.</summary>
    /// <param name="facts">State copied by the locked native caller at the original gate.</param>
    internal static void RequireSealedForRelease(LinuxApplicationWorkspaceStateFacts facts)
    {
        if (!facts.Sealed || facts.Quarantined || facts.HandlesClosed || facts.ScratchMounted)
            Invalid();
    }

    /// <summary>Enforces the final state and release-guard latch before descriptor disposal.</summary>
    /// <param name="facts">State copied by the locked native caller at the original gate.</param>
    internal static void RequireDispose(LinuxApplicationWorkspaceStateFacts facts)
    {
        if (!facts.Sealed || facts.Quarantined || !facts.ReleaseGuardPassed)
            Invalid();
    }

    private static void Invalid() => throw LinuxApplicationWorkspaceLayout.Invalid();
}
