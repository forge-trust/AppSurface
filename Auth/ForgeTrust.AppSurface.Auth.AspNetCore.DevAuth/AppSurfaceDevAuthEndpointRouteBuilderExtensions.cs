using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Maps AppSurface DevAuth local-only control and status endpoints.
/// </summary>
public static class AppSurfaceDevAuthEndpointRouteBuilderExtensions
{
    private const string ProtectorPurpose = "ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Persona.v1";
    private const string RedactedValue = "(hidden)";

    /// <summary>
    /// Maps the AppSurface DevAuth control page, status JSON, select persona, and clear persona endpoints.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder that receives the local-only endpoints.</param>
    /// <returns>The same endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapAppSurfaceDevAuth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>().Value;
        AppSurfaceDevAuthServiceCollectionExtensions.ValidateOptions(options);
        ValidateReservedPathIsAvailable(endpoints, options.PathPrefix);

        var group = endpoints.MapGroup(options.PathPrefix);
        group.WithMetadata(new AppSurfaceDevAuthEndpointMetadata());
        group.AddEndpointFilter(RequireLocalControlRequestAsync);

        group.MapGet("/", RenderControlPageAsync);
        group.MapGet("/status", RenderStatusAsync);
        group.MapPost("/select/{personaId}", SelectPersonaAsync);
        group.MapPost("/clear", ClearPersonaAsync);

