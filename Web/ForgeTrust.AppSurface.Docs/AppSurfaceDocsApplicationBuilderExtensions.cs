using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Routing;

namespace ForgeTrust.AppSurface.Docs;

/// <summary>Integrates catalog aliases with the host's existing routing and authorization middleware.</summary>
public static class AppSurfaceDocsApplicationBuilderExtensions
{
    private const string SelectorKey = "ForgeTrust.AppSurface.Docs.AliasSelector";

    /// <summary>Reserves active catalog alias namespaces before routing can normalize received request evidence.</summary>
    /// <param name="app">The application builder on which named Docs instances are finalized.</param>
    /// <returns>The same builder for fluent composition.</returns>
    /// <remarks>
    /// Call after PathBase/forwarded-path setup and before UseRouting, authentication, authorization and any response
    /// writer. Map named handles and finalize them on this builder after installing this hook. The hook selects the
    /// actual convention-bearing endpoint; the host's normal middleware evaluates authorization before its delegate.
    /// Repeated calls on this builder are idempotent. Alias-free catalogs require no hook. Default module hosts install
    /// it automatically. See the Docs consumer guide's named-host alias example and README alias diagnostics.
    /// </remarks>
    public static IApplicationBuilder UseAppSurfaceDocsAliases(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Properties.TryGetValue(SelectorKey, out var existing) && ReferenceEquals(existing, app))
        {
            return app;
        }

        app.Properties[SelectorKey] = app;
        var state = app.ApplicationServices.GetRequiredService<AppSurfaceDocsAliasOwnershipState>();
        app.Use(async (context, next) =>
        {
            var request = context.Features.Get<AppSurfaceDocsAliasRequest>() ?? state.Select(context);
            if (request is not null)
            {
                context.Features.Set(request);
                ArmOwnedHeaders(context);
            }

            await next();
        });
        return app;
    }

    /// <summary>Checks the marker belongs to this builder, rather than an inherited branch's properties.</summary>
    internal static bool IsInstalled(IEndpointRouteBuilder endpoints)
    {
        var properties = endpoints.CreateApplicationBuilder().Properties;
        if (!properties.TryGetValue(SelectorKey, out var marker) || marker is not IApplicationBuilder installed)
        {
            return false;
        }

        return ReferenceEquals(installed, endpoints)
            || installed.Properties.TryGetValue("__EndpointRouteBuilder", out var local) && ReferenceEquals(local, endpoints)
            || installed.Properties.TryGetValue("__GlobalEndpointRouteBuilder", out var global) && ReferenceEquals(global, endpoints);
    }

    /// <summary>Commits the cache contract after downstream header callbacks, including host auth responses.</summary>
    internal static void ArmOwnedHeaders(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.OnStarting(static state =>
        {
            ((HttpContext)state).Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }, context);
    }

    /// <summary>Suppresses host status-page replacement for terminal alias failures only.</summary>
    internal static void SuppressStatusPages(HttpContext context)
    {
        if (context.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
        {
            statusPages.Enabled = false;
        }
    }
}

/// <summary>Identifies a single active namespace's actual fully built endpoint.</summary>
/// <param name="NamespaceRoot">App-relative namespace root.</param>
internal sealed record AppSurfaceDocsAliasOwnershipMetadata(string NamespaceRoot);

/// <summary>Freezes the actual mapped endpoint references once, after all host conventions have been applied.</summary>
/// <remarks>Catalog → active namespace endpoint → immutable endpoint dictionary → raw classification → host auth.</remarks>
internal sealed class AppSurfaceDocsAliasOwnershipState
{
    private readonly object _gate = new();
    private readonly ILogger<AppSurfaceDocsAliasOwnershipState> _logger;
    private IEndpointRouteBuilder? _endpoints;
    private IReadOnlyDictionary<string, Endpoint>? _owners;
    private IReadOnlyList<string> _roots = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _unsafeWarnings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the default module handler shared by middleware setup and endpoint mapping.</summary>
    internal AppSurfaceDocsPublishedTreeHandler? DefaultHandler { get; set; }

    public AppSurfaceDocsAliasOwnershipState(ILogger<AppSurfaceDocsAliasOwnershipState> logger)
    {
        _logger = logger;
    }

    /// <summary>Binds the completed route builder without replaying conventions or rebuilding authorization metadata.</summary>
    internal void Bind(IEndpointRouteBuilder endpoints)
    {
        lock (_gate)
        {
            if (_owners is not null)
            {
                throw new InvalidOperationException("Docs alias endpoints cannot be changed after ownership selection begins.");
            }

            _endpoints = endpoints;
        }
    }

    /// <summary>Selects the real owner's endpoint and returns sticky received-path state, without writing a body.</summary>
    internal AppSurfaceDocsAliasRequest? Select(HttpContext context)
    {
        EnsureOwners();
        var request = AppSurfaceDocsAliasRequestClassifier.Classify(context, _roots);
        if (request is null)
        {
            return null;
        }

        var endpoint = _owners![request.NamespaceRoot];
        context.SetEndpoint(endpoint);
        var instanceName = endpoint.Metadata.GetMetadata<AppSurfaceDocsEndpointMetadata>()?.Name ?? "Default";
        if (!request.IsSafe && _unsafeWarnings.TryAdd(instanceName, 0))
        {
            _logger.LogWarning(
                "{AliasDiagnosticCode}: {AliasReason}. {AliasAction} InstanceName={InstanceName}. See {DocumentationReference}.",
                "ASDOCSALIAS010", "received-path-invalid",
                "Retain RawTarget and apply PathBase before the alias hook",
                instanceName,
                "https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs/README.md#asdocsalias010");
        }

        return request;
    }

    private void EnsureOwners()
    {
        if (_owners is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_owners is not null || _endpoints is null)
            {
                return;
            }

            var owners = new Dictionary<string, Endpoint>(StringComparer.OrdinalIgnoreCase);
            foreach (var endpoint in _endpoints.DataSources.SelectMany(source => source.Endpoints))
            {
                if (endpoint.Metadata.GetMetadata<AppSurfaceDocsAliasOwnershipMetadata>() is { } metadata)
                {
                    owners.Add(metadata.NamespaceRoot, endpoint);
                }
            }

            _roots = owners.Keys.ToArray();
            Volatile.Write(ref _owners, owners);
        }
    }
}
