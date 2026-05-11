using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Provides a base implementation for a web-based <see cref="AppSurfaceStartup{TModule}"/> that handles MVC, CORS, and static file configuration based on registered <see cref="IAppSurfaceWebModule"/> instances.
/// </summary>
/// <typeparam name="TModule">The root <see cref="IAppSurfaceWebModule"/> for the application.</typeparam>
public abstract class WebStartup<TModule> : AppSurfaceStartup<TModule>
    where TModule : IAppSurfaceWebModule, new()
{
    private Action<WebOptions>? _configureOptions;
    private WebOptions _options = WebOptions.Default;
    private bool _modulesBuilt;
    private bool _optionsBuilt;
    private readonly List<IAppSurfaceWebModule> _modules = new();

    /// <summary>
    /// Registers an optional callback to customize WebOptions and enables fluent chaining.
    /// </summary>
    /// <param name="configureOptions">An optional action invoked later when WebOptions are built to modify configuration.</param>
    /// <returns>The same <see cref="WebStartup{TModule}"/> instance to support fluent configuration.</returns>
    public WebStartup<TModule> WithOptions(Action<WebOptions>? configureOptions = null)
    {
        _configureOptions = configureOptions;

        return this;
    }

    /// <summary>
    /// Starts the web host with AppSurface Web's deterministic development-port fallback when the caller has not
    /// explicitly configured an endpoint through command-line arguments, environment variables, or appsettings.
    /// </summary>
    /// <param name="args">The command-line arguments supplied by the caller.</param>
    /// <returns>A task that completes when the web host run exits.</returns>
    public new Task RunAsync(string[] args)
    {
        var resolution = ResolveDevelopmentPortDefaults(args);

        if (resolution.AppliedPort is not null)
        {
            GetStartupLogger()
                .LogInformation(
                    "No explicit development web endpoint was configured. Defaulting to deterministic localhost port {Port} for '{SeedPath}'. Override with --port, --urls, ASPNETCORE_URLS, or ASPNETCORE_HTTP_PORTS.",
                    resolution.AppliedPort.Value,
                    resolution.SeedPath);
        }

        return RunResolvedAsync(resolution.Args);
    }

    /// <summary>
    /// Resolves the effective command-line arguments before the web host starts.
    /// </summary>
    /// <param name="args">The command-line arguments supplied by the caller.</param>
    /// <returns>The resolved startup arguments and any deterministic development-port metadata.</returns>
    internal virtual AppSurfaceWebDevelopmentPortResolution ResolveDevelopmentPortDefaults(string[] args)
    {
        return AppSurfaceWebDevelopmentPortDefaults.Resolve(
            args,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable,
            Environment
                .GetEnvironmentVariables()
                .Keys
                .Cast<string>());
    }

    /// <summary>
    /// Runs the base host startup path with arguments after AppSurface Web development defaults have been resolved.
    /// </summary>
    /// <param name="args">The effective command-line arguments to pass into the host.</param>
    /// <returns>A task that completes when the web host run exits.</returns>
    internal virtual Task RunResolvedAsync(string[] args)
    {
        return base.RunAsync(args);
    }

    /// <summary>
    /// Collects and caches all IAppSurfaceWebModule instances found in the provided startup context. This method is idempotent.
    /// </summary>
    /// <param name="context">The startup context whose dependencies and root module are inspected for web modules.</param>
    /// <remarks>This method is idempotent; subsequent calls have no effect once modules are built.</remarks>
    private void BuildModules(StartupContext context)
    {
        if (_modulesBuilt)
        {
            return;
        }

        _modules.Clear();
        foreach (var dep in context.GetDependencies())
        {
            if (dep is IAppSurfaceWebModule webModule)
            {
                _modules.Add(webModule);
            }
        }

        if (context.RootModule is IAppSurfaceWebModule root)
        {
            _modules.Add(root);
        }

        _modulesBuilt = true;
    }

    /// <summary>
    /// Initializes and caches WebOptions by applying configuration from discovered modules and the optional custom callback; enables static file support when MVC is configured for controllers with views.
    /// </summary>
    /// <param name="context">The startup context used when invoking module and custom option configuration.</param>
    /// <remarks>This method is idempotent; subsequent calls have no effect once options are built.</remarks>
    private void BuildWebOptions(StartupContext context)
    {
        if (_optionsBuilt)
        {
            return;
        }

        _options = new();

        foreach (var module in _modules)
        {
            module.ConfigureWebOptions(context, _options);
        }

        _configureOptions?.Invoke(_options);

        if ((_options.Errors.BrowserStatusPageMode == BrowserStatusPageMode.Enabled
                || _options.Errors.ConventionalExceptionPageEnabled)
            && _options.Mvc.MvcSupportLevel < MvcSupport.ControllersWithViews)
        {
            _options.Mvc = _options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews };
        }

        if (_options.Mvc.MvcSupportLevel >= MvcSupport.ControllersWithViews)
        {
            _options.StaticFiles.EnableStaticFiles = true;
        }

        _optionsBuilt = true;
    }

    /// <summary>
    /// Configures services required for the web application: registers MVC application parts from the entry assembly and enabled web modules, and adds a CORS policy when CORS is enabled.
    /// </summary>
    /// <param name="context">Startup context providing environment information and the entry-point assembly.</param>
    /// <param name="services">The service collection to register MVC and CORS services into.</param>
    /// <exception cref="InvalidOperationException">Thrown when CORS is enabled but no allowed origins are specified, except when all origins are explicitly allowed in development.</exception>
    protected sealed override void ConfigureServicesForAppType(StartupContext context, IServiceCollection services)
    {
        BuildModules(context);
        BuildWebOptions(context);

        var mvcOpts = _options.Mvc;

        if (mvcOpts.MvcSupportLevel > MvcSupport.None)
        {
            var mvcBuilder = mvcOpts.MvcSupportLevel switch
            {
                MvcSupport.Controllers => services.AddControllers(),
                MvcSupport.ControllersWithViews => services.AddControllersWithViews(),
                _ => services.AddMvc(),
            };

            // Register the entry point assembly.
            mvcBuilder.AddApplicationPart(context.EntryPointAssembly);

            // Register all web module assemblies marked for discovery so MVC can find their controllers and views.
            // We use Distinct() to avoid redundant registrations from multiple modules in the same assembly.
            var moduleAssemblies = _modules
                .Where(m => m.IncludeAsApplicationPart)
                .Select(m => m.GetType().Assembly)
                .Distinct();

            foreach (var assembly in moduleAssemblies)
            {
                if (assembly != context.EntryPointAssembly)
                {
                    mvcBuilder.AddApplicationPart(assembly);
                }
            }

            if (_options.Errors.AreConventionalBrowserStatusPagesEnabled(mvcOpts.MvcSupportLevel)
                || _options.Errors.ConventionalExceptionPageEnabled)
            {
                var frameworkAssembly = typeof(WebOptions).Assembly;
                if (frameworkAssembly != context.EntryPointAssembly)
                {
                    mvcBuilder.AddApplicationPart(frameworkAssembly);
                }
            }

            if (_options.Errors.AreConventionalBrowserStatusPagesEnabled(mvcOpts.MvcSupportLevel))
            {
                services.TryAddSingleton<BrowserStatusPageRenderer>();
            }

            if (_options.Errors.ConventionalExceptionPageEnabled)
            {
                services.TryAddSingleton<ConventionalExceptionPageRenderer>();
            }

            mvcOpts.ConfigureMvc?.Invoke(mvcBuilder);
        }

        if (_options.Cors.EnableCors
            || (context.IsDevelopment && _options.Cors.EnableAllOriginsInDevelopment))
        {
            // Enforce that origins are specified if CORS is enabled, except when allowing all in development
            if (_options.Cors is { EnableCors: true, AllowedOrigins.Length: 0 }
                && !(_options.Cors.EnableAllOriginsInDevelopment && context.IsDevelopment))
            {
                throw new InvalidOperationException(
                    "CORS is enabled but AllowedOrigins is empty. To prevent security surprises, you must specify allowed origins or rely on 'EnableAllOriginsInDevelopment' only during development.");
            }

            // The user has configured CORS options, so we need to add CORS services
            services.AddCors(o =>
                o.AddPolicy(
                    _options.Cors.PolicyName,
                    builder =>
                    {
                        // If we have all origins enabled in development, allow all origins
                        if (_options.Cors.EnableAllOriginsInDevelopment && context.IsDevelopment)
                        {
                            builder.AllowAnyOrigin();
                        }
                        // If we have specific origins defined, use them
                        else if (_options.Cors.AllowedOrigins.Length > 0)
                        {
                            if (_options.Cors.AllowedOrigins.Contains("*"))
                            {
                                if (!context.IsDevelopment)
                                {
                                    // Log a warning if wildcard is used in production
                                    GetStartupLogger()
                                        .LogWarning(
                                            "CORS is enabled with a wildcard origin ('*'). It is recommended to set specific AllowedOrigins for production environments.");
                                }

                                builder.AllowAnyOrigin();
                            }
                            else
                            {
                                builder.SetIsOriginAllowedToAllowWildcardSubdomains()
                                    .WithOrigins(_options.Cors.AllowedOrigins)
                                    .AllowCredentials();
                            }
                        }

                        //TODO: Make this configurable
                        builder.AllowAnyHeader()
                            .AllowAnyMethod();
                    }));
        }
    }


    /// <summary>
    /// Configures the provided host builder with web host defaults and registers the application's web initialization pipeline.
    /// </summary>
    /// <param name="context">The startup context used to collect modules and build web options.</param>
    /// <param name="builder">The host builder to configure.</param>
    /// <returns>The same <see cref="IHostBuilder"/> configured with web host defaults and the application's initialization pipeline.</returns>
    protected override IHostBuilder ConfigureBuilderForAppType(StartupContext context, IHostBuilder builder)
    {
        BuildModules(context);
        BuildWebOptions(context);

        return builder.ConfigureWebHostDefaults(webBuilder =>
        {
            if (_options.StaticFiles.EnableStaticWebAssets)
            {
                webBuilder.UseStaticWebAssets();
            }

            webBuilder.Configure(app => { InitializeWebApplication(context, app); });
        });
    }

    /// <summary>
    /// Configures the application's middleware pipeline and endpoint routing for the web application.
    /// </summary>
    /// <param name="context">The startup context containing environment, entry point, and discovered modules used during configuration.</param>
    /// <param name="app">The application builder to configure (middleware, routing, CORS, endpoints, etc.).</param>
    private void InitializeWebApplication(StartupContext context, IApplicationBuilder app)
    {
        if (_options.Errors.ConventionalExceptionPageEnabled && !context.IsDevelopment)
        {
            app.ApplicationServices
                .GetRequiredService<ConventionalExceptionPageRenderer>()
                .ValidateConfiguredViews();

            app.UseExceptionHandler(exceptionHandlerApp =>
            {
                exceptionHandlerApp.Run(
                    async httpContext =>
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

                        if (!ShouldApplyConventionalExceptionPage(httpContext))
                        {
                            return;
                        }

                        await httpContext.RequestServices
                            .GetRequiredService<ConventionalExceptionPageRenderer>()
                            .RenderAsync(httpContext);
                    });
            });
        }

        if (_options.Errors.AreConventionalBrowserStatusPagesEnabled(_options.Mvc.MvcSupportLevel))
        {
            app.ApplicationServices
                .GetRequiredService<BrowserStatusPageRenderer>()
                .ValidateConfiguredViews();

            app.UseWhen(
                ShouldApplyConventionalBrowserStatusPages,
                branch =>
                {
                    branch.UseStatusCodePages(
                        async statusCodeContext =>
                        {
                            if (!BrowserStatusPageDescriptor.TryGet(
                                    statusCodeContext.HttpContext.Response.StatusCode,
                                    out var descriptor))
                            {
                                return;
                            }

                            await ReExecuteConventionalBrowserStatusPageAsync(statusCodeContext, descriptor);
                        });
                });
        }

        foreach (var module in _modules)
        {
            module.ConfigureWebApplication(context, app);
        }

        if (_options.StaticFiles.EnableStaticFiles)
        {
            app.UseStaticFiles();
        }

        app.UseRouting();

        if (_options.Cors.EnableCors
            || (context.IsDevelopment && _options.Cors.EnableAllOriginsInDevelopment))
        {
            app.UseCors(_options.Cors.PolicyName);
        }

        app.UseEndpoints(endpoints =>
        {
            // Map endpoints from dependencies.
            foreach (var module in _modules)
            {
                module.ConfigureEndpoints(context, endpoints);
            }

            // Map direct endpoints, if provided.
            _options.MapEndpoints?.Invoke(endpoints);

            if (_options.Mvc.MvcSupportLevel > MvcSupport.None)
            {
                if (_options.Errors.AreConventionalBrowserStatusPagesEnabled(_options.Mvc.MvcSupportLevel))
                {
                    endpoints.MapMethods(
                        BrowserStatusPageDefaults.ReservedRoutePattern,
                        [HttpMethods.Get, HttpMethods.Head],
                        async httpContext =>
                        {
                            var statusCode = GetReservedRouteStatusCode(httpContext);
                            var isDirectRequest = httpContext.Features.Get<IStatusCodeReExecuteFeature>() is null;

                            if (!statusCode.HasValue
                                || !BrowserStatusPageDescriptor.TryGet(statusCode.Value, out _))
                            {
                                if (isDirectRequest)
                                {
                                    httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                                }

                                return;
                            }

                            await httpContext.RequestServices
                                .GetRequiredService<BrowserStatusPageRenderer>()
                                .RenderAsync(httpContext);
                        });
                }

                endpoints.MapControllers();
            }
        });
    }

    internal static bool ShouldApplyConventionalBrowserStatusPages(HttpContext httpContext)
    {
        if (!HttpMethods.IsGet(httpContext.Request.Method) && !HttpMethods.IsHead(httpContext.Request.Method))
        {
            return false;
        }

        if (httpContext.Request.Path.StartsWithSegments(BrowserStatusPageDefaults.ReservedRouteBase))
        {
            return false;
        }

        return RequestAcceptsHtml(httpContext);
    }

    internal static bool ShouldApplyConventionalExceptionPage(HttpContext httpContext)
    {
        return RequestAcceptsHtml(httpContext);
    }

    private static bool RequestAcceptsHtml(HttpContext httpContext)
    {
        return httpContext.Request.GetTypedHeaders().Accept?
            .Any(mediaType =>
                mediaType.MediaType.HasValue
                && (mediaType.MediaType.Value.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                    || mediaType.MediaType.Value.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
            ?? false;
    }

    private static async Task ReExecuteConventionalBrowserStatusPageAsync(
        StatusCodeContext statusCodeContext,
        BrowserStatusPageDescriptor descriptor)
    {
        var httpContext = statusCodeContext.HttpContext;
        var originalPath = httpContext.Request.Path;
        var originalQueryString = httpContext.Request.QueryString;
        var originalEndpoint = httpContext.GetEndpoint();
        var originalRouteValues = new RouteValueDictionary(httpContext.Request.RouteValues);
        var originalReExecuteFeature = httpContext.Features.Get<IStatusCodeReExecuteFeature>();
        var statusCodePagesFeature = httpContext.Features.Get<IStatusCodePagesFeature>();
        var originalStatusCodePagesEnabled = statusCodePagesFeature?.Enabled;

        httpContext.Features.Set<IStatusCodeReExecuteFeature>(
            new BrowserStatusPageReExecuteFeature(
                httpContext.Request.PathBase.Value ?? string.Empty,
                originalPath.Value ?? string.Empty,
                originalQueryString.Value ?? string.Empty,
                httpContext.Response.StatusCode,
                originalEndpoint,
                new RouteValueDictionary(originalRouteValues)));

        if (statusCodePagesFeature is not null)
        {
            statusCodePagesFeature.Enabled = false;
        }

        httpContext.Request.Path = new PathString(descriptor.ReservedRoute);
        httpContext.Request.QueryString = QueryString.Empty;
        httpContext.Request.RouteValues = new RouteValueDictionary();
        httpContext.SetEndpoint(null);

        try
        {
            await statusCodeContext.Next(httpContext);
        }
        finally
        {
            httpContext.Request.Path = originalPath;
            httpContext.Request.QueryString = originalQueryString;
            httpContext.Request.RouteValues = originalRouteValues;
            httpContext.SetEndpoint(originalEndpoint);

            if (statusCodePagesFeature is not null && originalStatusCodePagesEnabled.HasValue)
            {
                statusCodePagesFeature.Enabled = originalStatusCodePagesEnabled.Value;
            }

            httpContext.Features.Set(originalReExecuteFeature);
        }
    }

    internal static int? GetReservedRouteStatusCode(HttpContext httpContext)
    {
        if (httpContext.Request.RouteValues.TryGetValue("statusCode", out var routeValue) != true)
        {
            return null;
        }

        return routeValue switch
        {
            int intValue => intValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => null
        };
    }

    private sealed class BrowserStatusPageReExecuteFeature : IStatusCodeReExecuteFeature
    {
        public BrowserStatusPageReExecuteFeature(
            string originalPathBase,
            string originalPath,
            string? originalQueryString,
            int originalStatusCode,
            Endpoint? endpoint,
            RouteValueDictionary routeValues)
        {
            OriginalPathBase = originalPathBase;
            OriginalPath = originalPath;
            OriginalQueryString = originalQueryString;
            OriginalStatusCode = originalStatusCode;
            Endpoint = endpoint;
            RouteValues = routeValues;
        }

        public string OriginalPathBase { get; set; }

        public string OriginalPath { get; set; }

        public string? OriginalQueryString { get; set; }

        public int OriginalStatusCode { get; set; }

        public Endpoint? Endpoint { get; set; }

        public RouteValueDictionary RouteValues { get; set; }
    }
}
