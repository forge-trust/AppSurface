using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Renders an explicit AppSurface DevAuth state marker for host pages.
/// </summary>
/// <remarks>
/// Use this helper from pages or layouts where the selected fake persona should stay visible while navigating the app.
/// The returned HTML contains only safe persona display values and POST-only controls that target the mapped DevAuth
/// endpoints. DevAuth does not automatically inject this marker into host output, and this helper returns an empty
/// string when the current environment is not in <see cref="AppSurfaceDevAuthOptions.AllowedEnvironmentNames"/>.
/// With default styles, the marker is a fixed bottom-right overlay above 640 CSS pixels and participates in normal
/// document flow at widths up to and including 640 CSS pixels. The host owns the viewport metadata, render location,
/// outer spacing, and any containing layout; render the marker after persistent application chrome and before main
/// content so the narrow-screen marker reserves space instead of covering that content.
/// </remarks>
public static class AppSurfaceDevAuthMarker
{
    /// <summary>
    /// Renders a local DevAuth marker using the current request state.
    /// </summary>
    /// <param name="httpContext">Current HTTP context, used to read the protected persona cookie and build return URLs.</param>
    /// <param name="environment">Host environment used for marker status.</param>
    /// <param name="options">Configured DevAuth options.</param>
    /// <param name="dataProtectionProvider">Data protection provider used to read the selected persona cookie.</param>
    /// <param name="configure">Optional marker rendering customization.</param>
    /// <returns>
    /// HTML that can be embedded in a host page, or an empty string when the current environment is not allowed.
    /// With default styles, the returned marker is fixed above 640 CSS pixels and in flow at 640 CSS pixels or below.
    /// </returns>
    public static string Render(
        HttpContext httpContext,
        IHostEnvironment environment,
        IOptions<AppSurfaceDevAuthOptions> options,
        IDataProtectionProvider dataProtectionProvider,
        Action<AppSurfaceDevAuthMarkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        var markerOptions = new AppSurfaceDevAuthMarkerOptions();
        configure?.Invoke(markerOptions);
        var classPrefix = NormalizeCssClassPrefix(markerOptions.CssClassPrefix);
        var returnUrl = AppSurfaceDevAuthEndpointRouteBuilderExtensions.NormalizeLocalReturnUrl(
            markerOptions.ReturnUrl ?? CreateCurrentReturnUrl(httpContext));
        var devAuthOptions = options.Value;
        var status = AppSurfaceDevAuthEndpointRouteBuilderExtensions.BuildStatus(
            httpContext,
            environment,
            devAuthOptions,
            dataProtectionProvider);

        if (!status.Enabled)
        {
            return string.Empty;
        }

        return RenderMarker(status, devAuthOptions, markerOptions, classPrefix, returnUrl);
    }

