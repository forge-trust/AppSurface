using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.RazorWire.TagHelpers;

/// <summary>
/// Tag helper for rendering the necessary RazorWire scripts.
/// </summary>
[HtmlTargetElement("rw:scripts")]
public class RazorWireScriptsTagHelper : TagHelper
{
    private readonly IFileVersionProvider _fileVersionProvider;
    private readonly RazorWireOptions _options;
    private readonly IWebHostEnvironment? _environment;

    /// <summary>
    /// Initializes a new instance of the <see cref="RazorWireScriptsTagHelper"/> class.
    /// </summary>
    /// <param name="fileVersionProvider">The file version provider used to append version hashes to script paths.</param>
    /// <param name="options">RazorWire options used to emit runtime configuration.</param>
    /// <param name="environment">The current web host environment.</param>
    public RazorWireScriptsTagHelper(
        IFileVersionProvider fileVersionProvider,
        RazorWireOptions? options = null,
        IWebHostEnvironment? environment = null)
    {
        _fileVersionProvider = fileVersionProvider ?? throw new ArgumentNullException(nameof(fileVersionProvider));
        _options = options ?? RazorWireOptions.Default;
        _environment = environment;
    }

    /// <summary>
    /// Gets or sets the view context.
    /// </summary>
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    /// <summary>
    /// Gets or sets a value indicating whether the opt-in page-navigation runtime should be rendered after the core runtime.
    /// </summary>
    /// <remarks>
    /// Page navigation is split out of the core runtime to keep the default startup path lightweight. Set this attribute
    /// to <c>true</c> on layouts or pages that render <c>rw-page-nav</c> / <c>data-rw-page-nav</c> markup.
    /// </remarks>
    [HtmlAttributeName("page-navigation")]
    public bool PageNavigation { get; set; }

    /// <summary>
    /// Renders the client-side script tags required by RazorWire and removes the wrapper element so no enclosing tag is emitted.
    /// </summary>
    /// <param name="context">The current tag helper context.</param>
    /// <param name="output">The tag helper output that will be modified to contain the script elements and have no wrapper tag.</param>
    /// <remarks>
    /// The generated runtime script includes data attributes for form failure UX, development diagnostics, split-origin
    /// live origin, hybrid credential behavior, and the lazy anti-forgery token endpoint. The helper normalizes
    /// <see cref="RazorWireOptions.Hybrid"/>.<see cref="RazorWireHybridOptions.LiveOrigin"/> before emitting it so the
    /// browser receives only an origin, never a path-bearing URL. The anti-forgery endpoint is emitted relative to the
    /// current request path base so applications mounted under a virtual directory refresh tokens from the live app path.
    /// Use
    /// <see cref="RazorWireHybridCredentialsMode.Auto"/> to include credentials automatically when a live origin is
    /// configured; use explicit include or omit only when the live endpoint contract requires it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="RazorWireOptions.Hybrid"/>.<see cref="RazorWireHybridOptions.LiveOrigin"/> is not an
    /// absolute HTTP(S) origin or includes a path, query string, fragment, or userinfo.
    /// </exception>
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null; // No wrapper tag

        var pathBase = ViewContext.HttpContext.Request.PathBase;

        var razorwireJs = _fileVersionProvider.AddFileVersionToPath(
            pathBase,
            "/_content/ForgeTrust.RazorWire/razorwire/razorwire.js");
        var islandsJs = _fileVersionProvider.AddFileVersionToPath(
            pathBase,
            "/_content/ForgeTrust.RazorWire/razorwire/razorwire.islands.js");
        var pageNavigationJs = PageNavigation
            ? _fileVersionProvider.AddFileVersionToPath(
                pathBase,
                "/_content/ForgeTrust.RazorWire/razorwire/page-navigation.js")
            : null;

        var diagnosticsEnabled = _options.Forms.EnableFailureUx
                                 && _options.Forms.EnableDevelopmentDiagnostics
                                 && _environment?.IsDevelopment() == true;
        var failureMode = _options.Forms.EnableFailureUx
            ? _options.Forms.FailureMode.ToString().ToLowerInvariant()
            : "off";
        var failureUxEnabled = _options.Forms.EnableFailureUx.ToString().ToLowerInvariant();
        var defaultFailureMessage = HtmlEncoder.Default.Encode(_options.Forms.DefaultFailureMessage);
        var normalizedLiveOrigin = NormalizeOriginOrThrow(
            _options.Hybrid.LiveOrigin,
            "RazorWireOptions.Hybrid.LiveOrigin");
        var liveOrigin = HtmlEncoder.Default.Encode(normalizedLiveOrigin ?? string.Empty);
        var credentialsMode = ResolveHybridCredentialsAttribute(_options, normalizedLiveOrigin);
        var antiforgeryEndpoint = HtmlEncoder.Default.Encode(
            pathBase.Add(new PathString(_options.Forms.Antiforgery.TokenEndpointPath)).Value!);

        // This includes Turbo.js and the custom RazorWire island loader.
        var scripts = $@"
<script src=""https://cdn.jsdelivr.net/npm/@hotwired/turbo@8.0.12/dist/turbo.es2017-umd.js"" integrity=""sha256-1evN/OxCRDJtuVCzQ3gklVq8LzN6qhCm7x/sbawknOk="" crossorigin=""anonymous""></script>
<script src=""{razorwireJs}"" data-rw-development-diagnostics=""{diagnosticsEnabled.ToString().ToLowerInvariant()}"" data-rw-form-failure-enabled=""{failureUxEnabled}"" data-rw-form-failure-mode=""{failureMode}"" data-rw-default-failure-message=""{defaultFailureMessage}"" data-rw-live-origin=""{liveOrigin}"" data-rw-hybrid-credentials=""{credentialsMode}"" data-rw-antiforgery-endpoint=""{antiforgeryEndpoint}""></script>
<script src=""{islandsJs}""></script>
";

        if (pageNavigationJs is not null)
        {
            scripts += $@"<script src=""{pageNavigationJs}""></script>
";
        }

        output.Content.SetHtmlContent(scripts);
    }

    private static string ResolveHybridCredentialsAttribute(RazorWireOptions options, string? normalizedLiveOrigin)
    {
        return options.Hybrid.CredentialsMode is RazorWireHybridCredentialsMode.Include
               || (options.Hybrid.CredentialsMode is RazorWireHybridCredentialsMode.Auto
                   && !string.IsNullOrWhiteSpace(normalizedLiveOrigin))
            ? "include"
            : "omit";
    }

    private static string? NormalizeOriginOrThrow(string? origin, string optionName)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        var trimmedOrigin = origin.Trim();
        if (!Uri.TryCreate(trimmedOrigin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath.Trim('/') is { Length: > 0 })
        {
            throw new InvalidOperationException(
                $"{optionName} must be an absolute http or https origin such as 'https://api.example.com'. " +
                "Configure only the origin; paths, query strings, fragments, userinfo, and unsupported schemes are not supported.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
