using System.Text;
using ForgeTrust.AppSurface.Core.Extensions;
using ForgeTrust.RazorWire.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// An <see cref="IActionResult"/> that renders and streams Turbo Stream actions to the response.
/// </summary>
public class RazorWireStreamResult : IActionResult
{
    private readonly IEnumerable<IRazorWireStreamAction> _actions;
    private readonly Controller? _controller;
    private readonly int? _statusCode;
    private readonly bool _formHandled;

    /// <summary>
    /// Initializes a new <see cref="RazorWireStreamResult"/> with the sequence of actions to render and an optional controller whose view context may be reused.
    /// </summary>
    /// <param name="actions">Sequence of IRazorWireStreamAction instances to render and stream as Turbo Stream HTML fragments.</param>
    /// <param name="controller">Optional controller whose ViewData and TempData will be reused during rendering; if null, fresh view and temp data are created.</param>
    /// <param name="statusCode">Optional HTTP status code to set before writing stream output.</param>
    /// <param name="formHandled">Whether this result contains server-rendered failed-form UI.</param>
    public RazorWireStreamResult(
        IEnumerable<IRazorWireStreamAction> actions,
        Controller? controller = null,
        int? statusCode = null,
        bool formHandled = false)
    {
        _actions = actions;
        _controller = controller;
        _statusCode = statusCode;
        _formHandled = formHandled;
    }

    /// <summary>
    /// Creates a <see cref="RazorWireStreamResult"/> that will stream the provided raw HTML as a single render action.
    /// </summary>
    /// <remarks>
    /// This constructor is a trusted whole-stream escape hatch. RazorWire writes <paramref name="rawContent"/> exactly as
    /// provided; it does not encode, sanitize, wrap, or validate the payload. Prefer
    /// <see cref="RazorWireStreamBuilder"/> when composing targeted stream actions, and encode user-supplied values before
    /// they are included in raw stream markup.
    /// </remarks>
    /// <param name="rawContent">Trusted raw Turbo Stream HTML to stream; if null, an empty string is used.</param>
    public RazorWireStreamResult(string? rawContent)
    {
        _actions = [new RawHtmlStreamAction(rawContent ?? string.Empty)];
    }

    /// <summary>
    /// Streams rendered Turbo Stream HTML for the configured actions to the HTTP response.
    /// </summary>
    /// <param name="context">The current action context used to build the view context and access the HTTP response.</param>
    /// <remarks>
    /// A dialog-containing result validates request correlation and sequentially renders the complete action snapshot
    /// before generating antiforgery tokens or setting response headers, so a later render failure cannot leave an
    /// earlier action partially applied. It then writes the buffered markup and echoes the validated request UUID in
    /// <c>X-RazorWire-Request</c>. A correlated page-only result attaches metadata to package-authored target actions
    /// and echoes that request UUID while retaining the existing ordered streaming renderer. An uncorrelated page-only
    /// result remains compatible with callers that do not send RazorWire correlation headers.
    /// </remarks>
    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var actions = _actions.ToArray();
        var dialogIndex = RazorWireStreamRendering.FindDialogCommandIndex(actions);
        var metadata = RazorWireRequestMetadata.Read(
            context.HttpContext.Request,
            required: dialogIndex >= 0);
        var response = context.HttpContext.Response;

