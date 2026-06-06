using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Aspire;

namespace AspireAppHostExample;

public sealed class WebAppEnvironmentComponent : IAspireComponent<ProjectResource>
{
    private readonly WebAppProjectComponent _webApp;

    public WebAppEnvironmentComponent(WebAppProjectComponent webApp)
    {
        _webApp = webApp;
    }

    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder) =>
        context.Resolve(_webApp)
            .WithEnvironment("APPSURFACE_ASPIRE_EXAMPLE", "local");
}
