using System.Collections.Immutable;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>Exact image identity, including the index and selected platform manifest.</summary>
public sealed record KeycloakImageIdentity(string Index, string PlatformManifest, string Platform)
{
    /// <summary>Creates a declaration; recipe validation checks its digest identities and platform before effects.</summary>
    public static KeycloakImageIdentity Create(string index, string platformManifest, string platform) => new(index, platformManifest, platform);
    /// <summary>Material ID containing the exact upstream index JSON.</summary>
    public string? IndexMaterialId { get; init; }
    /// <summary>Material ID containing the exact selected platform archive.</summary>
    public string? ArchiveMaterialId { get; init; }
    /// <summary>Exact config digest observed in the selected platform manifest.</summary>
    public string? ConfigDigest { get; init; }
    /// <summary>Ordered exact layer diff IDs observed in the selected platform image.</summary>
    public ImmutableArray<string> DiffIds { get; init; }
}

/// <summary>A safe OCI image reference and its exact content digest.</summary>
public sealed record OciIdentity(string Reference, string Digest)
{
    /// <summary>Stable recipe-local role identity used to select runtime or frontend OCI input.</summary>
    public string? Id { get; init; }
    /// <summary>Explicit OCI platform, either linux/amd64 or linux/arm64.</summary>
    public string? Platform { get; init; }
    /// <summary>Recipe material ID containing the exact OCI archive or manifest bytes.</summary>
    public string? ArchiveMaterialId { get; init; }
    /// <summary>Exact platform manifest digest selected from the index.</summary>
    public string? PlatformManifestDigest { get; init; }
    /// <summary>Exact OCI config digest.</summary>
    public string? ConfigDigest { get; init; }
    /// <summary>Ordered OCI layer diff IDs.</summary>
    public ImmutableArray<string> DiffIds { get; init; }
    /// <summary>Recipe material ID containing the exact upstream index JSON.</summary>
    public string? IndexMaterialId { get; init; }
}

/// <summary>One explicitly authorized, bounded material input.</summary>
public sealed record KeycloakMaterialReference(string Id, string ExactHttpsUri, string Sha256, long MaxBytes)
{
    /// <summary>True when bytes are host-captured and have no downloadable HTTPS URI.</summary>
    public bool CapturedHostInput { get; init; }
}

/// <summary>Signed repository snapshot identity and its finite package authority.</summary>
public sealed record SignedRepositorySnapshot(string Id, string ExactHttpsUri, string Sha256, string SigningFingerprint, string Suite, ImmutableArray<string> Components, ImmutableArray<string> Architectures)
{
    /// <summary>Policy material ID containing the approved signing keyring.</summary>
    public string? KeyringMaterialId { get; init; }
    /// <summary>Relative package-index paths mapped to exact policy material IDs.</summary>
    public ImmutableDictionary<string, string> PackageIndexMaterialIds { get; init; } = null!;
}

/// <summary>One package in a captured runtime closure.</summary>
public sealed record RuntimePackageSpec(string Name, string Version, string Sha256, string Architecture)
{
    /// <summary>Policy material ID containing the exact package bytes.</summary>
    public string? MaterialId { get; init; }
}

/// <summary>Identity of a package that must be absent from the resulting runtime; removed bytes are not captured.</summary>
public sealed record KeycloakRemovedPackageSpec(string Name, string Architecture, string ExpectedVersion);
/// <summary>Coordinates for one Maven or package artifact.</summary>
public sealed record ArtifactCoordinate(string Group, string Artifact, string Version, string? Classifier, string Extension);
/// <summary>A content-addressed artifact and its intended image destination.</summary>
public sealed record ArtifactReference(ArtifactCoordinate Coordinate, string Sha256, string Path, string Destination);
/// <summary>One exact replacement in an alignment group.</summary>
public sealed record ArtifactReplacement(ArtifactReference Original, ArtifactReference Replacement);
/// <summary>Complete, closed replacement membership; no implicit or extra members are allowed.</summary>
public sealed record ArtifactReplacementGroup(string Id, ImmutableArray<ArtifactReplacement> Replacements, ImmutableArray<ArtifactCoordinate> RequiredMembers)
{
    /// <summary>Copies caller collections into immutable storage.</summary>
    public static ArtifactReplacementGroup Create(string id, IEnumerable<ArtifactReplacement> replacements, IEnumerable<ArtifactCoordinate> requiredMembers) => new(id, replacements.ToImmutableArray(), requiredMembers.ToImmutableArray());
}

