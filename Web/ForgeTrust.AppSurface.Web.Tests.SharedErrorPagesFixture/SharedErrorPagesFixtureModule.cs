using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Web.Tests.SharedErrorPagesFixture;

public sealed class SharedErrorPagesFixtureModule : IAppSurfaceWebModule
{
    public bool IncludeAsApplicationPart => true;

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
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

    public void ConfigureWebOptions(StartupContext context, WebOptions options)
    {
    }

    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
    }
}
