using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Configures the explicit local-only policy, predecessor, and typed secret bindings for one consumer seed project.
/// </summary>
public sealed class AppSurfaceKeycloakLocalSeedOptions
{
    private readonly List<AppSurfaceKeycloakLocalSeedSecretBinding> _requiredSecretBindings = [];

    /// <summary>
    /// Gets local environment names in which seed registration is allowed, compared case-insensitively.
    /// </summary>
    /// <remarks>
    /// The default is <c>Development</c>, <c>Test</c>, and <c>Testing</c>. Publish and every execution operation
    /// other than Aspire <c>Run</c> are always denied, even when this list contains a deployment-like environment.
    /// </remarks>
    public IList<string> AllowedEnvironmentNames { get; } = ["Development", "Test", "Testing"];

    internal AppSurfaceKeycloakLocalSeed? Predecessor { get; private set; }

    internal IReadOnlyList<AppSurfaceKeycloakLocalSeedSecretBinding> RequiredSecretBindings => _requiredSecretBindings;

    /// <summary>
    /// Requires this seed to follow the immediately preceding local seed returned by the same Keycloak wrapper.
    /// </summary>
    /// <param name="predecessor">The immediately prior returned seed handle.</param>
    /// <returns>This options instance for fluent configuration.</returns>
    public AppSurfaceKeycloakLocalSeedOptions After(AppSurfaceKeycloakLocalSeed predecessor)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        Predecessor = predecessor;
        return this;
    }

    /// <summary>
    /// Binds one required Aspire secret parameter only to this seed's consumer project.
    /// </summary>
    /// <param name="environmentVariableName">The consumer-owned environment variable name.</param>
    /// <param name="parameter">A typed Aspire parameter resource whose <see cref="ParameterResource.Secret"/> flag is true.</param>
    /// <returns>This options instance for fluent configuration.</returns>
    /// <remarks>
    /// AppSurface validates parameter identity and secret metadata only; it never reads, logs, writes, serializes, or
    /// otherwise resolves the parameter value. The parameter may not be reused by a second seed in the same wrapper.
    /// </remarks>
    public AppSurfaceKeycloakLocalSeedOptions WithRequiredSecretParameter(
        string environmentVariableName,
        IResourceBuilder<ParameterResource> parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariableName);
        ArgumentNullException.ThrowIfNull(parameter);
        _requiredSecretBindings.Add(new AppSurfaceKeycloakLocalSeedSecretBinding(environmentVariableName, parameter));
        return this;
    }
}

/// <summary>
/// Stores a declared typed secret binding until the parent resource validates and applies it to the consumer project.
/// </summary>
internal sealed record AppSurfaceKeycloakLocalSeedSecretBinding(
    string EnvironmentVariableName,
    IResourceBuilder<ParameterResource> Parameter);
