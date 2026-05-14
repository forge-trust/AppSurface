using System.Net.Mime;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Forms;

/// <summary>
/// Converts RazorWire form anti-forgery validation failures into handled responses that Turbo can render in-place.
/// </summary>
/// <remarks>
/// The filter runs late in MVC result execution so it can see <see cref="IAntiforgeryValidationFailedResult"/> results
/// produced by MVC anti-forgery validation. It only rewrites requests classified as RazorWire forms, sets
/// <see cref="RazorWireFormHeaders.FormHandled"/>, and chooses Turbo Stream, HTML, or plain-text output from the
/// request's <c>Accept</c> header. Turbo Stream responses prefer a form-local target when the posted marker can be read
/// within the configured safety limit; otherwise they append one safe diagnostic block to <c>body</c>.
/// </remarks>
internal sealed class RazorWireAntiforgeryFailureFilter : IAsyncAlwaysRunResultFilter, IOrderedFilter
{
    /// <summary>
    /// Turbo Stream MIME type used by Turbo Drive form submissions.
    /// </summary>
    private const string TurboStreamContentType = "text/vnd.turbo-stream.html";

    /// <summary>
    /// Maximum URL-encoded form body size the filter will read to discover a local failure target.
    /// </summary>
    private const long MaxFailureTargetFormBytes = 64 * 1024;

    private readonly RazorWireOptions _options;
    private readonly RazorWireFormRequestClassifier _classifier;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<RazorWireAntiforgeryFailureFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RazorWireAntiforgeryFailureFilter"/> class.
    /// </summary>
    /// <param name="options">RazorWire options that gate failed-form UX and development diagnostics.</param>
    /// <param name="classifier">Classifier used to confirm that the failed request came from a RazorWire-enhanced form.</param>
    /// <param name="environment">Current hosting environment used to limit detailed diagnostics to development.</param>
    /// <param name="logger">Logger used for structured anti-forgery failure events.</param>
    public RazorWireAntiforgeryFailureFilter(
        RazorWireOptions options,
        RazorWireFormRequestClassifier classifier,
        IWebHostEnvironment environment,
        ILogger<RazorWireAntiforgeryFailureFilter> logger)
    {
        _options = options;
        _classifier = classifier;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Gets the filter order. The value runs after normal MVC result filters while still leaving a small buffer for
    /// application filters that intentionally need to run later.
    /// </summary>
    public int Order => int.MaxValue - 100;

    /// <summary>
    /// Rewrites RazorWire anti-forgery validation failures into handled form responses, then continues result execution.
    /// </summary>
    /// <param name="context">The MVC result-executing context.</param>
    /// <param name="next">Delegate that continues MVC result execution.</param>
    /// <returns>A task that completes when the result pipeline has continued.</returns>
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is IAntiforgeryValidationFailedResult
            && _options.Forms.EnableFailureUx
            && await _classifier.IsRazorWireFormRequestAsync(context.HttpContext.Request, context.HttpContext.RequestAborted))
        {
            await RewriteAntiforgeryFailureAsync(context);
        }