/// <summary>Versioned built-in runtime profile selection.</summary>
public sealed record RuntimeProfile(string Id, string Version, string TemplateName, ImmutableArray<string> SupportedSourceProfiles)
{
    /// <summary>Copies source-profile identities into immutable storage.</summary>
    public static RuntimeProfile Create(string id, string version, string templateName, IEnumerable<string> supportedSourceProfiles) => new(id, version, templateName, supportedSourceProfiles.ToImmutableArray());
    /// <summary>Exact runtime OCI identities selected by platform.</summary>
    public ImmutableArray<OciIdentity> RuntimeOciIdentities { get; init; }
    /// <summary>Signed repository snapshots used by this profile.</summary>
    public ImmutableArray<SignedRepositorySnapshot> RepositorySnapshots { get; init; }
    /// <summary>Complete installed package closure.</summary>
    public ImmutableArray<RuntimePackageSpec> RequiredPackages { get; init; }
    /// <summary>Packages that must be absent from the resulting runtime.</summary>
    public ImmutableArray<KeycloakRemovedPackageSpec> RemovedPackages { get; init; }
    /// <summary>Expected runtime user, entrypoint, certificates, and other observation IDs.</summary>
    public ImmutableArray<string> ExpectedObservations { get; init; }
}

/// <summary>Versioned built-in embedded archive transformation selection.</summary>
public sealed record EmbeddedArchiveReplacement(string Id, string Version, string TemplateName, ArtifactCoordinate Source, ArtifactCoordinate Replacement, ImmutableArray<string> ClassPrefixes, ImmutableArray<string> VersionedClasses, ImmutableArray<string> ServicePaths)
{
    /// <summary>Complete content-addressed source/replacement modules and image destination.</summary>
    public EmbeddedArchiveModuleSet? ModuleSet { get; init; }
}

/// <summary>Complete source and replacement archive module material and destination.</summary>
public sealed record EmbeddedArchiveModuleSet(ArtifactReference SourceArtifact, ArtifactReference ReplacementArtifact, string Destination, ImmutableDictionary<string, string> SourceModuleSha256ByIdentity, ImmutableDictionary<string, string> ReplacementModuleSha256ByIdentity)
{
}

/// <summary>Recipe operation mode.</summary>
public enum KeycloakRecipeMode
{
    /// <summary>Security operations are enabled.</summary>
    Security,
    /// <summary>Only reviewed theme inputs are enabled.</summary>
    ThemeOnly
}

/// <summary>One of the deliberately closed recipe operation kinds.</summary>
public abstract record KeycloakOperation(string Id)
{
    /// <summary>Artifact alignment replacement operation.</summary>
    public sealed record ArtifactReplacementGroupOperation(ArtifactReplacementGroup Group) : KeycloakOperation(Group.Id);
    /// <summary>Runtime profile operation.</summary>
    public sealed record RuntimeProfileOperation(RuntimeProfile Profile) : KeycloakOperation(Profile.Id);
    /// <summary>Embedded archive replacement operation.</summary>
    public sealed record EmbeddedArchiveReplacementOperation(EmbeddedArchiveReplacement Replacement) : KeycloakOperation(Replacement.Id);
}

/// <summary>Reviewed theme or captured-host input identified by a safe content digest.</summary>
public sealed record KeycloakThemeInput(string Id, string Sha256, string? ExactUri, string? CapturedHostInput)
{
    /// <summary>Recipe material ID for the exact local or remote bytes.</summary>
    public string? LocalInputId { get; init; }
    /// <summary>Safe absolute image-internal destination for the reviewed theme input.</summary>
    public string? DestinationPath { get; init; }
}

