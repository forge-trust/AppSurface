using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Validates local seed registration before a consumer project can be launched.
/// </summary>
internal static partial class AppSurfaceKeycloakLocalSeedPolicy
{
    private static readonly Regex EnvironmentVariableNamePattern = CreateEnvironmentVariableNamePattern();
    private static readonly Regex NamePattern = CreateNamePattern();

    internal static void EnsureRunOperation(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        EnsureRunOperation(builder.ExecutionContext.Operation);
    }

    internal static void EnsureRunOperation(DistributedApplicationOperation operation)
    {
        if (operation != DistributedApplicationOperation.Run)
        {
            throw NotAllowed();
        }
    }

    internal static void EnsureAllowedEnvironment(IDistributedApplicationBuilder builder, IEnumerable<string> allowedEnvironmentNames)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(allowedEnvironmentNames);
        EnsureAllowedEnvironment(builder.Environment.EnvironmentName, allowedEnvironmentNames);
    }

    internal static void EnsureAllowedEnvironment(string? environmentName, IEnumerable<string> allowedEnvironmentNames)
    {
        ArgumentNullException.ThrowIfNull(allowedEnvironmentNames);
        if (string.IsNullOrWhiteSpace(environmentName)
            || !allowedEnvironmentNames.Any(name =>
                !string.IsNullOrWhiteSpace(name)
                && string.Equals(name, environmentName, StringComparison.OrdinalIgnoreCase)))
        {
            throw NotAllowed();
        }
    }

    internal static void ValidateRegistration(
        string name,
        AppSurfaceKeycloakLocalSeedOptions options,
        IDistributedApplicationBuilder applicationBuilder,
        IReadOnlyList<AppSurfaceKeycloakLocalSeed> seeds,
        IReadOnlySet<ParameterResource> usedParameters)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(applicationBuilder);
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(usedParameters);
        if (!NamePattern.IsMatch(name)
            || seeds.Any(seed => string.Equals(seed.Name, name, StringComparison.Ordinal)))
        {
            throw Invalid();
        }

        var predecessor = options.Predecessor;
        if (seeds.Count == 0)
        {
            if (predecessor is not null)
            {
                throw Invalid();
            }
        }
        else if (predecessor is null
            || !ReferenceEquals(predecessor.Owner, seeds[0].Owner)
            || !ReferenceEquals(predecessor, seeds[^1]))
        {
            throw Invalid();
        }

        var environmentVariables = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new HashSet<ParameterResource>(ReferenceEqualityComparer.Instance);
        foreach (var binding in options.RequiredSecretBindings)
        {
            if (!EnvironmentVariableNamePattern.IsMatch(binding.EnvironmentVariableName)
                || !ReferenceEquals(binding.Parameter.ApplicationBuilder, applicationBuilder)
                || !binding.Parameter.Resource.Secret
                || !environmentVariables.Add(binding.EnvironmentVariableName)
                || !parameters.Add(binding.Parameter.Resource)
                || usedParameters.Contains(binding.Parameter.Resource))
            {
                throw Invalid();
            }
        }
    }

    internal static AppSurfaceKeycloakException Invalid() =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.LocalSeedInvalid,
            $"Problem: AppSurface Keycloak local seed registration is invalid. Cause: the seed name, predecessor, consumer project, or typed secret binding does not satisfy the local seed contract. Fix: use one unique finite project per stage, name the immediate predecessor, and bind each required Aspire secret parameter exactly once. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.LocalSeedInvalid}.");

    private static AppSurfaceKeycloakException NotAllowed() =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.LocalSeedNotAllowed,
            $"Problem: AppSurface Keycloak local seed registration is not allowed in this AppHost execution. Cause: local seeds require Aspire Run and an explicitly allowed local environment. Fix: register the seed only from Development, Test, or Testing local execution. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.LocalSeedNotAllowed}.");

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex CreateEnvironmentVariableNamePattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{2,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex CreateNamePattern();
}
