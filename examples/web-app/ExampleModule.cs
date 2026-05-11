using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.AppSurface.Web.Scalar;

namespace WebAppExample;

public class ExampleModule : IAppSurfaceWebModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        // Register services for the application here if needed
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceWebScalarModule>();
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
    {
    }

    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/module", () => "Hello from the example module!");
    }
}
