using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Aspire;

namespace AspireAppHostExample;

/// <summary>
/// Adds the example web application project to the Aspire application model.
/// </summary>
/// <remarks>
/// The component uses Aspire's generated <c>Projects.WebAppExample</c> type and
/// registers it with the resource name <c>web</c>. Resource names must remain
/// unique within the AppHost.
/// </remarks>
public sealed class WebAppProjectComponent : IAspireComponent<ProjectResource>
{
    /// <summary>
    /// Creates the example web project resource.
    /// </summary>
    /// <param name="context">The AppSurface Aspire startup context for the component graph.</param>
    /// <param name="appBuilder">The Aspire distributed application builder for the AppHost.</param>
    /// <returns>The web project resource builder registered as <c>web</c>.</returns>
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder) =>
        appBuilder.AddProject<Projects.WebAppExample>("web");
}
