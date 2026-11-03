namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Immutable producer-workspace flags sampled by the original workspace lock.</summary>
internal readonly record struct LinuxProducerWorkspaceStateFacts(
    bool Quarantined,
    bool Sealed,
    bool HandlesClosed,
    bool SealAttempted,
    bool ProducerRetentionAttempted,
    bool ResultsTokenAttempted,
    bool SlashPresent,
    bool RunPresent);

/// <summary>Data-only producer workspace state gates; no owner, lock, handle, deadline or native authority.</summary>
internal static class LinuxProducerWorkspaceStatePolicy
{
    /// <summary>Preserves the state gate before retaining the producer process.</summary>
    internal static void RequireRetainProducer(LinuxProducerWorkspaceStateFacts facts)
    {
        if (facts.Quarantined || facts.Sealed || facts.SealAttempted || facts.ProducerRetentionAttempted)
            Invalid();
    }

    /// <summary>Preserves the active producer-work gate after reference checks and before native owner authentication.</summary>
    internal static void RequireOwnedBy(LinuxProducerWorkspaceStateFacts facts)
    {
        if (facts.Quarantined || facts.Sealed || facts.SealAttempted || facts.HandlesClosed)
            Invalid();
    }

    /// <summary>Preserves the cleanup-control state gate before native control-identity checks.</summary>
    internal static void RequireControlOwnedBy(LinuxProducerWorkspaceStateFacts facts)
    {
        if (facts.HandlesClosed || !facts.SlashPresent || !facts.RunPresent)
            Invalid();
    }

    /// <summary>Preserves the one-shot state gate before preparing the results token directory.</summary>
    internal static void RequireResultsTokenPreparation(LinuxProducerWorkspaceStateFacts facts)
    {
        if (facts.ResultsTokenAttempted)
            Invalid();
    }

    /// <summary>Preserves the sealed, live-handle state gate before account release rechecks.</summary>
    internal static void RequireSealedForAccountRelease(LinuxProducerWorkspaceStateFacts facts)
    {
        if (!facts.Sealed || facts.Quarantined || facts.HandlesClosed)
            Invalid();
    }

    private static void Invalid() => throw LinuxProducerWorkspaceLayout.Invalid();
}
