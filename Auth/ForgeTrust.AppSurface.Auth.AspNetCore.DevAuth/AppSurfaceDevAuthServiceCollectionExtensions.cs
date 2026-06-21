using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
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
    /// <param name="environment">Host environment used to enforce Development-only startup.</param>
    /// <param name="configure">Callback that configures seeded local personas and DevAuth options.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="AppSurfaceDevAuthException">
    /// Thrown when DevAuth is enabled outside Development or the supplied options are invalid.
    /// </exception>
    /// <remarks>
    /// DevAuth is fake local authentication. It registers a normal ASP.NET Core authentication handler, but it must
    /// not be used as a production identity provider, user store, OIDC replacement, or durable app-user mapping layer.
    /// </remarks>
    public static IServiceCollection AddAppSurfaceDevAuth(
        this IServiceCollection services,
        IHostEnvironment environment,
        Action<AppSurfaceDevAuthOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configure);

        if (!environment.IsDevelopment())
        {
            throw CreateNonDevelopmentException(environment.EnvironmentName);
        }

        var preview = new AppSurfaceDevAuthOptions();
        configure(preview);
        ValidateOptions(preview);

        services.AddSingleton(environment);
        services.AddOptions<AppSurfaceDevAuthOptions>().Configure(configure);
        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.AddHostedService<AppSurfaceDevAuthStartupValidator>();

        var authenticationBuilder = preview.UseAsDefaultSchemeForLocalProof
            ? services.AddAuthentication(preview.SchemeName)
            : services.AddAuthentication();

        authenticationBuilder.AddScheme<AuthenticationSchemeOptions, AppSurfaceDevAuthHandler>(
            preview.SchemeName,
            options => { _ = options; });

        return services;
    }

    internal static AppSurfaceDevAuthException CreateNonDevelopmentException(string environmentName)
    {
        var safeEnvironment = string.IsNullOrWhiteSpace(environmentName) ? "(unknown)" : environmentName;
        return new AppSurfaceDevAuthException(
            AppSurfaceDevAuthDiagnostics.NonDevelopmentEnvironment,
            $"ASDEV001 Problem: AppSurface DevAuth cannot run in '{safeEnvironment}'. Cause: fake local authentication was enabled outside Development. Fix: set DOTNET_ENVIRONMENT=Development for local proof or remove AddAppSurfaceDevAuth from deployed hosts. Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics.");
    }

    internal static void ValidateOptions(AppSurfaceDevAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SchemeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PathPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CookieName);

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

    private static bool IsMissingSubjectClaim(AppSurfaceDevAuthPersona persona)
    {
        return !persona.Claims.Any(claim =>
            string.Equals(claim.Type, persona.SubjectClaimType, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(claim.Value));
    }
}