        return endpoints;
    }

    private static void ValidateReservedPathIsAvailable(IEndpointRouteBuilder endpoints, string pathPrefix)
    {
        var reservedPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            NormalizeRoutePattern(pathPrefix),
            NormalizeRoutePattern(pathPrefix + "/"),
            NormalizeRoutePattern(pathPrefix + "/status"),
            NormalizeRoutePattern(pathPrefix + "/select/{personaId}"),
            NormalizeRoutePattern(pathPrefix + "/clear"),
        };

        var conflict = endpoints.DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(rawText => !string.IsNullOrWhiteSpace(rawText))
            .Select(rawText => NormalizeRoutePattern(rawText!))
            .FirstOrDefault(reservedPaths.Contains);

        if (conflict is not null)
        {
            throw new AppSurfaceDevAuthException(
                AppSurfaceDevAuthDiagnostics.ReservedPathConflict,
                $"ASDEV005 Problem: AppSurface DevAuth path prefix '{pathPrefix}' conflicts with an existing endpoint. Cause: the host already mapped '{conflict}'. Fix: move the existing endpoint or set AppSurfaceDevAuthOptions.PathPrefix to another local-only path. Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics.");
        }
    }

    private static string NormalizeRoutePattern(string value)
    {
        var normalized = value.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        return normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal)
            ? normalized[..^1]
            : normalized;
    }

    private static async ValueTask<object?> RequireLocalControlRequestAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>().Value;
        SetNoStoreHeaders(context.HttpContext);

        if (!options.RequireLoopbackControlRequests || IsLoopback(context.HttpContext.Connection.RemoteIpAddress))
        {
            return await next(context);
        }

        return Results.Problem(
            title: "AppSurface DevAuth local request required",
            detail: "Problem: AppSurface DevAuth control endpoints only accept loopback requests. Cause: fake local auth tooling was requested from a non-local address. Fix: use localhost/127.0.0.1 or remove DevAuth from this host. Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static IResult RenderControlPageAsync(
        HttpContext httpContext,
        IHostEnvironment environment,
        IOptions<AppSurfaceDevAuthOptions> options,
        IDataProtectionProvider dataProtectionProvider)
    {
        SetNoStoreHeaders(httpContext);
        var status = BuildStatus(httpContext, environment, options.Value, dataProtectionProvider);
        return Results.Content(RenderControlPage(status, options.Value), MediaTypeNames.Text.Html, Encoding.UTF8);
    }

    private static IResult RenderStatusAsync(
        HttpContext httpContext,
        IHostEnvironment environment,
        IOptions<AppSurfaceDevAuthOptions> options,
        IDataProtectionProvider dataProtectionProvider)
    {
        SetNoStoreHeaders(httpContext);
        var status = BuildStatus(httpContext, environment, options.Value, dataProtectionProvider);
        return Results.Json(status);
    }

    private static IResult SelectPersonaAsync(
        string personaId,
        HttpContext httpContext,
        IHostEnvironment environment,
        IOptions<AppSurfaceDevAuthOptions> options,
        IDataProtectionProvider dataProtectionProvider)
    {
        SetNoStoreHeaders(httpContext);
        var devAuthOptions = options.Value;
        string normalized;
        try
        {
            normalized = AppSurfaceDevAuthUserBuilder.NormalizePersonaId(personaId);
        }
        catch (AppSurfaceDevAuthException ex)
            when (string.Equals(ex.DiagnosticCode, AppSurfaceDevAuthDiagnostics.InvalidPersonaId, StringComparison.Ordinal))
        {
            return CreateInvalidPersonaResult();
        }

        if (!devAuthOptions.Users.Personas.TryGetValue(normalized, out var persona))
        {
            return CreateInvalidPersonaResult();
        }

        var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        httpContext.Response.Cookies.Append(
            devAuthOptions.CookieName,
            protector.Protect(normalized),
            CreatePersonaCookieOptions());

        var status = new AppSurfaceDevAuthStatus(
            Enabled: environment.IsDevelopment(),
            Environment: environment.EnvironmentName,
            Scheme: devAuthOptions.SchemeName,
            PathPrefix: devAuthOptions.PathPrefix,
            PersonaId: persona.Id,
            DisplayName: SafeStatusValue(persona.DisplayName),
            Subject: SafeStatusValue(persona.Subject),
            IsAnonymous: false,
            Warnings: []);

        return Results.Content(RenderControlPage(status, devAuthOptions), MediaTypeNames.Text.Html, Encoding.UTF8);
    }

    private static IResult CreateInvalidPersonaResult()
    {
        return Results.NotFound(new
        {
            code = AppSurfaceDevAuthDiagnostics.InvalidPersonaId,
            message = "ASDEV006 Problem: The selected DevAuth persona is invalid or stale. Cause: the selected persona id is not configured or is not route-safe. Fix: select a configured persona or clear the DevAuth cookie. Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics.",
        });
    }

    private static IResult ClearPersonaAsync(
        HttpContext httpContext,
        IHostEnvironment environment,
        IOptions<AppSurfaceDevAuthOptions> options)
    {
        SetNoStoreHeaders(httpContext);
        var devAuthOptions = options.Value;
        httpContext.Response.Cookies.Delete(devAuthOptions.CookieName, new CookieOptions { Path = "/" });

        var status = new AppSurfaceDevAuthStatus(
            Enabled: environment.IsDevelopment(),
            Environment: environment.EnvironmentName,
            Scheme: devAuthOptions.SchemeName,
            PathPrefix: devAuthOptions.PathPrefix,
            PersonaId: null,
            DisplayName: null,
            Subject: null,
            IsAnonymous: true,
            Warnings: []);

        return Results.Content(RenderControlPage(status, devAuthOptions), MediaTypeNames.Text.Html, Encoding.UTF8);
    }

    private static CookieOptions CreatePersonaCookieOptions()
    {
        return new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            Path = "/",
            SameSite = SameSiteMode.Strict,
            Secure = false,
        };
    }

    private static AppSurfaceDevAuthStatus BuildStatus(
        HttpContext httpContext,
        IHostEnvironment environment,
        AppSurfaceDevAuthOptions options,
        IDataProtectionProvider dataProtectionProvider)
    {
        var warnings = new List<string>();
        AppSurfaceDevAuthPersona? persona = null;
        string? personaId = null;

        if (httpContext.Request.Cookies.TryGetValue(options.CookieName, out var protectedPersonaId) &&
            !string.IsNullOrWhiteSpace(protectedPersonaId))
        {
            try
            {
                personaId = dataProtectionProvider
                    .CreateProtector(ProtectorPurpose)
                    .Unprotect(protectedPersonaId);

                if (!options.Users.Personas.TryGetValue(personaId, out persona))
                {
                    warnings.Add(AppSurfaceDevAuthDiagnostics.InvalidPersonaId);
                }
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                warnings.Add(AppSurfaceDevAuthDiagnostics.InvalidPersonaId);
            }
        }

        return new AppSurfaceDevAuthStatus(
            Enabled: environment.IsDevelopment(),
            Environment: environment.EnvironmentName,
            Scheme: options.SchemeName,
            PathPrefix: options.PathPrefix,
            PersonaId: persona?.Id,
            DisplayName: SafeStatusValue(persona?.DisplayName),
            Subject: SafeStatusValue(persona?.Subject),
            IsAnonymous: persona is null,
            Warnings: warnings);
    }

    private static string RenderControlPage(AppSurfaceDevAuthStatus status, AppSurfaceDevAuthOptions options)
    {
        var html = HtmlEncoder.Default;
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine("<meta name=\"robots\" content=\"noindex,nofollow\">");
        builder.AppendLine("<title>AppSurface Dev Auth</title>");
        builder.AppendLine("<style>");
        builder.AppendLine("body{font-family:system-ui,-apple-system,Segoe UI,sans-serif;margin:0;background:#f8fafc;color:#111827}main{max-width:920px;margin:0 auto;padding:24px}.danger{border:2px solid #b91c1c;background:#fee2e2;padding:12px;margin-bottom:16px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:12px}.panel{border:1px solid #cbd5e1;background:#fff;padding:12px}.actions{display:flex;flex-wrap:wrap;gap:8px}button{padding:8px 12px;border:1px solid #334155;background:#fff}button:focus{outline:3px solid #2563eb;outline-offset:2px}.selected{font-weight:700;border-color:#166534}.warning{color:#991b1b;font-weight:700}code{white-space:normal;word-break:break-word}@media(max-width:640px){main{padding:12px}.actions{display:grid}}");
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body><main>");
        builder.AppendLine("<section class=\"danger\" aria-label=\"Development only warning\">");
        builder.AppendLine("<h1>AppSurface Dev Auth [DEVELOPMENT ONLY]</h1>");
        builder.AppendLine("<p>This page is local development tooling. Do not enable it in deployed environments.</p>");
        builder.AppendLine("</section>");
        builder.AppendLine("<section class=\"grid\" aria-label=\"Current DevAuth state\">");
        builder.AppendLine($"<div class=\"panel\"><strong>Environment</strong><br>{html.Encode(status.Environment)}</div>");
        builder.AppendLine($"<div class=\"panel\"><strong>Scheme</strong><br><code>{html.Encode(status.Scheme)}</code></div>");
        builder.AppendLine($"<div class=\"panel\"><strong>Current persona</strong><br>{html.Encode(DisplayStatusPersonaName(status))}</div>");
        builder.AppendLine($"<div class=\"panel\"><strong>Subject</strong><br><code>{html.Encode(DisplayStatusSubject(status))}</code></div>");
        builder.AppendLine("</section>");
        builder.AppendLine("<section class=\"panel\" aria-label=\"Select persona\"><h2>Select persona</h2><div class=\"actions\">");
        foreach (var persona in options.Users.Personas.Values)
        {
            var selected = string.Equals(status.PersonaId, persona.Id, StringComparison.Ordinal);
            builder.AppendLine($"<form method=\"post\" action=\"{html.Encode(options.PathPrefix)}/select/{html.Encode(persona.Id)}\"><button class=\"{(selected ? "selected" : string.Empty)}\" aria-current=\"{(selected ? "true" : "false")}\">{html.Encode(DisplayPersonaName(persona))}</button></form>");
        }

        builder.AppendLine($"<form method=\"post\" action=\"{html.Encode(options.PathPrefix)}/clear\"><button>Clear persona</button></form>");
        builder.AppendLine("</div></section>");
        builder.AppendLine("<section class=\"panel\" aria-label=\"Claims preview\"><h2>Claims preview</h2>");
        if (status.PersonaId is not null && options.Users.Personas.TryGetValue(status.PersonaId, out var current))
        {
            var displayClaims = current.Claims
                .Where(claim => IsDisplaySafeClaim(claim.Type, claim.Value, options))
                .ToArray();
            var hiddenClaimCount = current.Claims.Count - displayClaims.Length;

            if (displayClaims.Length > 0)
            {
                builder.AppendLine("<dl>");
                foreach (var claim in displayClaims)
                {
                    builder.AppendLine($"<dt>{html.Encode(claim.Type)}</dt><dd><code>{html.Encode(claim.Value)}</code></dd>");
                }

                builder.AppendLine("</dl>");
            }

            if (hiddenClaimCount > 0)
            {
                builder.AppendLine($"<p>{hiddenClaimCount} claim(s) hidden from preview because they are not display-safe.</p>");
            }
        }
        else
        {
            builder.AppendLine("<p>Anonymous. No local persona is selected.</p>");
        }

        builder.AppendLine("</section>");
        builder.AppendLine("<section class=\"panel\" aria-label=\"Marker preview\"><h2>Marker preview</h2>");
        builder.AppendLine($"<p><a href=\"{html.Encode(options.PathPrefix)}/\">DEV AUTH: {html.Encode(DisplayStatusPersonaName(status))} ({html.Encode(status.Scheme)})</a></p>");
        builder.AppendLine("</section>");
        if (status.Warnings.Count > 0)
        {
            builder.AppendLine("<section class=\"panel warning\" aria-label=\"Warnings\"><h2>Status warnings</h2><ul>");
            foreach (var warning in status.Warnings)
            {
                builder.AppendLine($"<li>{html.Encode(warning)}</li>");
            }

            builder.AppendLine("</ul></section>");
        }

        builder.AppendLine("</main></body></html>");
        return builder.ToString();
    }

    private static bool IsLoopback(IPAddress? remoteAddress)
    {
        return remoteAddress is null || IPAddress.IsLoopback(remoteAddress);
    }

    private static bool IsDisplaySafeClaim(string type, string value, AppSurfaceDevAuthOptions options)
    {
        if (!options.DisplayClaimTypes.Contains(type) ||
            string.IsNullOrWhiteSpace(value) ||
            value.Length > 128)
        {
            return false;
        }

        return !ContainsSensitiveToken(type) && !ContainsSensitiveToken(value);
    }

    private static string DisplayPersonaName(AppSurfaceDevAuthPersona persona)
    {
        return SafeStatusValue(persona.DisplayName) ?? RedactedValue;
    }

    private static string DisplayStatusPersonaName(AppSurfaceDevAuthStatus status)
    {
        return status.DisplayName ?? (status.PersonaId is null ? "Anonymous" : RedactedValue);
    }

    private static string DisplayStatusSubject(AppSurfaceDevAuthStatus status)
    {
        return status.Subject ?? (status.PersonaId is null ? "(none)" : RedactedValue);
    }

    private static string? SafeStatusValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            ContainsSensitiveToken(value))
        {
            return null;
        }

        return value;
    }

    private static bool ContainsSensitiveToken(string value)
    {
        return value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("email", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("mail", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("key", StringComparison.OrdinalIgnoreCase) ||
            value.Contains('@', StringComparison.Ordinal);
    }

    private static void SetNoStoreHeaders(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store, no-cache";
        httpContext.Response.Headers.Pragma = "no-cache";
        httpContext.Response.Headers.Expires = "0";
        httpContext.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }
}

internal sealed record AppSurfaceDevAuthStatus(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("scheme")] string Scheme,
    [property: JsonPropertyName("pathPrefix")] string PathPrefix,
    [property: JsonPropertyName("personaId")] string? PersonaId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("isAnonymous")] bool IsAnonymous,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

/// <summary>
/// Marker metadata applied to endpoints mapped by AppSurface DevAuth.
/// </summary>
public sealed class AppSurfaceDevAuthEndpointMetadata
{
}