/// <summary>One explicitly bound local input used only during capture, never recipe identity.</summary>
public sealed record KeycloakLocalInputBinding(string MaterialId, string LocalPath, string Sha256, long MaxBytes);
/// <summary>Host-owned capture bindings for a reviewed recipe; local paths never enter canonical recipe JSON.</summary>
public sealed record KeycloakCaptureInputs(string ReviewedRecipeId, ImmutableArray<KeycloakLocalInputBinding> LocalInputs)
{
    /// <summary>Copies local capture bindings into immutable storage.</summary>
    public static KeycloakCaptureInputs Create(string reviewedRecipeId, IEnumerable<KeycloakLocalInputBinding> localInputs) => new(reviewedRecipeId, localInputs.ToImmutableArray());
}

/// <summary>Reviewed upstream template baseline whose archive member and captured bytes must agree.</summary>
public sealed record KeycloakTemplateBaseline(string ArchivePath, string ArchiveSha256, string Entry, string Sha256, string MaterialId);
/// <summary>Immutable recipe declaration for a supported Keycloak transformation.</summary>
public sealed record KeycloakSecurityRecipe(int SchemaVersion, KeycloakImageIdentity SourceImage, ImmutableArray<OciIdentity> RuntimeFrontendOciIdentities, ImmutableArray<KeycloakOperation> Operations, KeycloakRecipeMode Mode, ImmutableArray<KeycloakThemeInput> ThemeInputs, ImmutableArray<string> CapturedHostInputs)
{
    /// <summary>Reviewed upstream template baseline, required when a captured theme replaces FreeMarker templates.</summary>
    public KeycloakTemplateBaseline? TemplateBaseline { get; init; }
    /// <summary>Exact supported Keycloak version, such as 26.7.2.</summary>
    public string KeycloakVersion { get; init; } = string.Empty;
    /// <summary>Reviewed image-internal entrypoint path; never a command string.</summary>
    public string EntrypointPath { get; init; } = string.Empty;
    /// <summary>Explicit content-addressed materials referenced by this recipe.</summary>
    public ImmutableArray<KeycloakMaterialReference> MaterialReferences { get; init; }
    /// <summary>Explicit source profile identities selected by the recipe.</summary>
    public ImmutableArray<string> SourceProfiles { get; init; }
    /// <summary>Finite acquisition and transformation bounds.</summary>
    public KeycloakResourceLimits ResourceLimits { get; init; } = null!;

    /// <summary>Creates a recipe while copying all enumerable inputs.</summary>
    public static KeycloakSecurityRecipe Create(int schemaVersion, KeycloakImageIdentity sourceImage, IEnumerable<OciIdentity> runtimeFrontendOciIdentities, IEnumerable<KeycloakOperation> operations, KeycloakRecipeMode mode, IEnumerable<KeycloakThemeInput> themeInputs, IEnumerable<string> capturedHostInputs) => new(schemaVersion, sourceImage, runtimeFrontendOciIdentities.ToImmutableArray(), operations.ToImmutableArray(), mode, themeInputs.ToImmutableArray(), capturedHostInputs.ToImmutableArray());
}

