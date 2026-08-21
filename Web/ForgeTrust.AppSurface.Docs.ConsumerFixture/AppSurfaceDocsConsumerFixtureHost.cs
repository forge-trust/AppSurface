using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.RazorWire;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Docs.ConsumerFixture;

/// <summary>
/// Creates the real ASP.NET Core consumer host used to verify AppSurface Docs Razor Class Library precedence.
/// </summary>
/// <remarks>
/// The fixture deliberately owns conventional <c>Views/_ViewStart.cshtml</c> and
/// <c>Views/Shared/_Layout.cshtml</c> files. AppSurface Docs views must retain their package shell when this host is
/// the application, while hosts remain free to deliberately override the package-specific layout path.
/// </remarks>
public static class AppSurfaceDocsConsumerFixtureHost
{
    /// <summary>
    /// Runs the consumer fixture until the host shuts down.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to AppSurface Web startup.</param>
    /// <returns>A task that completes when the host exits.</returns>
    [ExcludeFromCodeCoverage(
        Justification = "Process lifetime wrapper delegates to the covered host-builder seam and runs until shutdown.")]
    public static Task RunAsync(string[] args) => new ConsumerFixtureStartup().RunAsync(args);

    /// <summary>
    /// Creates a consumer host builder without starting it.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to AppSurface Web startup.</param>
    /// <param name="environmentProvider">
    /// Optional environment provider used by AppSurface startup decisions before the generic host is built.
    /// </param>
    /// <returns>A configured host builder whose application identity is the consumer fixture assembly.</returns>
    public static IHostBuilder CreateBuilder(
        string[] args,
        IEnvironmentProvider? environmentProvider = null)
    {
        var context = new StartupContext(
            args,
            new ConsumerFixtureModule(),
            EnvironmentProvider: environmentProvider)
        {
            OverrideEntryPointAssembly = typeof(AppSurfaceDocsConsumerFixtureHost).Assembly
        };

        return ((IAppSurfaceStartup)new ConsumerFixtureStartup()).CreateHostBuilder(context);
    }

    private static IConfiguration CreateFixtureConfiguration(StartupContext context)
    {
        return new ConfigurationBuilder()
            .AddCommandLine(context.Args)
            .Build();
    }

    private static bool UsesNamedComposition(StartupContext context)
    {
        var configuration = CreateFixtureConfiguration(context);
        return configuration.GetSection("AppSurfaceDocs:Public").Exists()
               || configuration.GetSection("AppSurfaceDocs:Internal").Exists();
    }

    private sealed class ConsumerFixtureStartup : WebStartup<ConsumerFixtureModule>
    {
    }

    private sealed class ConsumerFixtureModule : IAppSurfaceWebModule
    {
        public bool IncludeAsApplicationPart => true;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            if (UsesNamedComposition(context))
            {
                services
                    .AddAuthentication(ConsumerFixtureProofAuth.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, ConsumerFixtureHeaderAuthenticationHandler>(
                        ConsumerFixtureProofAuth.SchemeName,
                        static _ => { });
                services.AddAuthorization(
                    options =>
                    {
                        options.AddPolicy(
                            ConsumerFixtureProofAuth.InternalDocsPolicy,
                            policy =>
                            {
                                policy.AddAuthenticationSchemes(ConsumerFixtureProofAuth.SchemeName);
                                policy.RequireAuthenticatedUser();
                            });
                    });
                return;
            }

            // Docs legacy registration runs in the dependency module before this preference adapter, which relies on
            // the resolver that AddAppSurfaceDocs() installs. Named Docs products use their own Theme sections instead.
            services.AddAppSurfaceWebThemePreferences(options => options.StorageKey = "appsurface_docs_theme");
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
            builder.AddModule<AppSurfaceCachingModule>();
            builder.AddModule<RazorWireWebModule>();
            builder.AddModule<ConsumerFixtureDocsCompositionModule>();
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

        public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
        {
            if (!UsesNamedComposition(context))
            {
                return;
            }

            app.UseAuthentication();
            app.UseAuthorization();
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
        }

    }