    private static string RenderMarker(
        AppSurfaceDevAuthStatus status,
        AppSurfaceDevAuthOptions options,
        AppSurfaceDevAuthMarkerOptions markerOptions,
        string classPrefix,
        string returnUrl)
    {
        var html = HtmlEncoder.Default;
        var additionalCssClass = NormalizeCssClassList(markerOptions.AdditionalCssClass);
        var rootClasses = string.IsNullOrWhiteSpace(additionalCssClass)
            ? classPrefix
            : classPrefix + " " + additionalCssClass;
        var builder = new StringBuilder();

        if (markerOptions.IncludeDefaultStyles)
        {
            builder.AppendLine("<style>");
            builder.AppendLine($".{classPrefix}{{position:fixed;right:16px;bottom:16px;z-index:2147483647;max-width:min(360px,calc(100vw - 32px));font:13px/1.35 system-ui,-apple-system,Segoe UI,sans-serif;color:#111827;background:#fff;border:2px solid #b91c1c;box-shadow:0 16px 40px rgba(15,23,42,.22)}}.{classPrefix} *{{box-sizing:border-box}}.{classPrefix}__summary{{display:flex;justify-content:space-between;gap:12px;align-items:center;list-style:none;cursor:pointer;padding:10px 12px}}.{classPrefix}__summary::-webkit-details-marker{{display:none}}.{classPrefix}__summary::marker{{content:\"\"}}.{classPrefix}__badge{{font-weight:800;color:#b91c1c;letter-spacing:0}}.{classPrefix}__name{{font-weight:700;text-align:right;min-width:0;overflow-wrap:anywhere}}.{classPrefix}__toggle{{border:1px solid #cbd5e1;background:#f8fafc;color:#111827;display:inline-flex;align-items:center;justify-content:center;inline-size:22px;block-size:22px;flex:0 0 auto;font-weight:800}}.{classPrefix}__toggle::before{{content:\"+\"}}.{classPrefix}__details[open] .{classPrefix}__toggle::before{{content:\"-\"}}.{classPrefix}__body{{border-top:1px solid #e2e8f0;padding:10px 12px 12px}}.{classPrefix}__meta{{display:grid;grid-template-columns:auto 1fr;gap:4px 8px;margin:0 0 10px}}.{classPrefix}__meta dt{{font-weight:700}}.{classPrefix}__meta dd{{margin:0;min-width:0;overflow-wrap:anywhere}}.{classPrefix}__actions{{display:flex;flex-wrap:wrap;gap:6px;margin-bottom:8px}}.{classPrefix}__actions form{{margin:0}}.{classPrefix}__button{{border:1px solid #334155;background:#f8fafc;color:#111827;padding:6px 9px;font:inherit;cursor:pointer}}.{classPrefix}__button[aria-current=true]{{border-color:#166534;background:#dcfce7;font-weight:700}}.{classPrefix}__links{{display:flex;gap:10px;flex-wrap:wrap}}.{classPrefix}__links a{{color:#1d4ed8}}@media(max-width:640px){{.{classPrefix}{{position:static;right:auto;bottom:auto;z-index:auto;max-width:none;box-shadow:none}}.{classPrefix}__actions form{{min-width:0;max-width:100%}}.{classPrefix}__button{{min-width:0;max-width:100%;overflow-wrap:anywhere}}}}");
            builder.AppendLine("</style>");
        }

        builder.AppendLine($"<aside class=\"{rootClasses}\" {AppSurfaceDevAuthStaticExportMarkers.MarkerAttributeName}=\"marker\" aria-label=\"AppSurface development authentication state\">");
        builder.AppendLine($"<details class=\"{classPrefix}__details\"{(markerOptions.StartExpanded ? " open" : string.Empty)}>");
        builder.AppendLine($"<summary class=\"{classPrefix}__summary\"><span class=\"{classPrefix}__badge\">DEV AUTH</span><span class=\"{classPrefix}__name\">{html.Encode(AppSurfaceDevAuthEndpointRouteBuilderExtensions.DisplayStatusPersonaName(status))}</span><span class=\"{classPrefix}__toggle\" aria-hidden=\"true\"></span></summary>");
        builder.AppendLine($"<div class=\"{classPrefix}__body\">");
        builder.AppendLine($"<dl class=\"{classPrefix}__meta\">");
        builder.AppendLine($"<dt>Scheme</dt><dd><code>{html.Encode(status.Scheme)}</code></dd>");
        builder.AppendLine($"<dt>Subject</dt><dd><code>{html.Encode(AppSurfaceDevAuthEndpointRouteBuilderExtensions.DisplayStatusSubject(status))}</code></dd>");
        builder.AppendLine("</dl>");

        if (markerOptions.ShowPersonaControls)
        {
            builder.AppendLine($"<div class=\"{classPrefix}__actions\" aria-label=\"Select DevAuth persona\">");
            foreach (var persona in options.Users.Personas.Values)
            {
                var selected = string.Equals(status.PersonaId, persona.Id, StringComparison.Ordinal);
                var action = AppSurfaceDevAuthEndpointRouteBuilderExtensions.BuildMutationUrl(
                    options.PathPrefix,
                    "select/" + persona.Id,
                    returnUrl);
                builder.AppendLine($"<form method=\"post\" action=\"{html.Encode(action)}\"><button class=\"{classPrefix}__button\" aria-current=\"{(selected ? "true" : "false")}\">{html.Encode(AppSurfaceDevAuthEndpointRouteBuilderExtensions.DisplayPersonaName(persona))}</button></form>");
            }

            var clearAction = AppSurfaceDevAuthEndpointRouteBuilderExtensions.BuildMutationUrl(
                options.PathPrefix,
                "clear",
                returnUrl);
            builder.AppendLine($"<form method=\"post\" action=\"{html.Encode(clearAction)}\"><button class=\"{classPrefix}__button\">Clear</button></form>");
            builder.AppendLine("</div>");
        }

        builder.AppendLine($"<div class=\"{classPrefix}__links\"><a href=\"{html.Encode(options.PathPrefix)}/\">Open persona lab</a><a href=\"{html.Encode(options.PathPrefix)}/status\">Status JSON</a></div>");
        builder.AppendLine("</div>");
        builder.AppendLine("</details>");
        builder.AppendLine("</aside>");
        return builder.ToString();
    }

    private static string NormalizeCssClassPrefix(string value)
    {
        var token = NormalizeCssClassToken(value);
        return string.IsNullOrWhiteSpace(token) ? AppSurfaceDevAuthStaticExportMarkers.MarkerCssClass : token;
    }

    private static string NormalizeCssClassList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeCssClassToken)
                .Where(token => !string.IsNullOrWhiteSpace(token)));
    }

    private static string NormalizeCssClassToken(string value)
    {
        return new string(value.Where(IsCssClassTokenCharacter).ToArray());
    }

    private static bool IsCssClassTokenCharacter(char value)
    {
        return char.IsAsciiLetterOrDigit(value) || value is '-' or '_';
    }

    private static string CreateCurrentReturnUrl(HttpContext httpContext)
    {
        return httpContext.Request.PathBase + httpContext.Request.Path + httpContext.Request.QueryString;
    }

}