/// <summary>Exact HTTPS origin authorization and material digest binding.</summary>
public sealed record KeycloakMaterialTrustPolicy(string Version, string OwnerApprovalReference, ImmutableArray<string> PermittedHttpsOrigins, ImmutableArray<string> PermittedPathMappings, ImmutableArray<string> PermittedCoordinateMappings, ImmutableDictionary<string, string> MaterialSha256ByIdentity, ImmutableArray<string> SigningFingerprints, ImmutableArray<string> SigningAlgorithms, ImmutableArray<string> WithdrawnKeys, ImmutableArray<string> HistoricalExactReleaseBytes)
{
    /// <summary>Creates a policy with immutable copies of all collection inputs.</summary>
    public static KeycloakMaterialTrustPolicy Create(string version, string ownerApprovalReference, IEnumerable<string> origins, IEnumerable<string> paths, IEnumerable<string> coordinates, IEnumerable<KeyValuePair<string, string>> materialDigests, IEnumerable<string> fingerprints, IEnumerable<string> algorithms, IEnumerable<string> withdrawnKeys, IEnumerable<string> historicalExactReleaseBytes) => new(version, ownerApprovalReference, origins.ToImmutableArray(), paths.ToImmutableArray(), coordinates.ToImmutableArray(), materialDigests.ToImmutableDictionary(StringComparer.Ordinal), fingerprints.ToImmutableArray(), algorithms.ToImmutableArray(), withdrawnKeys.ToImmutableArray(), historicalExactReleaseBytes.ToImmutableArray());
}

/// <summary>Explicit finite limits that participate in recipe identity.</summary>
public sealed record KeycloakResourceLimits(long MaxMaterialBytes, long MaxArchiveEntries, long MaxArchiveExpandedBytes, long MaxOutputBytes, int MaxOperationSeconds);
/// <summary>Stable stage at which an operation stopped.</summary>
public enum KeycloakSafePhase
{
    /// <summary>Declaration validation.</summary>
    Validation,
    /// <summary>Material acquisition.</summary>
    Acquisition,
    /// <summary>Input capture.</summary>
    Capture,
    /// <summary>Context materialization.</summary>
    Materialization,
    /// <summary>Archive transformation.</summary>
    Archive,
    /// <summary>Atomic publication.</summary>
    Publication,
    /// <summary>Host build.</summary>
    Build,
    /// <summary>Image inspection.</summary>
    Inspection,
    /// <summary>Runtime probe.</summary>
    Probe,
    /// <summary>Evidence serialization.</summary>
    Evidence,
    /// <summary>Cancellation handling.</summary>
    Cancellation,
    /// <summary>Owned cleanup.</summary>
    Cleanup
}

/// <summary>Known effect state after a failed operation.</summary>
public enum KeycloakEffectState
{
    /// <summary>No effect was committed.</summary>
    None,
    /// <summary>Owned staged data may exist.</summary>
    Staged,
    /// <summary>Publication committed.</summary>
    Committed,
    /// <summary>Commit state cannot be classified.</summary>
    Unknown
}

/// <summary>Recovery class exposed to noninteractive callers.</summary>
public enum KeycloakRecoveryClass
{
    /// <summary>Correct the supplied declaration.</summary>
    CorrectInput,
    /// <summary>Request owner intervention.</summary>
    OwnerAction,
    /// <summary>One explicitly requested transient retry.</summary>
    ExplicitTransientRetryOnce,
    /// <summary>Inspect the effect before retrying.</summary>
    InspectBeforeRetry,
    /// <summary>Do not retry.</summary>
    NoRetry
}

/// <summary>Stable failure categories; enum names are canonical serialized values.</summary>
public enum KeycloakFailureCode
{
    /// <summary>Required value missing.</summary>
    Required = 0,
    /// <summary>Value malformed.</summary>
    Invalid = 1,
    /// <summary>Version or capability unsupported.</summary>
    Unsupported = 2,
    /// <summary>Values conflict.</summary>
    Conflict = 3,
    /// <summary>Material unavailable.</summary>
    MaterialUnavailable = 4,
    /// <summary>Provenance rejected.</summary>
    ProvenanceRejected = 5,
    /// <summary>Digest mismatch.</summary>
    DigestMismatch = 6,
    /// <summary>Captured input invalid.</summary>
    CaptureInvalid = 7,
    /// <summary>Archive integrity failure.</summary>
    ArchiveIntegrity = 8,
    /// <summary>Archive limit exceeded.</summary>
    ArchiveLimit = 9,
    /// <summary>Publication failed.</summary>
    PublicationFailed = 10,
    /// <summary>Host build failed.</summary>
    BuildFailed = 11,
    /// <summary>Image inspection failed.</summary>
    InspectionFailed = 12,
    /// <summary>Runtime probe failed.</summary>
    ProbeFailed = 13,
    /// <summary>Evidence was not published.</summary>
    EvidenceNotPublished = 14,
    /// <summary>Operation was cancelled.</summary>
    Cancelled = 15,
    /// <summary>Unexpected safe-boundary failure.</summary>
    Unexpected = 16
}

