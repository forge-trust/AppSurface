using System.Text;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NamedCanaryLab;

/// <summary>Configures the local named-canary adoption lab.</summary>
internal static class NamedCanaryLabApp
{
    public const string CanaryName = "lab.proof";
    private const int MaximumMarkerUtf8Bytes = 256;
    private static readonly TimeSpan StaleOffset = TimeSpan.FromMinutes(5);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static Task RunAsync(string[] args) => WebApp<NamedCanaryLabModule>.RunAsync(args);

    public static void Configure(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton(sp =>
            CanaryLabSettings.Create(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<IHostEnvironment>()));
        builder.Services.AddHostedService<CanaryLabStartupValidationService>();
        ConfigureServices(builder.Services);
    }

    public static void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CanaryLabProofStore>();
        services.AddSingleton<CanaryLabEvaluator>();
        services
            .AddAuthentication(CanaryLabAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, CanaryLabAuthenticationHandler>(
                CanaryLabAuthenticationHandler.SchemeName,
                _ => { });
        services.AddAuthorization(options =>
            options.AddPolicy(
                CanaryLabPolicies.OperatorsOnly,
                policy => policy
                    .AddAuthenticationSchemes(CanaryLabAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()));
        services.AddAppSurfaceCanary<CanaryLabEvaluator>(
            CanaryName,
            canary =>
            {
                canary.RequireMarker();
                canary.RequireFreshSince();
            });
    }

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseAuthentication();
        app.UseAuthorization();

        MapEndpoints(app);
    }

    public static void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/", () => Results.Text("AppSurface named-canary lab is running.", "text/plain"));
        endpoints.MapPost("/lab/canary/trigger", Trigger)
            .RequireAuthorization(CanaryLabPolicies.OperatorsOnly);
        endpoints.MapAppSurfaceCanaries(CanaryLabPolicies.OperatorsOnly);
    }

    private static IResult Trigger(
        HttpContext httpContext,
        CanaryLabSettings settings,
        CanaryLabProofStore proofStore,
        TimeProvider timeProvider)
    {
        if (!TryReadMarker(httpContext, out var marker))
        {
            return Results.BadRequest(new CanaryLabProblem(
                "invalid-request",
                "A single nonblank canary marker header is required.",
                "Set the canary marker through an environment-sourced request header and retry."));
        }

        if (settings.Scenario != CanaryLabScenario.Pending)
        {
            var observedAt = timeProvider.GetUtcNow();
            if (settings.Scenario == CanaryLabScenario.Stale)
            {
                observedAt -= StaleOffset;
            }

            var recorded = proofStore.Record(new CanaryProofRecord(
                settings.Identity,
                CanaryLabMarkerFingerprint.Create(marker),
                observedAt,
                AppSurfaceCanaryStatus.Pass));
            if (recorded is null)
            {
                return Results.Json(
                    new CanaryLabProblem(
                        "proof-store-full",
                        "The local proof store is full.",
                        "Restart the Development-only lab before triggering a new marker."),
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        return Results.Accepted(
            value: new CanaryLabTriggerAcknowledgement(
                "accepted",
                "The local synthetic workflow was accepted without exposing its proof."));
    }

    private static bool TryReadMarker(HttpContext httpContext, out string marker)
    {
        marker = string.Empty;
        if (!httpContext.Request.Headers.TryGetValue(AppSurfaceCanaryHeaderNames.Marker, out var values)
            || values.Count != 1
            || string.IsNullOrWhiteSpace(values[0]))
        {
            return false;
        }

        marker = values[0]!;
        return IsValidMarker(marker);
    }

    /// <summary>Validates the same opaque-marker profile required by the named-canary evaluation route.</summary>
    internal static bool IsValidMarker(string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker) || marker.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(marker) <= MaximumMarkerUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}

/// <summary>Contains a bounded trigger acknowledgement.</summary>
internal sealed record CanaryLabTriggerAcknowledgement(string Status, string Summary);

/// <summary>Contains a bounded trigger validation problem.</summary>
internal sealed record CanaryLabProblem(string Code, string Summary, string NextAction);

/// <summary>Fails host startup before the lab can serve requests with invalid local-only configuration.</summary>
internal sealed class CanaryLabStartupValidationService(CanaryLabSettings settings) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = settings;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Connects the named-canary lab to the AppSurface Web host.</summary>
public sealed class NamedCanaryLabModule : IAppSurfaceWebModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp =>
            CanaryLabSettings.Create(
                sp.GetRequiredService<IConfiguration>(),
                context.IsDevelopment));
        services.AddHostedService<CanaryLabStartupValidationService>();
        NamedCanaryLabApp.ConfigureServices(services);
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
    }

    /// <inheritdoc />
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(builder);
    }

    /// <inheritdoc />
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(builder);
    }

    /// <inheritdoc />
    public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(app);

        app.UseAuthentication();
        app.UseAuthorization();
    }

    /// <inheritdoc />
    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(context);
        NamedCanaryLabApp.MapEndpoints(endpoints);
    }
}
