namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>The shared explicit-mode and legacy Observation-only migration rule.</summary>
internal static class EvidenceModeSelection
{
    /// <summary>Requires a named mode; legacy true means only Observation and cannot conflict with Trusted.</summary>
    internal static EvidenceExecutionMode Select(string? mode, bool legacyObservationOnly = false)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            if (legacyObservationOnly) return EvidenceExecutionMode.Observation;
            throw new EvidenceAdmissionException("ASEVD401", "Select --mode trusted or --mode observation explicitly.");
        }

        var selected = mode.ToLowerInvariant() switch
        {
            "trusted" => EvidenceExecutionMode.Trusted,
            "observation" => EvidenceExecutionMode.Observation,
            _ => throw new EvidenceAdmissionException("ASEVD401", "Select --mode trusted or --mode observation explicitly."),
        };
        if (legacyObservationOnly && selected != EvidenceExecutionMode.Observation)
            throw new EvidenceAdmissionException("ASEVD401", "The legacy Observation alias conflicts with the selected mode.");
        return selected;
    }
}
