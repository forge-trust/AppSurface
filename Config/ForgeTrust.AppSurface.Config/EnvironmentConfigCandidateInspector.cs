namespace ForgeTrust.AppSurface.Config;

/// <summary>Projects the frozen native rules and separate direct-composition rules without source dependencies.</summary>
internal sealed class EnvironmentConfigCandidateInspector(EnvironmentConfigMappingSnapshot snapshot)
    : IEnvironmentConfigCandidateInspector
{
    /// <inheritdoc />
    public EnvironmentNativeKeyPlan InspectNativeKey(string environment, AppSurfaceConfigKey key)
    {
        Validate(environment, nameof(environment), nameof(InspectNativeKey));
        Validate(key, nameof(key), nameof(InspectNativeKey));
        return Native(environment, key, ConfigKeyInputOrigin.Typed, null);
    }

    /// <inheritdoc />
    public EnvironmentNativeKeyPlan InspectNativeKey(string environment, string strictInput)
    {
        Validate(environment, nameof(environment), nameof(InspectNativeKey));
        Validate(strictInput, nameof(strictInput), nameof(InspectNativeKey));
        AppSurfaceConfigKey key;
        try
        {
            key = AppSurfaceConfigKey.Parse(strictInput);
        }
        catch (FormatException exception)
        {
            EnvironmentConfigCandidateErrors.Enrich(exception, "config-key-invalid", nameof(InspectNativeKey),
                "Use the documented strict colon-delimited logical-key grammar.", "fix_and_retry",
                EnvironmentConfigCandidateErrors.LogicalKeyGuideUri + "#config-key-invalid");
            throw;
        }

        return Native(environment, key, ConfigKeyInputOrigin.StrictString, strictInput);
    }

    /// <inheritdoc />
    public EnvironmentCompositionPathPlan InspectCompositionPath(string environment, AppSurfaceConfigKey path)
    {
        Validate(environment, nameof(environment), nameof(InspectCompositionPath));
        Validate(path, nameof(path), nameof(InspectCompositionPath));
        return new(path, ConfigEnvironmentCandidates.GetPathMetadataCandidates(environment, path.Segments));
    }

    private EnvironmentNativeKeyPlan Native(string environment, AppSurfaceConfigKey key,
        ConfigKeyInputOrigin origin, string? originalInput)
    {
        System.Collections.Immutable.ImmutableArray<EnvironmentNativeCandidate> candidates;
        try
        {
            candidates = EnvironmentConfigCodec.GetCandidates(environment, key, origin, originalInput, snapshot.Mappings);
        }
        catch (InvalidOperationException exception) when (exception.Message == EnvironmentConfigCodec.UnencodableMessage)
        {
            EnvironmentConfigCandidateErrors.Enrich(exception, "config-candidate-unencodable", nameof(InspectNativeKey),
                "Correct the key or configure its exact native mapping before constructing the container.",
                "fix_before_rebuild", EnvironmentConfigCandidateErrors.LogicalKeyGuideUri + "#config-candidate-unencodable");
            throw;
        }

        return new(key, candidates);
    }

    private static void Validate(object? value, string parameter, string operation)
    {
        if (value is null)
        {
            throw EnvironmentConfigCandidateErrors.Null(parameter, operation);
        }
    }
}
