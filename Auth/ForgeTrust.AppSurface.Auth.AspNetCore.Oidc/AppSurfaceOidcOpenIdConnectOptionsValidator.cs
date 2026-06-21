using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;

internal sealed class AppSurfaceOidcOpenIdConnectOptionsValidator : IValidateOptions<OpenIdConnectOptions>
{
    private readonly AppSurfaceOidcAuthOptions _options;

    public AppSurfaceOidcOpenIdConnectOptionsValidator(AppSurfaceOidcAuthOptions options)
    {
        _options = options;
    }

    public ValidateOptionsResult Validate(string? name, OpenIdConnectOptions options)
    {
        if (!string.Equals(name, _options.OidcScheme, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Authority))
        {
            failures.Add(Format(AppSurfaceOidcAuthDiagnosticCodes.MissingAuthority, nameof(options.Authority)));
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            failures.Add(Format(AppSurfaceOidcAuthDiagnosticCodes.MissingClientId, nameof(options.ClientId)));
        }

        if (_options.RequireClientSecret && string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            failures.Add(Format(AppSurfaceOidcAuthDiagnosticCodes.MissingClientSecret, nameof(options.ClientSecret)));
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    public static void ThrowIfInvalid(string name, OpenIdConnectOptions options, AppSurfaceOidcAuthOptions appSurfaceOptions)
    {
        var result = new AppSurfaceOidcOpenIdConnectOptionsValidator(appSurfaceOptions).Validate(name, options);
        if (result.Failed)
        {
            throw new OptionsValidationException(name, typeof(OpenIdConnectOptions), result.Failures);
        }
    }

    private static string Format(string code, string optionName)
    {
        return string.Create(
            null,
            $"Problem: AppSurface OIDC option {optionName} is missing. Cause: the named OpenID Connect handler was configured without {optionName}. Fix: configure {optionName} through ConfigureOpenIdConnect(...). Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.Oidc/README.md. Code: {code}.");
    }
}
