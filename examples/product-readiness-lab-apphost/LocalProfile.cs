using CliFx.Binding;
using ForgeTrust.AppSurface.Aspire;
using Microsoft.Extensions.Logging;

namespace ProductReadinessLabAppHost;

/// <summary>
/// Runs the product-readiness lab with local Aspire resources.
/// </summary>
[Command("local", Description = "Run the AppSurface product-readiness lab with local Postgres.")]
public sealed partial class LocalProfile : AspireProfile
{
    private readonly ProductReadinessWebComponent _web;

    /// <summary>
    /// Creates the local profile.
    /// </summary>
    /// <param name="web">Configured web component.</param>
    /// <param name="logger">Logger for the profile.</param>
    public LocalProfile(ProductReadinessWebComponent web, ILogger<LocalProfile> logger)
        : base(logger)
    {
        _web = web;
    }

    /// <inheritdoc />
    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _web;
    }
}
