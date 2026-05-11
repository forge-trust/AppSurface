using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Hosting;
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
    /// Renders the client-side script tags required by RazorWire and removes the wrapper element so no enclosing tag is emitted.
    /// </summary>
    /// <param name="context">The current tag helper context.</param>
    /// <param name="output">The tag helper output that will be modified to contain the script elements and have no wrapper tag.</param>
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

        var diagnosticsEnabled = _options.Forms.EnableFailureUx
                                 && _options.Forms.EnableDevelopmentDiagnostics
                                 && _environment?.IsDevelopment() == true;
        var failureMode = _options.Forms.EnableFailureUx
            ? _options.Forms.FailureMode.ToString().ToLowerInvariant()
            : "off";
        var failureUxEnabled = _options.Forms.EnableFailureUx.ToString().ToLowerInvariant();
        var defaultFailureMessage = HtmlEncoder.Default.Encode(_options.Forms.DefaultFailureMessage);

        // This includes Turbo.js and the custom RazorWire island loader.
        output.Content.SetHtmlContent(
            $@"
<script src=""https://cdn.jsdelivr.net/npm/@hotwired/turbo@8.0.12/dist/turbo.es2017-umd.js"" integrity=""sha256-1evN/OxCRDJtuVCzQ3gklVq8LzN6qhCm7x/sbawknOk="" crossorigin=""anonymous""></script>
<script src=""{razorwireJs}"" data-rw-development-diagnostics=""{diagnosticsEnabled.ToString().ToLowerInvariant()}"" data-rw-form-failure-enabled=""{failureUxEnabled}"" data-rw-form-failure-mode=""{failureMode}"" data-rw-default-failure-message=""{defaultFailureMessage}""></script>
<script src=""{islandsJs}""></script>
");
    }
}
