using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Performs DevAuth startup validation after the host service provider has been built.
/// </summary>
/// <remarks>
/// Registration validates the caller-supplied environment early, but this hosted service re-checks the real
/// environment resolved from dependency injection and inspects the final authentication defaults and schemes. It rejects
/// environments outside the DevAuth activation allow-list and real authentication scheme conflicts unless the consumer
/// explicitly enabled the local-proof override. This service is internal infrastructure for
/// <see cref="AppSurfaceDevAuthServiceCollectionExtensions"/>.
/// </remarks>
internal sealed class AppSurfaceDevAuthStartupValidator : IHostedService
{
    private readonly IAuthenticationSchemeProvider _schemeProvider;
    private readonly IHostEnvironment _environment;
    private readonly IOptions<AuthenticationOptions> _authenticationOptions;
    private readonly IOptions<AppSurfaceDevAuthOptions> _devAuthOptions;

    /// <summary>
    /// Creates a validator over the final host authentication and environment services.
    /// </summary>
    /// <param name="schemeProvider">Authentication scheme provider used to inspect all registered schemes.</param>
    /// <param name="environment">Host environment resolved from dependency injection.</param>
    /// <param name="authenticationOptions">ASP.NET Core authentication defaults resolved from dependency injection.</param>
    /// <param name="devAuthOptions">Materialized DevAuth options registered by <c>AddAppSurfaceDevAuth</c>.</param>
    public AppSurfaceDevAuthStartupValidator(
        IAuthenticationSchemeProvider schemeProvider,
        IHostEnvironment environment,
        IOptions<AuthenticationOptions> authenticationOptions,
        IOptions<AppSurfaceDevAuthOptions> devAuthOptions)
    {
        ArgumentNullException.ThrowIfNull(schemeProvider);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(authenticationOptions);
        ArgumentNullException.ThrowIfNull(devAuthOptions);

        _schemeProvider = schemeProvider;
        _environment = environment;
        _authenticationOptions = authenticationOptions;
        _devAuthOptions = devAuthOptions;
    }

    /// <summary>
    /// Validates that DevAuth is running in an allowed environment and is not silently coexisting with real authentication.
    /// </summary>
    /// <param name="cancellationToken">Startup cancellation token; validation performs only in-memory inspection.</param>
    /// <returns>A completed task when the final host configuration is safe for local DevAuth.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when materialized DevAuth options contain blank required values or an invalid
    /// <see cref="AppSurfaceDevAuthOptions.AllowedEnvironmentNames"/> allow-list.
    /// </exception>
    /// <exception cref="AppSurfaceDevAuthException">
    /// Thrown with <c>ASDEV001</c> outside configured proof environments or with <c>ASDEV002</c> when real
    /// schemes/defaults are present without the explicit override.
    /// </exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _devAuthOptions.Value;
        AppSurfaceDevAuthServiceCollectionExtensions.ValidateOptions(options);

        if (!AppSurfaceDevAuthEnvironmentPolicy.IsEnvironmentAllowed(_environment, options))
        {
            throw AppSurfaceDevAuthServiceCollectionExtensions.CreateNonDevelopmentException(
                _environment.EnvironmentName,
                options);
        }

        if (options.AllowDevAuthOverrideForLocalProof)
        {
            return;
        }

        var schemes = await _schemeProvider.GetAllSchemesAsync();
        var realSchemes = schemes
            .Where(scheme => !string.Equals(scheme.Name, options.SchemeName, StringComparison.Ordinal))
            .Select(scheme => scheme.Name)
            .ToArray();

        var defaults = new[]
            {
                _authenticationOptions.Value.DefaultScheme,
                _authenticationOptions.Value.DefaultAuthenticateScheme,
                _authenticationOptions.Value.DefaultChallengeScheme,
                _authenticationOptions.Value.DefaultForbidScheme,
                _authenticationOptions.Value.DefaultSignInScheme,
                _authenticationOptions.Value.DefaultSignOutScheme,
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !string.Equals(value, options.SchemeName, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (realSchemes.Length > 0 || defaults.Length > 0)
        {
            var conflict = string.Join(", ", realSchemes.Concat(defaults).Distinct(StringComparer.Ordinal));
            throw new AppSurfaceDevAuthException(
                AppSurfaceDevAuthDiagnostics.RealSchemeConflict,
                $"ASDEV002 Problem: AppSurface DevAuth detected existing authentication scheme/default '{conflict}'. Cause: fake local auth must not silently replace real host auth. Fix: remove DevAuth from this host or set AllowDevAuthOverrideForLocalProof only for an intentional local proof. Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics.");
        }
    }

    /// <summary>
    /// Stops the validator.
    /// </summary>
    /// <param name="cancellationToken">Stop cancellation token.</param>
    /// <returns>A completed task because the validator does not own background work.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
