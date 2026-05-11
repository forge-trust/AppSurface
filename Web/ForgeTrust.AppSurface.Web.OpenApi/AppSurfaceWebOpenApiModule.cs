using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Web.OpenApi;

/// <summary>
/// A web module that integrates OpenAPI/Swagger document generation into the application.
/// </summary>
public class AppSurfaceWebOpenApiModule : IAppSurfaceWebModule
{
    /// <summary>
    /// Configures services needed for OpenAPI, including document and operation transformers to customize the generated schema.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="services">The service collection to populate.</param>
    public virtual void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((d, _, _) =>
            {
                // TODO: Update w/ real versioning strategy
                d.Info.Title = $"{context.ApplicationName} | v1";
                if (d.Tags is not null)
                {
                    d.Tags = d.Tags.Where(x => x.Name != "ForgeTrust.AppSurface.Web").ToList();
                }

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((op, ctx, _) =>
            {
                if (op.Tags is not null)
                {
                    op.Tags = op.Tags
                        .Where(x => x.Name != "ForgeTrust.AppSurface.Web")
                        .ToList();
                }

                return Task.CompletedTask;
            });
        });

        services.AddEndpointsApiExplorer();
    }

    /// <summary>
    /// Maps the OpenAPI document endpoint.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="endpoints">The endpoint route builder.</param>
    public virtual void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOpenApi();
    }

    /// <summary>
    /// Registers dependencies for this module; currently no implementation is required.
    /// </summary>
    /// <param name="builder">The module dependency builder.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    /// <summary>
    /// Executes pre-service host configuration; currently no implementation is required.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="builder">The host builder.</param>
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Executes post-service host configuration; currently no implementation is required.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="builder">The host builder.</param>
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Configures the web application pipeline; currently no implementation is required.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="app">The application builder.</param>
    public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
    {
    }
}
