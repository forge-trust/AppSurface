using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

internal sealed class AppSurfaceDevAuthStartupValidator : IHostedService
{
    private readonly IAuthenticationSchemeProvider _schemeProvider;
    private readonly IHostEnvironment _environment;
    private readonly IOptions<AuthenticationOptions> _authenticationOptions;
    private readonly IOptions<AppSurfaceDevAuthOptions> _devAuthOptions;

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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            throw AppSurfaceDevAuthServiceCollectionExtensions.CreateNonDevelopmentException(_environment.EnvironmentName);
        }

        var options = _devAuthOptions.Value;
        AppSurfaceDevAuthServiceCollectionExtensions.ValidateOptions(options);

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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
