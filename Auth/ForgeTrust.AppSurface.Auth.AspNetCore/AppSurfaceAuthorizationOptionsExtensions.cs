using Microsoft.AspNetCore.Authorization;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Adds AppSurface naming helpers for host-owned ASP.NET Core authorization policies.
/// </summary>
public static class AppSurfaceAuthorizationOptionsExtensions
{
    /// <summary>
    /// Registers a host-owned ASP.NET Core authorization policy that AppSurface endpoint helpers can evaluate.
    /// </summary>
    /// <param name="options">ASP.NET Core authorization options that receive the policy.</param>
    /// <param name="policyName">The non-blank policy name.</param>
    /// <param name="configurePolicy">Callback that configures the normal ASP.NET Core authorization policy.</param>
    /// <returns>The same authorization options for chaining.</returns>
    /// <remarks>
    /// This method delegates to ASP.NET Core <see cref="AuthorizationOptions.AddPolicy(string, Action{AuthorizationPolicyBuilder})" />.
    /// AppSurface does not define a parallel policy DSL or own permission truth.
    /// </remarks>
    public static AuthorizationOptions AddAppSurfacePolicy(
        this AuthorizationOptions options,
        string policyName,
        Action<AuthorizationPolicyBuilder> configurePolicy)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentNullException.ThrowIfNull(configurePolicy);

        options.AddPolicy(policyName, configurePolicy);
        return options;
    }
}
