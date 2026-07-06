using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Registers AppSurface development-only authentication for ASP.NET Core hosts.
/// </summary>
public static class AppSurfaceDevAuthServiceCollectionExtensions
{
    /// <summary>
    /// Adds the AppSurface DevAuth named authentication scheme and startup safety validation.
    /// </summary>
    /// <param name="services">Service collection that receives DevAuth registrations.</param>
    /// <param name="environment">Host environment used to enforce the DevAuth activation allow-list.</param>
    /// <param name="configure">Callback that configures seeded local personas and DevAuth options once during registration.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when required option values are blank or
    /// <see cref="AppSurfaceDevAuthOptions.AllowedEnvironmentNames"/> is empty or contains blank names.
    /// </exception>
    /// <exception cref="AppSurfaceDevAuthException">
    /// Thrown when DevAuth is enabled in an environment that is not allowed or another DevAuth safety diagnostic fails.
    /// </exception>
    /// <remarks>
    /// DevAuth is fake local authentication. It registers a normal ASP.NET Core authentication handler, but it must
    /// not be used as a production identity provider, user store, OIDC replacement, or durable app-user mapping layer.
    /// DevAuth activates only when the trimmed host environment name matches
    /// <see cref="AppSurfaceDevAuthOptions.AllowedEnvironmentNames"/> case-insensitively. The default allow-list contains
    /// <c>Development</c>; add proof environments only for intentional local/proof hosts.
    /// </remarks>
    public static IServiceCollection AddAppSurfaceDevAuth(
        this IServiceCollection services,
        IHostEnvironment environment,
        Action<AppSurfaceDevAuthOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configure);

        var devAuthOptions = new AppSurfaceDevAuthOptions();
        configure(devAuthOptions);
        ValidateOptions(devAuthOptions);

        if (!AppSurfaceDevAuthEnvironmentPolicy.IsEnvironmentAllowed(environment, devAuthOptions))
        {
            throw CreateNonDevelopmentException(environment.EnvironmentName, devAuthOptions);
        }

        services.TryAddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IOptions<AppSurfaceDevAuthOptions>>(Options.Create(devAuthOptions));
        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.AddHostedService<AppSurfaceDevAuthStartupValidator>();

        var authenticationBuilder = devAuthOptions.UseAsDefaultSchemeForLocalProof
            ? services.AddAuthentication(devAuthOptions.SchemeName)
            : services.AddAuthentication();

        authenticationBuilder.AddScheme<AuthenticationSchemeOptions, AppSurfaceDevAuthHandler>(
            devAuthOptions.SchemeName,
            options => { _ = options; });

        return services;
    }

    /// <summary>
    /// Creates the stable <see cref="AppSurfaceDevAuthDiagnostics.NonDevelopmentEnvironment"/> exception.
    /// </summary>
    /// <param name="environmentName">
    /// Host environment name used only in the safe diagnostic message. Blank values are rendered as <c>(unknown)</c>.
    /// </param>
    /// <param name="options">Materialized DevAuth options used to format the allowed environment names.</param>
    /// <returns>An exception that tells consumers to run DevAuth only in an allowed proof environment or remove it.</returns>
    internal static AppSurfaceDevAuthException CreateNonDevelopmentException(
        string environmentName,
        AppSurfaceDevAuthOptions options)
    {
        var safeEnvironment = string.IsNullOrWhiteSpace(environmentName) ? "(unknown)" : environmentName;
        var allowedEnvironments = AppSurfaceDevAuthEnvironmentPolicy.FormatAllowedEnvironmentNames(options);
        return new AppSurfaceDevAuthException(
            AppSurfaceDevAuthDiagnostics.NonDevelopmentEnvironment,
            $"ASDEV001 Problem: AppSurface DevAuth is not enabled for environment '{safeEnvironment}'. Cause: fake local authentication was enabled outside the configured DevAuth activation allow-list ({allowedEnvironments}). Fix: set DOTNET_ENVIRONMENT=Development, add the proof environment to AppSurfaceDevAuthOptions.AllowedEnvironmentNames, or remove AddAppSurfaceDevAuth from deployed hosts. Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics.");
    }

    /// <summary>
    /// Validates the materialized DevAuth options before endpoint mapping and startup safety checks run.
    /// </summary>
    /// <param name="options">Options instance populated once by the consumer registration callback.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="AppSurfaceDevAuthOptions.SchemeName"/>,
    /// <see cref="AppSurfaceDevAuthOptions.PathPrefix"/>, or
    /// <see cref="AppSurfaceDevAuthOptions.CookieName"/> is blank, or when
    /// <see cref="AppSurfaceDevAuthOptions.AllowedEnvironmentNames"/> is empty or contains blank names.
    /// </exception>
    /// <exception cref="AppSurfaceDevAuthException">
    /// Thrown with <c>ASDEV003</c> when no personas are configured, <c>ASDEV004</c> when a persona lacks its
    /// configured subject claim, or <c>ASDEV005</c> when the path prefix is not an absolute path without a trailing slash.
    /// </exception>
    internal static void ValidateOptions(AppSurfaceDevAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SchemeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PathPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CookieName);
        AppSurfaceDevAuthEnvironmentPolicy.ValidateAllowedEnvironmentNames(options);

        if (!options.PathPrefix.StartsWith("/", StringComparison.Ordinal) ||
            options.PathPrefix.EndsWith("/", StringComparison.Ordinal))
        {
            throw new AppSurfaceDevAuthException(
                AppSurfaceDevAuthDiagnostics.ReservedPathConflict,
                "ASDEV005 Problem: DevAuth path prefix must be an absolute path without a trailing slash. Cause: the configured path prefix is not safe to reserve. Fix: use a path such as '/_appsurface/dev-auth'. Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics.");
        }

        if (options.Users.Personas.Count == 0)
        {
            throw new AppSurfaceDevAuthException(
                AppSurfaceDevAuthDiagnostics.NoPersonas,
                "ASDEV003 Problem: AppSurface DevAuth requires at least one seeded persona. Cause: no users were added to AppSurfaceDevAuthOptions.Users. Fix: add a local persona such as 'admin' or remove DevAuth. Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics.");
        }

        foreach (var persona in options.Users.Personas.Values.Where(IsMissingSubjectClaim))
        {
            throw new AppSurfaceDevAuthException(
                AppSurfaceDevAuthDiagnostics.MissingSubjectClaim,
                $"ASDEV004 Problem: DevAuth persona '{persona.Id}' is missing subject claim '{persona.SubjectClaimType}'. Cause: AppSurface Auth.AspNetCore cannot map authenticated users without a stable subject. Fix: call Subject(...) and keep it aligned with MapSubjectClaim(...). Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics.");
        }
    }

    /// <summary>
    /// Determines whether a seeded persona is missing the non-blank subject claim required by Auth.AspNetCore mapping.
    /// </summary>
    /// <param name="persona">Persona to inspect after it has been built from the local seed callback.</param>
    /// <returns><see langword="true"/> when the persona lacks its configured subject claim.</returns>
    private static bool IsMissingSubjectClaim(AppSurfaceDevAuthPersona persona)
    {
        return !persona.Claims.Any(claim =>
            string.Equals(claim.Type, persona.SubjectClaimType, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(claim.Value));
    }
}
