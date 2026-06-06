using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Aspire;

namespace AspireAppHostExample;

public sealed class WebAppProjectComponent : IAspireComponent<ProjectResource>
{
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder) =>
        appBuilder.AddProject<Projects.WebAppExample>("web");
}