        if (dialogIndex >= 0)
        {
            var dialogCommand = ((IRazorWireDialogCommandStreamAction)actions[dialogIndex]).Command;
            var dialogViewContext = CreateViewContext(context);
            var renderedActions = new string[actions.Length];
            var cancellationToken = context.HttpContext.RequestAborted;

            for (var index = 0; index < actions.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var phase = RazorWireStreamRendering.GetPhase(index, dialogIndex, dialogCommand);
                renderedActions[index] = await RazorWireStreamRendering.RenderActionAsync(
                    actions[index],
                    dialogViewContext,
                    metadata,
                    phase,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            PrepareResponse(context, metadata);
            await response.WriteAsync(
                string.Concat(renderedActions),
                Encoding.UTF8,
                cancellationToken);
            return;
        }

        PrepareResponse(context, metadata);

        var viewContext = CreateViewContext(context);

        await foreach (var html in actions.ParallelSelectAsyncEnumerable(
                           (action, ct) => RazorWireStreamRendering.RenderActionAsync(
                               action,
                               viewContext,
                               metadata,
                               RazorWireDialogPhase.Origin,
                               ct),
                           maxDegreeOfParallelism: 64,
                           cancellationToken: context.HttpContext.RequestAborted))
        {
            await response.WriteAsync(html, Encoding.UTF8, context.HttpContext.RequestAborted);
        }
    }

    private void PrepareResponse(ActionContext context, RazorWireRequestMetadata? metadata)
    {
        var services = context.HttpContext.RequestServices;

        // Generate antiforgery tokens before writing any response bytes so required cookies can still be set.
        var antiforgery = services.GetService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>();
        if (antiforgery is not null)
        {
            _ = antiforgery.GetAndStoreTokens(context.HttpContext);
        }

        var response = context.HttpContext.Response;
        if (_statusCode is not null)
        {
            response.StatusCode = _statusCode.Value;
        }

        if (_formHandled)
        {
            response.Headers[RazorWireFormHeaders.FormHandled] = "true";
        }

        if (metadata is not null)
        {
            response.Headers[RazorWireRequestMetadata.ResponseRequestHeaderName] =
                metadata.RequestId.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        }

        response.ContentType = "text/vnd.turbo-stream.html";
    }

    /// <summary>
    /// Creates a <see cref="ViewContext"/> configured for rendering the stream actions, optionally inheriting ViewData and TempData from the associated controller.
    /// </summary>
    /// <param name="actionContext">The current ActionContext used to build the ViewContext.</param>
    /// <returns>A ViewContext configured with a <see cref="NullView"/>, the prepared ViewData, TempData (the controller's if available or obtained from ITempDataDictionaryFactory), TextWriter.Null, and default HtmlHelperOptions.</returns>
    private ViewContext CreateViewContext(ActionContext actionContext)
    {
        var services = actionContext.HttpContext.RequestServices;

        ViewDataDictionary viewData;
        ITempDataDictionary? tempData = null;

        var tempDataProvider = services.GetRequiredService<ITempDataDictionaryFactory>();

        // If we have a controller instance, inherit its ViewData and TempData
        if (_controller != null)
        {
            viewData = new ViewDataDictionary(_controller.ViewData);
            tempData = _controller.TempData;
        }
        else
        {
            viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), actionContext.ModelState);
        }

        return new ViewContext(
            actionContext,
            new NullView(),
            viewData,
            tempData ?? tempDataProvider.GetTempData(actionContext.HttpContext),
            TextWriter.Null,
            new HtmlHelperOptions()
        );
    }

    private class RawHtmlStreamAction : IRazorWireStreamAction
    {
        private readonly string _html;

        /// <summary>
        /// Initializes a new <see cref="RawHtmlStreamAction"/> that will return the specified HTML when rendered.
        /// </summary>
        /// <param name="html">HTML content that RenderAsync will return (can be empty).</param>
        public RawHtmlStreamAction(string html)
        {
            _html = html;
        }

        /// <summary>
        /// Produces the stored raw HTML as the rendered output for the given view context.
        /// </summary>
        /// <param name="viewContext">The view rendering context supplied to the action.</param>
        /// <param name="cancellationToken">Cancellation token (ignored for raw HTML).</param>
        /// <returns>The stored HTML string.</returns>
        public Task<string> RenderAsync(ViewContext viewContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(_html);
    }

    private class NullView : IView
    {
        public string Path => string.Empty;

        /// <summary>
        /// Performs no rendering and completes immediately.
        /// </summary>
        /// <param name="viewContext">The rendering context provided by the framework; this implementation ignores it.</param>
        /// <returns>A completed <see cref="Task"/> representing the finished render operation.</returns>
        public Task RenderAsync(ViewContext viewContext) => Task.CompletedTask;
    }
}
