using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Aspire;

namespace AspireAppHostExample;

/// <summary>
/// Applies local profile configuration to the example web application resource.
/// </summary>
/// <remarks>
/// This component demonstrates component composition by resolving
/// <see cref="WebAppProjectComponent"/> instead of creating the project resource
/// directly. It sets <c>APPSURFACE_ASPIRE_EXAMPLE=local</c> for the local AppHost
/// run, may replace an existing value with the same environment variable name, and exposes the project's HTTP endpoint
/// for readiness and client-based Aspire tests.
/// </remarks>
public sealed class WebAppEnvironmentComponent : IAspireComponent<ProjectResource>
{
    private readonly WebAppProjectComponent _webApp;

    /// <summary>
    /// Initializes a component that configures the shared web application project resource.
    /// </summary>
    /// <param name="webApp">The component that produces the example web project resource.</param>
    public WebAppEnvironmentComponent(WebAppProjectComponent webApp)
    {
        _webApp = webApp;
    }

    /// <summary>
    /// Resolves the web project component and adds the local example environment variable.
    /// </summary>
    /// <param name="context">The AppSurface Aspire startup context used to resolve component dependencies.</param>
    /// <param name="appBuilder">The Aspire distributed application builder for the AppHost.</param>
    /// <returns>The configured web project resource builder.</returns>
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder) =>
        context.Resolve(_webApp)
            .WithEnvironment("APPSURFACE_ASPIRE_EXAMPLE", "local")
            .WithHttpEndpoint();
}
