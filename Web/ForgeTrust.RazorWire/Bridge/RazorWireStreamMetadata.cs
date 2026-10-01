using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Primitives;

namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// Identifies which dialog flow a package-authored target action belongs to in one response.
/// </summary>
internal enum RazorWireDialogPhase
{
    /// <summary>The action belongs to the flow that originated the request, or to the page outside a dialog.</summary>
    Origin,

    /// <summary>The action follows an open command and belongs to the dialog opened by that command.</summary>
    New,

    /// <summary>The action follows a close command and must not target a dialog flow.</summary>
    Closed
}

/// <summary>
/// Describes the validated request correlation values echoed on package-authored stream elements.
/// </summary>
internal sealed record RazorWireRequestMetadata(Guid RequestId, long Order, Guid? FlowId)
{
    internal const string DocumentationPath = "Docs/dialog-responses.md";
    internal const string RequestHeaderName = "X-RazorWire-Request";
    internal const string ResponseRequestHeaderName = "X-RazorWire-Request";
    private const string OrderHeader = "X-RazorWire-Order";
    private const string FlowHeader = "X-RazorWire-Flow";
    private const long MaximumSafeJavaScriptInteger = 9_007_199_254_740_991;

    /// <summary>
    /// Reads and validates correlation values from a request.
    /// </summary>
    /// <param name="request">The request whose correlation headers are inspected.</param>
    /// <param name="required">Whether request and order values are required even when all headers are absent.</param>
    /// <returns>The validated metadata, or <see langword="null"/> when no correlation headers were supplied and metadata is optional.</returns>
    /// <exception cref="InvalidOperationException">Thrown when required metadata is missing or any supplied header is duplicated, oversized, or malformed.</exception>
    public static RazorWireRequestMetadata? Read(HttpRequest request, bool required)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hasRequest = request.Headers.ContainsKey(RequestHeaderName);
        var hasOrder = request.Headers.ContainsKey(OrderHeader);
        var hasFlow = request.Headers.ContainsKey(FlowHeader);
        if (!hasRequest && !hasOrder && !hasFlow)
        {
            if (required)
            {
                throw CreateMetadataException("request correlation headers are required for a dialog command");
            }

            return null;
        }

        var requestIdText = ReadSingleHeader(request.Headers, RequestHeaderName, required: true, maximumLength: 36);
        var orderText = ReadSingleHeader(request.Headers, OrderHeader, required: true, maximumLength: 16);
        var flowIdText = ReadSingleHeader(request.Headers, FlowHeader, required: false, maximumLength: 36);

        if (!Guid.TryParseExact(requestIdText, "D", out var requestId))
        {
            throw CreateMetadataException($"{RequestHeaderName} must contain one UUID");
        }

        if (!long.TryParse(orderText, NumberStyles.None, CultureInfo.InvariantCulture, out var order)
            || order <= 0
            || order > MaximumSafeJavaScriptInteger)
        {
            throw CreateMetadataException($"{OrderHeader} must be a positive safe integer");
        }

        Guid? flowId = null;
        if (flowIdText is not null)
        {
            if (!Guid.TryParseExact(flowIdText, "D", out var parsedFlowId))
            {
                throw CreateMetadataException($"{FlowHeader} must contain one UUID when supplied");
            }

            flowId = parsedFlowId;
        }

        return new RazorWireRequestMetadata(requestId, order, flowId);
    }

    /// <summary>
    /// Creates encoded HTML attributes for a stream element using this request metadata and the action's position.
    /// </summary>
    /// <param name="phase">The resolved dialog phase for the action.</param>
    /// <returns>A space-prefixed string of encoded <c>data-rw-*</c> attributes.</returns>
    public string ToHtmlAttributes(RazorWireDialogPhase phase)
    {
        var phaseValue = phase switch
        {
            RazorWireDialogPhase.Origin => "origin",
            RazorWireDialogPhase.New => "new",
            RazorWireDialogPhase.Closed => "closed",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unsupported RazorWire dialog phase.")
        };

        var attributes = new StringBuilder()
            .Append(" data-rw-request=\"")
            .Append(HtmlEncoder.Default.Encode(RequestId.ToString("D", CultureInfo.InvariantCulture)))
            .Append("\" data-rw-order=\"")
            .Append(Order.ToString(CultureInfo.InvariantCulture))
            .Append('\"');

        if (FlowId is Guid flowId)
        {
            attributes.Append(" data-rw-flow=\"")
                .Append(HtmlEncoder.Default.Encode(flowId.ToString("D", CultureInfo.InvariantCulture)))
                .Append('\"');
        }

        attributes.Append(" data-rw-dialog-phase=\"")
            .Append(phaseValue)
            .Append('\"');

        return attributes.ToString();
    }

    private static string? ReadSingleHeader(
        IHeaderDictionary headers,
        string name,
        bool required,
        int maximumLength)
    {
        if (!headers.TryGetValue(name, out StringValues values))
        {
            if (required)
            {
                throw CreateMetadataException($"{name} is missing");
            }

            return null;
        }

        if (values.Count != 1 || values[0] is not { Length: > 0 } value || value.Length > maximumLength)
        {
            throw CreateMetadataException($"{name} must be a single value of at most {maximumLength} characters");
        }

        return value;
    }

    private static InvalidOperationException CreateMetadataException(string detail)
    {
        return new InvalidOperationException(
            $"RazorWire dialog response metadata is invalid: {detail}. "
            + "Send one X-RazorWire-Request UUID and one positive X-RazorWire-Order no greater than 9007199254740991; "
            + $"X-RazorWire-Flow is optional and must be one UUID when present. See {DocumentationPath}.");
    }
}

