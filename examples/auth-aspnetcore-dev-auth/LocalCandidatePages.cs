using System.Net;
using System.Text.Encodings.Web;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace AuthAspNetCoreDevAuthExample;

/// <summary>Maps the sample's read-only operator pages and independent, ready-only completion actions.</summary>
/// <remarks>
/// The host guards these routes independently of the DevAuth control endpoints. Each role requires an explicit
/// authorization policy; neither GET nor completion initializes fixtures. Only explicit persona selection does.
/// </remarks>
internal static class LocalCandidatePages
{
    /// <summary>Maps four local-only routes with role policies and no-store responses.</summary>
    internal static void MapLocalCandidatePages(this WebApplication app)
    {
        var group = app.MapGroup("/candidate");
        group.AddEndpointFilter(RequireLocalRequestAsync);
        group.MapGet("/label", (HttpContext context, LocalCandidateFixtureStore store) => Page(context, store.Read(), "labeler"))
            .RequireSurfacePolicy("LabelersOnly");
        group.MapGet("/review", (HttpContext context, LocalCandidateFixtureStore store) => Page(context, store.Read(), "reviewer"))
            .RequireSurfacePolicy("ReviewersOnly");
        group.MapPost("/label/complete", (HttpContext context, LocalCandidateFixtureStore store) => Complete(context, store, "labeler"))
            .RequireSurfacePolicy("LabelersOnly");
        group.MapPost("/review/complete", (HttpContext context, LocalCandidateFixtureStore store) => Complete(context, store, "reviewer"))
            .RequireSurfacePolicy("ReviewersOnly");
    }

    /// <summary>Rejects nonlocal or foreign-origin work before the host route can read or mutate the scenario.</summary>
    /// <remarks>The sample deliberately uses Development and loopback even if a host opts DevAuth into other environments.</remarks>
    private static async ValueTask<object?> RequireLocalRequestAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        var environment = http.RequestServices.GetRequiredService<IHostEnvironment>();
        if (!environment.IsDevelopment() || http.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (HttpMethods.IsPost(http.Request.Method) &&
            (string.Equals(http.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase) ||
             HasForeignOrigin(http.Request, "Origin") || HasForeignOrigin(http.Request, "Referer")))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }

    /// <summary>Checks every supplied origin/referrer against the exact scheme and authority of this local request.</summary>
    private static bool HasForeignOrigin(HttpRequest request, string header) =>
        request.Headers.TryGetValue(header, out var values) && values.Any(value =>
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Completes an existing ready stage, or returns the same read-only recovery page without ensuring data.</summary>
    private static IResult Complete(HttpContext context, LocalCandidateFixtureStore store, string personaId)
    {
        if (!store.TryComplete(personaId))
        {
            return Page(context, store.Read(), personaId);
        }

        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = Path(personaId);
        return Results.Empty;
    }

    /// <summary>Combines the package's visible identity marker with independently checked host fixture readiness.</summary>
    private static IResult Page(HttpContext context, LocalCandidateSnapshot? candidate, string personaId)
    {
        var marker = AppSurfaceDevAuthMarker.Render(context,
            context.RequestServices.GetRequiredService<IHostEnvironment>(),
            context.RequestServices.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>(),
            context.RequestServices.GetRequiredService<IDataProtectionProvider>(),
            options => options.AdditionalCssClass = "demo-dev-auth");
        return Results.Content(Render(marker, candidate, personaId), "text/html; charset=utf-8",
            statusCode: candidate?.IsReady(personaId) == true ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
    }

    private static string Path(string personaId) => personaId == "labeler" ? "/candidate/label" : "/candidate/review";

    /// <summary>Renders a coherent immutable state with encoded values and native role action or POST recovery.</summary>
    /// <param name="marker">Trusted HTML generated by the package-owned DevAuth marker.</param>
    /// <param name="candidate">An optional immutable snapshot. Null and unready snapshots expose only recovery.</param>
    /// <param name="personaId">A configured labeler or reviewer ID chosen by the route, never request input.</param>
    /// <remarks>The marker remains visible; identity alone does not establish fixture readiness.</remarks>
    internal static string Render(string marker, LocalCandidateSnapshot? candidate, string personaId)
    {
        var html = HtmlEncoder.Default;
        var labeler = personaId == "labeler";
        var heading = labeler ? "Label candidate" : "Review candidate";
        var ready = candidate?.IsReady(personaId) == true;
        var completed = ready && (labeler ? candidate!.LabelingCompleted : candidate!.ReviewCompleted);
        var action = ready
            ? completed ? "<p>Your work is completed.</p>" : $"<form method=\"post\" action=\"{Path(personaId)}/complete\"><button>Complete {(labeler ? "labeling" : "review")}</button></form>"
            : $"<form method=\"post\" action=\"/_appsurface/dev-auth/select/{html.Encode(personaId)}?returnUrl={Uri.EscapeDataString(Path(personaId))}\"><button>Reselect {html.Encode(personaId)}</button></form>";
        var state = ready
            ? $"<p>Candidate ID: <code data-candidate-id=\"{html.Encode(candidate!.Id)}\">{html.Encode(candidate.Id)}</code></p><p>Labeling: {(candidate.LabelingCompleted ? "completed" : "pending")}</p><p>Review: {(candidate.ReviewCompleted ? "completed" : "pending")}</p>"
            : "<p>Select this persona again to finish preparing its fixtures.</p>";
        return $$"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{heading}} · Local candidate proof</title>
            <style>
            :root{--background:#f8fafc;--text:#111827;--header:#0f172a;--link:#1d4ed8;--visited:#6b21a8;--focus:#1d4ed8}
            *{box-sizing:border-box}body{font-family:system-ui,-apple-system,Segoe UI,sans-serif;margin:0;font-size:16px;line-height:1.5;color:var(--text);background:var(--background)}
            header{padding:16px 32px;background:var(--header);color:#fff;font-weight:700}main{max-width:760px;padding:32px}
            h1{font-size:32px;line-height:1.25;margin:24px 0 8px}a{color:var(--link);text-decoration:underline}a:visited{color:var(--visited)}
            button{font:inherit;min-height:44px;padding:8px 16px;border:1px solid var(--text);background:#fff;color:var(--text);cursor:pointer}
            button:focus-visible,a:focus-visible{outline:3px solid var(--focus);outline-offset:3px}code{overflow-wrap:anywhere}nav{margin-top:24px}nav a{display:inline-block;min-height:44px;padding:10px 0}
            @media(max-width:640px){main{padding:16px}header{padding:16px}h1{font-size:28px}.demo-dev-auth{margin:12px 16px}
            }
            </style></head>
            <body><header>AppSurface local proof</header>{{marker}}<main>
            <h1>{{heading}}</h1><p><strong>Fixtures {{(ready ? "ready" : "not ready")}}</strong></p>
            {{state}}{{action}}
            <nav aria-label="Persona navigation"><a href="/_appsurface/dev-auth/">Change persona</a></nav>
            </main></body></html>
            """;
    }
}
