using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>Opts a local Run into the qualified macOS ARM64 owner and Linux ARM64 container boundary.</summary>
/// <remarks>Candidate profile: registration is effect-free. Use the returned supervised run helper for owned stop and a process exit result.</remarks>
public sealed class AppSurfaceKeycloakSupervisedOptions
{
    private const string QualifiedKeycloakRepository = "quay.io/keycloak/keycloak";
    private const string QualifiedKeycloakTag = "26.7.2";
    private const string QualifiedKeycloakDigest = "sha256:831330513f55695572286e521f94fcd3c7e285250ed5b848090265a33192f669";

    /// <summary>The digest-only default for the image qualified for persisted database ownership.</summary>
    internal const string QualifiedKeycloakImage = QualifiedKeycloakRepository + "@" + QualifiedKeycloakDigest;

    /// <summary>The same qualified image with its release tag for Docker and theme references.</summary>
    internal const string TaggedQualifiedKeycloakImage = QualifiedKeycloakRepository + ":" + QualifiedKeycloakTag + "@" + QualifiedKeycloakDigest;

    /// <summary>Gets or sets the stable private lease root shared by sibling worktrees.</summary>
    public string LeaseRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppSurface", "Keycloak", "leases");
    /// <summary>Gets or sets an existing private parent for collision-resistant run directories.</summary>
    public string RunRoot { get; set; } = Path.Combine(Path.GetTempPath(), "appsurface-keycloak-runs");
    /// <summary>Gets or sets the exact per-user Docker Unix endpoint or <c>unix:///var/run/docker.sock</c>.</summary>
    public string DockerEndpoint { get; set; } = SupervisedDocker.GetUserSocketEndpoint();
    /// <summary>Gets or sets the pinned Linux ARM64 .NET image containing the worker's framework.</summary>
    public string RuntimeImage { get; set; } = "mcr.microsoft.com/dotnet/sdk@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b";
    /// <summary>Gets or sets the pinned Keycloak image qualified by this profile.</summary>
    public string KeycloakImage { get; set; } = QualifiedKeycloakImage;
    /// <summary>Gets or sets the maximum transport startup wait; defaults to 120 seconds.</summary>
    public TimeSpan TransportReadyTimeout { get; set; } = TimeSpan.FromSeconds(120);
    /// <summary>Gets or sets the deadline for each required consumer seed; defaults to 120 seconds.</summary>
    public TimeSpan LocalSeedTimeout { get; set; } = TimeSpan.FromSeconds(120);

    internal AppSurfaceKeycloakSupervisedOptions Snapshot()
    {
        if (!Path.IsPathFullyQualified(LeaseRoot) || !Path.IsPathFullyQualified(RunRoot)
            || new[] { LeaseRoot, RunRoot }.Any(p => p.Any(char.IsControl) || p.Contains(','))
            || !Pinned(RuntimeImage) || !Pinned(KeycloakImage)
            || !Valid(TransportReadyTimeout) || !Valid(LocalSeedTimeout))
            throw AppSurfaceKeycloakCredentialBindings.Invalid();
        return new()
        {
            LeaseRoot = Path.GetFullPath(LeaseRoot),
            RunRoot = Path.GetFullPath(RunRoot),
            DockerEndpoint = DockerEndpoint,
            RuntimeImage = RuntimeImage,
            KeycloakImage = KeycloakImage,
            TransportReadyTimeout = TransportReadyTimeout,
            LocalSeedTimeout = LocalSeedTimeout
        };
    }
    private static bool Valid(TimeSpan value) => value > TimeSpan.Zero && value <= TimeSpan.FromMinutes(10);
    private static bool Pinned(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)) return false;
        var split = value.Split("@sha256:", StringSplitOptions.None);
        return split.Length == 2 && split[0].Length > 0 && split[1].Length == 64 && split[1].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}

/// <summary>Describes a copied framework-dependent consumer payload executed inside the owned namespace.</summary>
/// <param name="Name">Unique lower-case resource name from 3 to 63 characters. Package-owned roles and generated container names are reserved.</param>
/// <param name="PayloadDirectory">Absolute output directory containing the complete portable publish closure.</param>
/// <param name="AssemblyName">Simple DLL filename in that directory.</param>
/// <param name="Arguments">Nonsecret arguments. Use only public metadata and private-input paths.</param>
/// <remarks>No checkout project is launched. The directory is copied after ownership acquisition. No secret may appear in arguments.</remarks>
public sealed record AppSurfaceKeycloakSupervisedWorkload(string Name, string PayloadDirectory, string AssemblyName,
    ImmutableArray<string> Arguments)
{
    /// <summary>Gets nonsecret environment settings; credentials belong in private typed bindings.</summary>
    public ImmutableDictionary<string, string> Environment { get; init; } = ImmutableDictionary<string, string>.Empty;
    /// <summary>Gets consumer-owned private JSON field to typed parameter bindings. Values are resolved only under the owner lease.</summary>
    public ImmutableDictionary<string, IResourceBuilder<ParameterResource>> PrivateInputs { get; init; } = ImmutableDictionary<string, IResourceBuilder<ParameterResource>>.Empty;
}

/// <summary>Returns the final process-level outcome after verified owned teardown.</summary>
/// <param name="ExitCode">Zero for clean cancellation/success, nonzero for required-gate or shutdown failure.</param>
/// <param name="Code">Safe stable diagnostic code.</param>
/// <param name="OwnerId">Opaque run owner identifier.</param>
/// <param name="ActiveRetained">Whether this run's active marker remains or its absence could not be verified under the lease.</param>
/// <param name="StopSeconds">Observed shared stop/verify/cleanup duration.</param>
public sealed record AppSurfaceKeycloakSupervisedResult(int ExitCode, string Code, string OwnerId, bool ActiveRetained, double StopSeconds);

/// <summary>Returns a verification-only recovery result; a successful result never continues ordinary startup.</summary>
/// <param name="Kind">RecoveryOnlyCompleted on success.</param>
/// <param name="WorkloadsStarted">Always false.</param>
/// <param name="Cleared">Whether only the verified expected active marker was cleared.</param>
public sealed record AppSurfaceKeycloakSupervisedRecoveryResult(string Kind, bool WorkloadsStarted, bool Cleared);
