using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Stable command and environment names shared by the Keycloak host and its package-owned worker.</summary>
/// <remarks>These values cross a process boundary and must remain compatible with existing callers.</remarks>
internal static class KeycloakWorkerProtocol
{
    /// <summary>Starts the realm-reconciliation worker mode.</summary>
    internal const string Reconcile = "--appsurface-keycloak-reconcile";

    /// <summary>Starts the fenced memory-anchor worker mode.</summary>
    internal const string FencedMemoryAnchor = "--appsurface-keycloak-fenced-memory-anchor";

    /// <summary>Starts the credential-free owner-fence worker mode.</summary>
    internal const string OwnerFence = "--appsurface-keycloak-owner-fence";

    /// <summary>Initializes ownership of a new, empty Keycloak volume.</summary>
    internal const string InitializeVolume = "--appsurface-keycloak-initialize-volume";

    /// <summary>Reads the Keycloak volume incarnation.</summary>
    internal const string VolumeIncarnation = "--appsurface-keycloak-volume-incarnation";

    /// <summary>Reads Keycloak volume and database ownership metadata.</summary>
    internal const string VolumeOwnership = "--appsurface-keycloak-volume-ownership";

    /// <summary>Names the environment variable containing the PEM for the expected server certificate.</summary>
    internal const string CertificateFileEnvironmentVariable = "APPSURFACE_KEYCLOAK_CA_CERTIFICATE_FILE";

    /// <summary>Returns the listening marker path by appending its suffix to a ready path or leaf.</summary>
    internal static string ListeningMarkerPath(string readyPath) => readyPath + ".listening";
}

/// <summary>JSON bindings shared by the package-owned Keycloak worker and its host-side stager.</summary>
internal sealed record KeycloakWorkerBindings(
    string BaseAddress,
    string AdminUsername,
    string AdminPassword,
    string? ClientSecret,
    string? CaptureRoot,
    string? CaptureRunId,
    ImmutableDictionary<string, string>? BrokerSecrets)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KeycloakClientCredentialReference? ClientCredentialReference { get; init; }
    public string? ClientCredentialReferenceFile { get; init; }
    public string? ExpectedCaptureRunId { get; init; }

    /// <summary>Returns the fixed contract type name without exposing credentials or capture metadata.</summary>
    public override string ToString() => nameof(KeycloakWorkerBindings);
}

/// <summary>Metadata-only volume owner response emitted by the realm-ready worker.</summary>
internal sealed record KeycloakWorkerVolumeOwnership(
    KeycloakVolumeIncarnation? Incarnation,
    uint Uid,
    uint Gid,
    [property: JsonRequired] KeycloakWorkerDatabaseOwnership? Database);

/// <summary>UID and GID of the pinned H2 database file, or null when the database is absent.</summary>
internal sealed record KeycloakWorkerDatabaseOwnership(
    [property: JsonRequired] uint Uid,
    [property: JsonRequired] uint Gid);
