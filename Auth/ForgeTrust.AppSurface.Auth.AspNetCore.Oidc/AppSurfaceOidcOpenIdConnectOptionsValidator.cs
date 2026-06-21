using System.Globalization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;

/// <summary>
/// Validates OpenID Connect handler options for the AppSurface-owned OIDC scheme.
/// </summary>
/// <remarks>
/// Validation is scoped to <see cref="AppSurfaceOidcAuthOptions.OidcScheme"/> so other host-owned OIDC schemes remain
/// untouched. The client-secret requirement follows <see cref="AppSurfaceOidcAuthOptions.RequireClientSecret"/>.
/// </remarks>
internal sealed class AppSurfaceOidcOpenIdConnectOptionsValidator : IValidateOptions<OpenIdConnectOptions>
{
    private readonly AppSurfaceOidcAuthOptions _options;

    /// <summary>
    /// Creates a validator for the configured AppSurface OIDC scheme.
    /// </summary>
    /// <param name="options">AppSurface OIDC registration options that define the scheme and validation policy.</param>
    public AppSurfaceOidcOpenIdConnectOptionsValidator(AppSurfaceOidcAuthOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Validates required OIDC handler fields for the matching AppSurface scheme.
    /// </summary>
    /// <param name="name">The scheme name currently being validated.</param>
    /// <param name="options">The ASP.NET Core OpenID Connect handler options.</param>
    /// <returns>
    /// <see cref="ValidateOptionsResult.Skip"/> for non-AppSurface schemes, success when authority, client id, and any
    /// required client secret are present, or a failure result with stable AppSurface diagnostic messages.
    /// </returns>
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

    /// <summary>
    /// Fails fast with <see cref="OptionsValidationException"/> when the AppSurface OIDC handler is invalid.
    /// </summary>
    /// <param name="name">The scheme name to validate.</param>
    /// <param name="options">The ASP.NET Core OpenID Connect handler options.</param>
    /// <param name="appSurfaceOptions">AppSurface OIDC registration options that define the validation policy.</param>
    /// <remarks>
    /// This helper is intended for post-configuration paths that need the same validation semantics as
    /// <see cref="Validate(string?, OpenIdConnectOptions)"/>.
    /// </remarks>
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
            CultureInfo.InvariantCulture,
            $"Problem: AppSurface OIDC option {optionName} is missing. Cause: the named OpenID Connect handler was configured without {optionName}. Fix: configure {optionName} through ConfigureOpenIdConnect(...). Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.Oidc/README.md. Code: {code}.");
    }
}