    /// <summary>
    /// Maps either the fixture's legacy Docs surface or its named public/internal product pair.
    /// </summary>
    /// <remarks>
    /// This module is a dependency rather than the fixture root because <see cref="WebStartup{TModule}" /> maps
    /// dependency endpoints before root endpoints. That keeps RazorWire transport mapping ahead of Docs mapping and
    /// makes legacy Docs service registration precede the consumer theme-preference adapter.
    /// </remarks>
    private sealed class ConsumerFixtureDocsCompositionModule : IAppSurfaceWebModule
    {
        private readonly AppSurfaceDocsWebModule _legacyDocsModule = new();
        private AppSurfaceDocsInstance? _publicDocs;
        private AppSurfaceDocsInstance? _internalDocs;
        private bool _usesNamedComposition;

        public ConsumerFixtureDocsCompositionModule()
        {
        }

        public bool IncludeAsApplicationPart => true;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
            _usesNamedComposition = UsesNamedComposition(context);
            if (_usesNamedComposition)
            {
                options.StaticFiles.EnableStaticWebAssets = true;
                return;
            }

            _legacyDocsModule.ConfigureWebOptions(context, options);
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            if (!_usesNamedComposition)
            {
                _legacyDocsModule.ConfigureServices(context, services);
                // This wrapper owns the dependency-module slot, so mirror the legacy Docs module's application-part
                // contribution. Named composition adds this part through its own infrastructure registration.
                services.AddControllersWithViews().AddApplicationPart(typeof(AppSurfaceDocsWebModule).Assembly);
                return;
            }

            var configuration = CreateFixtureConfiguration(context);
            _publicDocs = services.AddAppSurfaceDocs(
                "public",
                configuration.GetRequiredSection("AppSurfaceDocs:Public"));
            _internalDocs = services.AddAppSurfaceDocs(
                "internal",
                configuration.GetRequiredSection("AppSurfaceDocs:Internal"));
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
            if (!_usesNamedComposition)
            {
                _legacyDocsModule.ConfigureWebApplication(context, app);
            }
        }

        public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
        {
        }

        public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
        {
            if (!_usesNamedComposition)
            {
                _legacyDocsModule.ConfigureEndpoints(context, endpoints);
                return;
            }

            var publicDocs = _publicDocs
                             ?? throw new InvalidOperationException("Named ConsumerFixture Docs endpoints were configured before public registration.");
            var internalDocs = _internalDocs
                               ?? throw new InvalidOperationException("Named ConsumerFixture Docs endpoints were configured before internal registration.");

            publicDocs.MapEndpoints(endpoints);
            internalDocs
                .MapEndpoints(endpoints)
                .RequireAuthorization(ConsumerFixtureProofAuth.InternalDocsPolicy);
            endpoints.FinalizeAppSurfaceDocsInstances();
        }
    }

    private sealed class ConsumerFixtureHeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public ConsumerFixtureHeaderAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userName = Request.Headers[ConsumerFixtureProofAuth.UserHeaderName].ToString();
            if (string.IsNullOrWhiteSpace(userName))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userName),
                new Claim(ClaimTypes.Name, userName)
            };
            var identity = new ClaimsIdentity(claims, ConsumerFixtureProofAuth.SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, ConsumerFixtureProofAuth.SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}

/// <summary>
/// Names the deliberately simple header-authentication contract used only by the executable multi-instance consumer fixture.
/// </summary>
/// <remarks>
/// The fixture demonstrates where a real host supplies authentication and authorization. It is not a production
/// authentication implementation: a request carrying <see cref="UserHeaderName"/> is treated as an authenticated reader
/// so automated walkthroughs can prove the public/internal route boundary without an external identity provider.
/// </remarks>
public static class ConsumerFixtureProofAuth
{
    /// <summary>Gets the fixture-only authentication scheme name.</summary>
    public const string SchemeName = "ConsumerFixtureHeader";

    /// <summary>Gets the fixture policy applied to every internal Docs endpoint.</summary>
    public const string InternalDocsPolicy = "ConsumerFixtureInternalDocs";

    /// <summary>Gets the request header whose non-blank value names the fixture reader.</summary>
    public const string UserHeaderName = "X-Consumer-Fixture-User";
}
