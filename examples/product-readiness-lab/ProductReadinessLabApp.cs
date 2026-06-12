using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Flow.DurableTask;
using ForgeTrust.AppSurface.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

[assembly: InternalsVisibleTo("ProductReadinessLab.Tests")]

namespace ProductReadinessLab;

/// <summary>
/// Starts the AppSurface product-readiness lab web application.
/// </summary>
public static class ProductReadinessLabApp
{
    /// <summary>
    /// Runs the lab web application with AppSurface Web startup.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the web host.</param>
    /// <returns>A task that completes when the web host stops.</returns>
    public static Task RunAsync(string[] args) =>
        WebApp<ProductReadinessModule>.RunAsync(
            args,
            options =>
            {
                options.MapEndpoints = ProductReadinessEndpoints.Map;
            });
}

/// <summary>
/// AppSurface module that wires the product-readiness lab services.
/// </summary>
public sealed class ProductReadinessModule : IAppSurfaceWebModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddProductReadinessLab(context.EnvironmentProvider.Environment, context.EnvironmentProvider.IsDevelopment);
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceAspNetCoreAuthModule>();
        builder.AddModule<AppSurfaceFlowDurableTaskModule>();
    }

    /// <inheritdoc />
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <inheritdoc />
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <inheritdoc />
    public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }
}

/// <summary>
/// Shared service registration helpers for the product-readiness lab.
/// </summary>
internal static class ProductReadinessServiceCollectionExtensions
{
    /// <summary>
    /// Registers the lab's proof services.
    /// </summary>
    /// <param name="services">The service collection receiving registrations.</param>
    /// <param name="environmentName">The host environment name.</param>
    /// <param name="isDevelopment">Whether the current host is a development host.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddProductReadinessLab(
        this IServiceCollection services,
        string environmentName,
        bool isDevelopment)
    {
        var proofAuthEnabled = IsProofAuthEnabled(isDevelopment);
        ProductReadinessProofAuthGuard.Validate(environmentName, isDevelopment, proofAuthEnabled);

        if (proofAuthEnabled)
        {
            services
                .AddAuthentication(ProductReadinessProofAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, ProductReadinessProofAuthenticationHandler>(
                    ProductReadinessProofAuthenticationHandler.SchemeName,
                    options => { _ = options; });
        }
        else
        {
            services.AddAuthentication();
        }

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                ProductReadinessPolicies.OperatorsOnly,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireClaim("role", "operator")
                    .RequireClaim("sub"));
            options.AddPolicy(
                ProductReadinessPolicies.UnavailableEntitlement,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireClaim("entitlement", "product-readiness-admin"));
        });

        services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim("sub"));
        services.AddOptions<AppSurfaceFlowOptions>();
        services.AddOptions<AppSurfaceFlowDurableTaskOptions>();
        services.TryAddSingleton<IFlowContextSerializer, SystemTextJsonFlowContextSerializer>();
        services.TryAddSingleton<FlowContextSerializationValidator>();
        services.TryAddSingleton(typeof(IDurableTaskFlowRunner<>), typeof(DurableTaskFlowRunner<>));
        services.TryAddSingleton(typeof(IDurableTaskFlowClient<>), typeof(DurableTaskFlowClient<>));
        services.AddSingleton(ProductReadinessFlowDefinition.Build());
        services.AddSingleton<IFlowDefinitionRegistry>(sp =>
        {
            var registry = new FlowDefinitionRegistry();
            registry.Register(sp.GetRequiredService<FlowDefinition<ProductApprovalState>>());
            return registry;
        });
        services.AddSingleton<IFlowResumeAuthorizer, ProductReadinessResumeAuthorizer>();
        services.AddSingleton<IProductStateStore>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString("ProductReadiness");
            return string.IsNullOrWhiteSpace(connectionString)
                ? new InMemoryProductStateStore()
                : new PostgresProductStateStore(connectionString);
        });
        services.AddSingleton<ProductApprovalInProcessHost>();
        services.AddSingleton<ProductReadinessReportService>();

        return services;
    }

    /// <summary>
    /// Resolves whether the proof authentication handler should be enabled.
    /// </summary>
    /// <param name="isDevelopment">Whether the current host is a development host.</param>
    /// <param name="configuredValue">Optional configured value from PRODUCT_READINESS_ENABLE_PROOF_AUTH.</param>
    /// <returns><see langword="true" /> when proof authentication should be enabled.</returns>
    public static bool ResolveProofAuthEnabled(bool isDevelopment, string? configuredValue)
    {
        return string.IsNullOrWhiteSpace(configuredValue)
            ? isDevelopment
            : bool.TryParse(configuredValue, out var enabled) && enabled;
    }

    private static bool IsProofAuthEnabled(bool isDevelopment)
    {
        var value = Environment.GetEnvironmentVariable("PRODUCT_READINESS_ENABLE_PROOF_AUTH");
        return ResolveProofAuthEnabled(isDevelopment, value);
    }
}

/// <summary>
/// Guard for the proof-only authentication handler.
/// </summary>
internal static class ProductReadinessProofAuthGuard
{
    /// <summary>
    /// Throws when proof authentication is enabled outside local development.
    /// </summary>
    /// <param name="environmentName">Host environment name.</param>
    /// <param name="isDevelopment">Whether the host is marked as development.</param>
    /// <param name="proofAuthEnabled">Whether proof authentication is enabled.</param>
    public static void Validate(string environmentName, bool isDevelopment, bool proofAuthEnabled)
    {
        if (proofAuthEnabled && !isDevelopment)
        {
            throw new InvalidOperationException(
                $"The product-readiness lab proof auth handler cannot run in '{environmentName}'. Set PRODUCT_READINESS_ENABLE_PROOF_AUTH=false or use a Development/local host.");
        }
    }
}

/// <summary>
/// Local proof-only authentication handler driven by the X-Proof-User header.
/// </summary>
internal sealed class ProductReadinessProofAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>
    /// Authentication scheme name used by the lab proof handler.
    /// </summary>
    public const string SchemeName = "ProductReadinessProof";

    /// <summary>
    /// Creates a proof authentication handler.
    /// </summary>
    public ProductReadinessProofAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Proof-User"].ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = ProductReadinessProofUsers.CreatePrincipal(user);
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

/// <summary>
/// Supported local proof users.
/// </summary>
internal static class ProductReadinessProofUsers
{
    /// <summary>
    /// Creates a local proof principal.
    /// </summary>
    /// <param name="user">Proof user name.</param>
    /// <returns>An authenticated principal for known proof users; otherwise <see langword="null" />.</returns>
    public static ClaimsPrincipal? CreatePrincipal(string user)
    {
        Claim[]? claims = user switch
        {
            "operator" =>
            [
                new Claim("sub", "operator-1"),
                new Claim("role", "operator"),
            ],
            "viewer" =>
            [
                new Claim("sub", "viewer-1"),
                new Claim("role", "viewer"),
            ],
            "nosub" => [new Claim("role", "operator")],
            _ => null,
        };

        return claims is null
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity(claims, ProductReadinessProofAuthenticationHandler.SchemeName));
    }
}

/// <summary>
/// Named authorization policies used by the lab.
/// </summary>
internal static class ProductReadinessPolicies
{
    /// <summary>
    /// Local operator policy used for proof requests.
    /// </summary>
    public const string OperatorsOnly = "OperatorsOnly";

    /// <summary>
    /// Local probe policy that intentionally fails for bundled proof users.
    /// </summary>
    public const string UnavailableEntitlement = "UnavailableEntitlement";
}
