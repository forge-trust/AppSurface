using Microsoft.AspNetCore.OutputCaching;

namespace ForgeTrust.RazorWire.Caching;

/// <summary>
/// Provides extension methods for configuring ASP.NET Core Output Caching with RazorWire policies.
/// </summary>
public static class RazorWireOutputCachingExtensions
{
    /// <summary>
    /// Adds two output caching policies for Razor Wire using names from <paramref name="rwOptions"/>:
    /// a page policy with 1-minute expiration and an island policy with 30-second expiration,
    /// both restricted to responses for unauthenticated users.
    /// </summary>
    /// <param name="options">The OutputCacheOptions instance to configure.</param>
    /// <param name="rwOptions">RazorWire configuration providing policy names.</param>
    /// <returns>The modified <see cref="OutputCacheOptions"/> instance.</returns>
    public static OutputCacheOptions AddRazorWirePolicies(this OutputCacheOptions options, RazorWireOptions rwOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rwOptions);
        ArgumentNullException.ThrowIfNull(rwOptions.Caching);
        options.AddPolicy(
            rwOptions.Caching.PagePolicyName,
            builder =>
            {
                builder.Expire(TimeSpan.FromMinutes(1));
                // Rule: authenticated or personalized content should not be cached unless explicitly configured.
                builder.With(context => context.HttpContext.User.Identity?.IsAuthenticated == false);
            });

        options.AddPolicy(
            rwOptions.Caching.IslandPolicyName,
            builder =>
            {
                builder.Expire(TimeSpan.FromSeconds(30));
                builder.With(context => context.HttpContext.User.Identity?.IsAuthenticated == false);
            });

        return options;
    }
}