/// <summary>
/// Renders package-authored target actions with request correlation metadata.
/// </summary>
internal interface IRazorWireTargetedStreamAction : IRazorWireStreamAction
{
    /// <summary>Renders this action with validated request correlation metadata.</summary>
    Task<string> RenderCorrelatedAsync(
        ViewContext viewContext,
        RazorWireRequestMetadata metadata,
        RazorWireDialogPhase phase,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Marks the single final dialog command in a builder snapshot.
/// </summary>
internal interface IRazorWireDialogCommandStreamAction : IRazorWireStreamAction
{
    /// <summary>Gets whether the command opens or closes the pending dialog.</summary>
    RazorWireDialogCommand Command { get; }

    /// <summary>Renders this command with validated request correlation metadata.</summary>
    Task<string> RenderCorrelatedAsync(
        ViewContext viewContext,
        RazorWireRequestMetadata metadata,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Identifies the final dialog command represented by a response.
/// </summary>
internal enum RazorWireDialogCommand
{
    /// <summary>Replace the visible dialog with a newly rendered body.</summary>
    Open,

    /// <summary>Close the eligible visible dialog.</summary>
    Close
}

/// <summary>
/// Provides shared action-position and rendering logic for builder and result paths.
/// </summary>
internal static class RazorWireStreamRendering
{
    /// <summary>Finds the final dialog command's position in a snapshot.</summary>
    /// <param name="actions">The immutable action snapshot.</param>
    /// <returns>The command index, or <c>-1</c> when the response has no dialog command.</returns>
    public static int FindDialogCommandIndex(IReadOnlyList<IRazorWireStreamAction> actions)
    {
        var dialogIndex = -1;
        for (var index = 0; index < actions.Count; index++)
        {
            if (actions[index] is not IRazorWireDialogCommandStreamAction)
            {
                continue;
            }

            if (dialogIndex >= 0)
            {
                throw new InvalidOperationException("A RazorWire response can contain only one dialog command.");
            }

            dialogIndex = index;
        }

        return dialogIndex;
    }

    /// <summary>Resolves the phase for a built-in target action from its position relative to the final command.</summary>
    /// <param name="actionIndex">The action's index in the snapshot.</param>
    /// <param name="dialogIndex">The dialog command index, or <c>-1</c> for page-only output.</param>
    /// <param name="command">The final dialog command when one exists.</param>
    /// <returns>The phase serialized for the action.</returns>
    public static RazorWireDialogPhase GetPhase(
        int actionIndex,
        int dialogIndex,
        RazorWireDialogCommand? command)
    {
        if (dialogIndex < 0 || actionIndex < dialogIndex)
        {
            return RazorWireDialogPhase.Origin;
        }

        return command switch
        {
            RazorWireDialogCommand.Open => RazorWireDialogPhase.New,
            RazorWireDialogCommand.Close => RazorWireDialogPhase.Closed,
            _ => throw new InvalidOperationException("A dialog action phase requires one final dialog command.")
        };
    }

    /// <summary>Renders one action, adding metadata only for typed built-in target or dialog actions.</summary>
    /// <param name="action">The action to render.</param>
    /// <param name="viewContext">The MVC rendering context.</param>
    /// <param name="metadata">Validated request metadata, or <see langword="null"/> for legacy uncorrelated output.</param>
    /// <param name="phase">The phase to attach to a targeted action.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The rendered stream markup.</returns>
    public static Task<string> RenderActionAsync(
        IRazorWireStreamAction action,
        ViewContext viewContext,
        RazorWireRequestMetadata? metadata,
        RazorWireDialogPhase phase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (action is IRazorWireDialogCommandStreamAction dialogAction)
        {
            if (metadata is null)
            {
                throw new InvalidOperationException("A RazorWire dialog command requires validated request correlation metadata.");
            }

            return dialogAction.RenderCorrelatedAsync(viewContext, metadata, cancellationToken);
        }

        if (action is IRazorWireTargetedStreamAction targetedAction && metadata is not null)
        {
            return targetedAction.RenderCorrelatedAsync(viewContext, metadata, phase, cancellationToken);
        }

        return action.RenderAsync(viewContext, cancellationToken);
    }
}

/// <summary>
/// Encodes and wraps built-in target actions with an optional, already validated correlation value.
/// </summary>
internal static class RazorWireStreamMarkup
{
    /// <summary>Renders one package-authored targeted Turbo Stream element.</summary>
    /// <param name="action">The Turbo action name.</param>
    /// <param name="target">The target selector or identifier.</param>
    /// <param name="content">The template body, if the action uses a template.</param>
    /// <param name="hasTemplate">Whether the action should include a template element.</param>
    /// <param name="metadata">Validated request metadata, or <see langword="null"/> for uncorrelated output.</param>
    /// <param name="phase">The action's dialog phase when metadata is present.</param>
    /// <returns>The encoded Turbo Stream element.</returns>
    public static string RenderTargeted(
        string action,
        string target,
        string? content,
        bool hasTemplate,
        RazorWireRequestMetadata? metadata = null,
        RazorWireDialogPhase phase = RazorWireDialogPhase.Origin)
    {
        var encodedAction = HtmlEncoder.Default.Encode(action);
        var encodedTarget = HtmlEncoder.Default.Encode(target);
        var attributes = metadata?.ToHtmlAttributes(phase) ?? string.Empty;

        if (!hasTemplate)
        {
            return $"<turbo-stream action=\"{encodedAction}\" target=\"{encodedTarget}\"{attributes}></turbo-stream>";
        }

        return $"<turbo-stream action=\"{encodedAction}\" target=\"{encodedTarget}\"{attributes}>"
               + $"<template>{content ?? string.Empty}</template></turbo-stream>";
    }
}
