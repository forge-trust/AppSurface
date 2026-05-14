using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.RazorWire.Caching;
using ForgeTrust.RazorWire.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire;

/// <summary>
/// A web module that integrates RazorWire real-time streaming and output caching into the application.
/// </summary>
public class RazorWireWebModule : IAppSurfaceWebModule
{
    /// <summary>
    /// Ensures the application's MVC support level is at least ControllersWithViews.
    /// </summary>
    /// <param name="context">The startup context for the web module.</param>
    /// <param name="options">Web options to configure; may be modified to raise Mvc.MvcSupportLevel to ControllersWithViews if it is lower.</param>
    public void ConfigureWebOptions(StartupContext context, WebOptions options)
    {
        var needsRuntimeCompilation = context.IsDevelopment;
        var needsMvcUpgrade = options.Mvc.MvcSupportLevel < MvcSupport.ControllersWithViews;
        Action<IMvcBuilder> configureRazorWireMvc = builder =>
        {
            if (needsRuntimeCompilation)
            {
                builder.AddRazorRuntimeCompilation();
            }

            builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(mvcOptions =>
            {
                mvcOptions.Filters.AddService<RazorWireAntiforgeryFailureFilter>(order: int.MaxValue - 100);
            });
        };

        if (needsRuntimeCompilation || needsMvcUpgrade || options.Mvc.ConfigureMvc is not null)
        {
            // Even if only 'needsRuntimeCompilation' is true, we recreate the options record
            // to pass both flags. This simplifies the logic by handling all upgrades in one place.
            options.Mvc = options.Mvc with
            {
                MvcSupportLevel = needsMvcUpgrade ? MvcSupport.ControllersWithViews : options.Mvc.MvcSupportLevel,
                ConfigureMvc = options.Mvc.ConfigureMvc + configureRazorWireMvc
            };
        }
        else
        {
            options.Mvc = options.Mvc with
            {
                ConfigureMvc = configureRazorWireMvc
            };
        }
    }

    /// <summary>
    /// Gets a value indicating whether this module's assembly should be searched for MVC application parts.
    /// Returns <c>true</c> for RazorWire to enable its tag helpers and other components.
    /// </summary>
    public bool IncludeAsApplicationPart => true;

    /// <summary>
    /// Registers RazorWire services, enables output caching, and configures output cache options to include RazorWire policies.
    /// </summary>
    /// <param name="context">The startup context for the current module initialization.</param>
    /// <param name="services">The service collection to which RazorWire, output caching, and related options are added.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddRazorWire();
        services.AddOutputCache();

        services.AddOptions<OutputCacheOptions>()
            .PostConfigure<RazorWireOptions>((options, rwOptions) => { options.AddRazorWirePolicies(rwOptions); });
    }

    /// <summary>
    /// Registers this module's dependencies with the provided dependency builder.
    /// </summary>
    /// <param name="builder">The dependency builder used to declare other modules this module requires.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        // RazorWire depends on the core web functionality
    }

    /// <summary>
    /// Executes module-specific host configuration before application services are registered.
    /// </summary>
    /// <param name="context">The startup context providing environment and configuration for module initialization.</param>
    /// <param name="builder">The host builder to apply pre-service host configuration to.</param>
    /// <remarks>The default implementation does nothing.</remarks>
    public void ConfigureHostBeforeServices(StartupContext context, Microsoft.Extensions.Hosting.IHostBuilder builder)
    {
    }

    /// <summary>
    /// Provides a hook to modify the host builder after services have been registered.
    /// </summary>
    /// <param name="context">Startup context containing environment and module information.</param>
    /// <param name="builder">The <see cref="Microsoft.Extensions.Hosting.IHostBuilder"/> to configure.</param>
    public void ConfigureHostAfterServices(StartupContext context, Microsoft.Extensions.Hosting.IHostBuilder builder)
    {
    }

    /// <summary>
    /// Enables output caching in the application's request pipeline.
    /// </summary>
    /// <param name="context">Startup context providing environment and configuration for module initialization.</param>
    /// <param name="app">Application builder used to configure the HTTP request pipeline.</param>
    public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
    {
#if DEBUG
        // Only map source files for hot reload when the library itself is compiled in DEBUG mode.
        // This prevents Release builds from attempting to serve source files even if the consuming app is in Development.
        ConfigureDevelopmentStaticFiles(context, app);
#endif

        app.UseOutputCache();
    }

    private static void ConfigureDevelopmentStaticFiles(StartupContext context, IApplicationBuilder app)
    {
        if (context.IsDevelopment)
        {
            var env = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
            var libraryWebRoot = Path.GetFullPath(
                Path.Combine(env.ContentRootPath, "..", "..", "Web", "ForgeTrust.RazorWire", "wwwroot"));

            if (Directory.Exists(libraryWebRoot))
            {
                app.UseStaticFiles(
                    new StaticFileOptions
                    {
                        FileProvider = new PhysicalFileProvider(libraryWebRoot),
                        RequestPath = "/_content/ForgeTrust.RazorWire"
                    });
            }
            else
            {
                var logger = app.ApplicationServices.GetService<ILogger<RazorWireWebModule>>();
                logger?.LogDebug(
                    "RazorWire development static assets directory not found at: {LibraryWebRoot}",
                    libraryWebRoot);
            }
        }
    }

    /// <summary>
    /// Maps RazorWire HTTP endpoints into the application's endpoint route builder.
    /// </summary>
    /// <param name="context">The startup context providing environment and configuration for module initialization.</param>
    /// <param name="endpoints">The endpoint route builder to which RazorWire routes will be added.</param>
    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        endpoints.MapRazorWire();
    }
}