/// <summary>Opaque correlation identity suitable for logs and support requests.</summary>
public sealed record KeycloakCorrelationId(string Value);
/// <summary>Safe, noninteractive typed failure. Cause text and machine paths are intentionally excluded.</summary>
public sealed class KeycloakSecurityException : Exception
{
    private const string DiagnosticReferenceRoot = "https://github.com/forge-trust/AppSurface/blob/main/Auth/ForgeTrust.AppSurface.Auth.Keycloak/docs/diagnostics.md";
    /// <summary>Creates a safe diagnostic failure without retaining a raw cause.</summary>
    public KeycloakSecurityException(KeycloakFailureCode code, KeycloakSafePhase phase, string operationId, KeycloakEffectState effect, KeycloakRecoveryClass recovery, string message, string nextAction, KeycloakCorrelationId correlationId) : base(message)
    {
        (Code, Phase, OperationId, Effect, Recovery, NextAction, CorrelationId) = (code, phase, operationId, effect, recovery, nextAction, correlationId);
    }

    /// <summary>Stable failure code.</summary>
    public KeycloakFailureCode Code { get; }
    /// <summary>Safe phase.</summary>
    public KeycloakSafePhase Phase { get; }
    /// <summary>Safe operation identity.</summary>
    public string OperationId { get; }
    /// <summary>Observed side-effect state.</summary>
    public KeycloakEffectState Effect { get; }
    /// <summary>Permitted recovery class.</summary>
    public KeycloakRecoveryClass Recovery { get; }
    /// <summary>Safe corrective action.</summary>
    public string NextAction { get; }
    /// <summary>Opaque correlation value.</summary>
    public KeycloakCorrelationId CorrelationId { get; }
    /// <summary>Stable documentation reference for this failure code.</summary>
    public string DiagnosticReference => $"{DiagnosticReferenceRoot}#{Code.ToString().ToLowerInvariant()}";
    /// <summary>Safe profile or operation key for boundary diagnostics.</summary>
    public string ProfileKey => OperationId;
    /// <summary>Retry guidance computed from the stable recovery and effect fields.</summary>
    public KeycloakRetryMetadata RetryMetadata => KeycloakRetryMetadata.For(Recovery, Effect);
}

/// <summary>Whether one explicit fresh invocation may be attempted after a transient result.</summary>
public sealed record KeycloakRetryMetadata(bool AutomaticRetry, bool ExplicitFreshAttemptAllowed, int MaximumAdditionalAttempts, bool RequiresInspectionFirst)
{
    /// <summary>Computes bounded retry guidance from the exception's stable fields.</summary>
    public static KeycloakRetryMetadata For(KeycloakRecoveryClass recovery, KeycloakEffectState effect) => effect == KeycloakEffectState.Unknown || recovery == KeycloakRecoveryClass.InspectBeforeRetry ? InspectBeforeRetry : recovery == KeycloakRecoveryClass.ExplicitTransientRetryOnce && effect is KeycloakEffectState.None or KeycloakEffectState.Staged ? ExplicitTransientOnce : None;
    /// <summary>No automatic retry metadata, as required by the recipe contract.</summary>
    public static KeycloakRetryMetadata None { get; } = new(false, false, 0, false);
    /// <summary>Metadata for an eligible transient acquisition or read-only probe failure.</summary>
    public static KeycloakRetryMetadata ExplicitTransientOnce { get; } = new(false, true, 1, false);
    /// <summary>Metadata for an uncertain effect.</summary>
    public static KeycloakRetryMetadata InspectBeforeRetry { get; } = new(false, false, 0, true);
}
