using CliFx.Binding;
using ForgeTrust.AppSurface.Aspire;
using Microsoft.Extensions.Logging;

namespace AspireAppHostExample;

/// <summary>
/// Builds the example's one-project graph for deterministic Aspire integration tests.
/// </summary>
/// <remarks>
/// The profile intentionally uses constructor-injected components and no CliFx-bound members, so
/// <c>ForgeTrust.AppSurface.Aspire.Testing</c> can select it by type without running command dispatch.
/// </remarks>
[Command("qa", Description = "Build the deterministic AppSurface Aspire QA graph.")]
public sealed partial class QaProfile : AspireProfile
{
    private readonly WebAppEnvironmentComponent _webApp;

    /// <summary>
    /// Initializes the QA profile.
    /// </summary>
    /// <param name="webApp">The configured web project component.</param>
    /// <param name="logger">The logger used for runtime build and run failures.</param>
    public QaProfile(WebAppEnvironmentComponent webApp, ILogger<QaProfile> logger)
        : base(logger)
    {
        _webApp = webApp;
    }

    /// <summary>
    /// Returns the one-project web graph used by package-consumer tests.
    /// </summary>
    /// <returns>The QA profile component sequence.</returns>
    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _webApp;
    }
}
