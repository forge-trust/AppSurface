using CliFx.Binding;
using ForgeTrust.AppSurface.Aspire;
using Microsoft.Extensions.Logging;

namespace AspireAppHostExample;

/// <summary>
/// Runs the local Aspire AppHost profile for the AppSurface example.
/// </summary>
/// <remarks>
/// The <c>local</c> command is selected after the Aspire CLI argument separator:
/// <c>aspire run --apphost examples/aspire-apphost/AspireAppHostExample.csproj -- local</c>.
/// The profile owns component composition only; deployment pass-through arguments
/// remain unsupported unless a profile explicitly overrides them.
/// </remarks>
[Command("local", Description = "Run the local AppSurface Aspire example.")]
public sealed partial class LocalProfile : AspireProfile
{
    private readonly WebAppEnvironmentComponent _webApp;

    /// <summary>
    /// Initializes a local Aspire profile with the composed web application component.
    /// </summary>
    /// <param name="webApp">The component that resolves and configures the web project resource.</param>
    /// <param name="logger">The logger used by the base Aspire profile command.</param>
    public LocalProfile(
        WebAppEnvironmentComponent webApp,
        ILogger<LocalProfile> logger) : base(logger)
    {
        _webApp = webApp;
    }

    /// <summary>
    /// Returns the components that this profile contributes to the Aspire application model.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="WebAppEnvironmentComponent"/> depends on
    /// <see cref="WebAppProjectComponent"/>, which lets AppSurface resolve the
    /// shared project resource before applying profile-specific configuration.
    /// </remarks>
    /// <returns>The local profile component sequence.</returns>
    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _webApp;
    }
}