        await next();
    }

    private async Task RewriteAntiforgeryFailureAsync(ResultExecutingContext context)
    {
        var request = context.HttpContext.Request;
        var useDiagnostics = _environment.IsDevelopment() && _options.Forms.EnableDevelopmentDiagnostics;
        var responseKind = SelectResponseKind(request);
        var message = BuildMessage(useDiagnostics);
        var turboStreamTarget = await ResolveTurboStreamTargetAsync(request, context.HttpContext.RequestAborted);

        context.HttpContext.Response.Headers[RazorWireFormHeaders.FormHandled] = "true";
        context.Result = responseKind switch
        {
            RazorWireAntiforgeryResponseKind.TurboStream => new ContentResult
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentType = TurboStreamContentType,
                Content = BuildTurboStream(message, turboStreamTarget)
            },
            RazorWireAntiforgeryResponseKind.Html => new ContentResult
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentType = MediaTypeNames.Text.Html,
                Content = BuildHtml(message)
            },
            _ => new ContentResult
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentType = MediaTypeNames.Text.Plain,
                Content = message.ToPlainText()
            }
        };

        _logger.LogInformation(
            RazorWireEventIds.AntiforgeryValidationFailed,
            "RazorWire form antiforgery validation failed. StatusCode: {StatusCode}. Environment: {Environment}. TurboStreamRequested: {TurboStreamRequested}.",
            StatusCodes.Status400BadRequest,
            _environment.EnvironmentName,
            responseKind == RazorWireAntiforgeryResponseKind.TurboStream);
    }

    private static RazorWireAntiforgeryResponseKind SelectResponseKind(HttpRequest request)
    {
        var typedHeaders = request.GetTypedHeaders();
        var accept = typedHeaders.Accept;
        if (accept is null || accept.Count == 0)
        {
            return RazorWireAntiforgeryResponseKind.Html;
        }

        foreach (var value in accept)
        {
            var mediaType = value.MediaType.Value;
            if (string.Equals(mediaType, TurboStreamContentType, StringComparison.OrdinalIgnoreCase)
                && (value.Quality is null || value.Quality > 0))
            {
                return RazorWireAntiforgeryResponseKind.TurboStream;
            }
        }

        foreach (var value in accept)
        {
            var mediaType = value.MediaType.Value;
            if (value.Quality == 0)
            {
                continue;
            }

            if (string.Equals(mediaType, MediaTypeNames.Text.Html, StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "*/*", StringComparison.OrdinalIgnoreCase))
            {
                return RazorWireAntiforgeryResponseKind.Html;
            }
        }

        return RazorWireAntiforgeryResponseKind.PlainText;
    }

    private static RazorWireFormFailureMessage BuildMessage(bool useDiagnostics)
    {
        if (!useDiagnostics)
        {
            return new RazorWireFormFailureMessage(
                "We could not submit this form.",
                "Refresh the page and try again.",
                null,
                []);
        }

        return new RazorWireFormFailureMessage(
            "Antiforgery token validation failed",
            "This RazorWire form posted without a valid __RequestVerificationToken.",
            "The token may be missing or stale after a partial form update.",
            [
                "Render the whole <form> with ReplacePartial when replacing a form.",
                "Include @Html.AntiForgeryToken() in updated form contents.",
                "Avoid UpdatePartial on only the inner HTML of a form when the token must refresh.",
                "Review the RazorWire anti-forgery guide for token-refresh patterns before partial form updates."
            ]);
    }

    private static async ValueTask<RazorWireTurboStreamTarget> ResolveTurboStreamTargetAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var form = request.HttpContext.Features.Get<IFormFeature>()?.Form;
        if (RazorWireFormFailureTarget.TryNormalizeIdTarget(form?[RazorWireFormFields.FailureTarget].ToString(), out var normalizedFailureTarget))
        {
            return RazorWireTurboStreamTarget.ById(normalizedFailureTarget);
        }

        if (!CanReadFailureTargetForm(request))
        {
            return RazorWireTurboStreamTarget.BySelector("body");
        }

        try
        {
            form = await request.ReadFormAsync(cancellationToken);
            if (RazorWireFormFailureTarget.TryNormalizeIdTarget(form[RazorWireFormFields.FailureTarget].ToString(), out normalizedFailureTarget))
            {
                return RazorWireTurboStreamTarget.ById(normalizedFailureTarget);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException
                                      or IOException
                                      or InvalidOperationException
                                      or BadHttpRequestException)
        {
            return RazorWireTurboStreamTarget.BySelector("body");
        }

        return RazorWireTurboStreamTarget.BySelector("body");
    }

    private static bool CanReadFailureTargetForm(HttpRequest request)
    {
        if (!request.HasFormContentType)
        {
            return false;
        }

        var contentType = request.ContentType ?? string.Empty;
        if (!contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return request.ContentLength is long length
               && length <= MaxFailureTargetFormBytes;
    }

    private static string BuildTurboStream(RazorWireFormFailureMessage message, RazorWireTurboStreamTarget target)
    {
        var html = BuildHtml(message);
        if (target.AttributeName == "target")
        {
            return "<turbo-stream action=\"update\" "
                   + target.ToAttributeHtml()
                   + "><template>"
                   + html
                   + "</template></turbo-stream>";
        }

        return "<turbo-stream action=\"remove\" targets=\"[data-rw-form-error-generated=true][data-rw-form-error-kind=antiforgery]\"></turbo-stream>"
               + "<turbo-stream action=\"append\" "
               + target.ToAttributeHtml()
               + "><template>"
               + html
               + "</template></turbo-stream>";
    }

    private static string BuildHtml(RazorWireFormFailureMessage message)
    {
        var encodedTitle = HtmlEncoder.Default.Encode(message.Title);
        var encodedMessage = HtmlEncoder.Default.Encode(message.Message);
        var encodedDetail = message.Detail is null ? null : HtmlEncoder.Default.Encode(message.Detail);
        var hints = string.Join(
            string.Empty,
            message.Hints.Select(hint => $"<li>{HtmlEncoder.Default.Encode(hint)}</li>"));

        return $"""
                <div data-rw-form-error-generated="true" data-rw-form-error-kind="antiforgery" role="alert" aria-live="assertive" tabindex="-1">
                  <strong data-rw-form-error-title="true">{encodedTitle}</strong>
                  <p data-rw-form-error-message="true">{encodedMessage}</p>
                  {(encodedDetail is null ? string.Empty : $"<p data-rw-form-error-detail=\"true\">{encodedDetail}</p>")}
                  {(message.Hints.Count == 0 ? string.Empty : $"<ul data-rw-form-error-hints=\"true\">{hints}</ul>")}
                </div>
                """;
    }

    private enum RazorWireAntiforgeryResponseKind
    {
        TurboStream,
        Html,
        PlainText
    }

    private sealed record RazorWireTurboStreamTarget(string AttributeName, string Value)
    {
        public static RazorWireTurboStreamTarget ById(string id)
        {
            return new RazorWireTurboStreamTarget("target", id);
        }

        public static RazorWireTurboStreamTarget BySelector(string selector)
        {
            return new RazorWireTurboStreamTarget("targets", selector);
        }

        public string ToAttributeHtml()
        {
            return $"{AttributeName}=\"{HtmlEncoder.Default.Encode(Value)}\"";
        }
    }

    private sealed record RazorWireFormFailureMessage(
        string Title,
        string Message,
        string? Detail,
        IReadOnlyList<string> Hints)
    {
        public string ToPlainText()
        {
            var parts = new List<string> { Title, Message };
            if (!string.IsNullOrWhiteSpace(Detail))
            {
                parts.Add(Detail);
            }

            parts.AddRange(Hints);

            return string.Join(Environment.NewLine, parts);
        }
    }
}
