using ForgeTrust.AppSurface.Theming;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.AppSurface.Web.Theming;

var builder = WebApplication.CreateBuilder(args);
TenantThemeProofHostEnvironment.ThrowUnlessDevelopment(builder.Environment.EnvironmentName);

builder.Services.AddRazorPages();

builder.Services.AddAppSurfaceTheming(options =>
{
    var defaultPair = AppSurfaceThemePair.AppSurface();
    options.DefaultTheme = defaultPair.Id;
    options.DefaultMode = AppSurfaceThemeMode.System;
    options.Pairs.Add(defaultPair);
    options.Pairs.Add(new AppSurfaceThemePair(new AppSurfaceThemeId("shared-blue"), defaultPair.Light, defaultPair.Dark));
});
builder.Services.AddScoped<TenantThemeContext>();
builder.Services.AddSingleton(
    serviceProvider =>
        TenantThemeMap.Create(
            serviceProvider.GetRequiredService<IAppSurfaceThemeRegistry>(),
            [
                new TenantThemeMapping("tenant-a", new AppSurfaceThemeId("shared-blue")),
                new TenantThemeMapping("tenant-b", new AppSurfaceThemeId("shared-blue"))
            ]));
builder.Services.AddScoped<IAppSurfaceWebThemeSelectionPolicy, TenantThemeSelectionPolicy>();
builder.Services.AddAppSurfaceWebThemeSelection();

var app = builder.Build();
app.Use(
    async (httpContext, next) =>
    {
        // Proof-only stand-in for a host's authentication and authorization boundary. Production code must obtain
        // this context from already-authorized application state, never trust a request header as a tenant identity.
        var requestedTenant = httpContext.Request.Headers["X-Proof-Authorized-Tenant"].ToString();
        var tenantContext = httpContext.RequestServices.GetRequiredService<TenantThemeContext>();
        tenantContext.TenantId = requestedTenant is "tenant-a" or "tenant-b" ? requestedTenant : null;

        // The conservative host response-cache policy. AppSurface does not add this header or own cache behavior.
        httpContext.Response.Headers.CacheControl = "private, no-store";
        await next(httpContext);
    });
app.MapRazorPages();
app.Run();

public sealed class TenantThemeContext
{
    public string? TenantId { get; set; }
}

public static class TenantThemeProofHostEnvironment
{
    public static void ThrowUnlessDevelopment(string? environmentName)
    {
        if (!string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The tenant-theme selection proof host runs only in Development because it accepts a proof-only request header.");
        }
    }
}

public sealed record TenantThemeMapping(string TenantId, AppSurfaceThemeId ThemeId);

public sealed class TenantThemeMap
{
    private readonly IReadOnlyDictionary<string, AppSurfaceThemeId> _pairs;

    private TenantThemeMap(IReadOnlyDictionary<string, AppSurfaceThemeId> pairs)
    {
        _pairs = pairs;
    }

    public static TenantThemeMap Create(
        IAppSurfaceThemeRegistry registry,
        IEnumerable<TenantThemeMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(mappings);

        var registeredPairIds = new HashSet<string>(
            registry.ThemeIds.Select(themeId => themeId.Value),
            StringComparer.Ordinal);
        var pairs = new Dictionary<string, AppSurfaceThemeId>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            if (mapping is null)
            {
                throw new InvalidOperationException("Tenant theme mappings cannot contain null entries.");
            }

            if (string.IsNullOrWhiteSpace(mapping.TenantId)
                || !string.Equals(mapping.TenantId, mapping.TenantId.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Tenant theme mappings require a non-blank tenant id without surrounding whitespace.");
            }

            if (!registeredPairIds.Contains(mapping.ThemeId.Value))
            {
                throw new InvalidOperationException(
                    "Tenant theme mappings must reference a pair registered by AddAppSurfaceTheming.");
            }

            if (!pairs.TryAdd(mapping.TenantId, mapping.ThemeId))
            {
                throw new InvalidOperationException(
                    "Tenant theme mappings cannot contain the same ordinal tenant id more than once.");
            }
        }

        return new TenantThemeMap(pairs);
    }

    public bool TryGet(string? tenantId, out AppSurfaceThemeId themeId)
    {
        themeId = default;
        return tenantId is not null && _pairs.TryGetValue(tenantId, out themeId);
    }
}

public sealed class TenantThemeSelectionPolicy(TenantThemeContext context, TenantThemeMap map)
    : IAppSurfaceWebThemeSelectionPolicy
{

    public bool TrySelect(out AppSurfaceThemeId themeId)
    {
        themeId = default;
        return context.TenantId is { } tenantId && map.TryGet(tenantId, out themeId);
    }
}
