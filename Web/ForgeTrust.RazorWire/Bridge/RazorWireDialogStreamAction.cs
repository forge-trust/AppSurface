using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// Describes the immutable body selection captured by one dialog command.
/// </summary>
internal sealed record RazorWireDialogPayload(
    string Title,
    RazorWireDialogBodyKind BodyKind,
    string? Message = null,
    string? ViewName = null,
    object? Model = null,
    Type? ComponentType = null,
    string? ComponentName = null,
    object? Arguments = null)
{
    /// <summary>Creates a validated plain-text dialog payload.</summary>
    public static RazorWireDialogPayload ForMessage(string title, string? message)
    {
        ValidateTitle(title);
        return new RazorWireDialogPayload(title, RazorWireDialogBodyKind.Message, Message: message);
    }

    /// <summary>Creates a validated partial-view dialog payload.</summary>
    public static RazorWireDialogPayload ForPartial(string title, string viewName, object? model)
    {
        ValidateTitle(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        return new RazorWireDialogPayload(title, RazorWireDialogBodyKind.Partial, ViewName: viewName, Model: model);
    }

    /// <summary>Creates a validated typed-component dialog payload.</summary>
    public static RazorWireDialogPayload ForComponent(string title, Type componentType, object? arguments)
    {
        ValidateTitle(title);
        ArgumentNullException.ThrowIfNull(componentType);
        return new RazorWireDialogPayload(
            title,
            RazorWireDialogBodyKind.ComponentType,
            ComponentType: componentType,
            Arguments: arguments);
    }

    /// <summary>Creates a validated named-component dialog payload.</summary>
    public static RazorWireDialogPayload ForNamedComponent(string title, string componentName, object? arguments)
    {
        ValidateTitle(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        return new RazorWireDialogPayload(
            title,
            RazorWireDialogBodyKind.ComponentName,
            ComponentName: componentName,
            Arguments: arguments);
    }

    private static void ValidateTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
    }
}

/// <summary>
/// Identifies how the selected dialog body is rendered.
/// </summary>
internal enum RazorWireDialogBodyKind
{
    /// <summary>Encode the selected plain-text message as template text.</summary>
    Message,

    /// <summary>Render the selected MVC partial view.</summary>
    Partial,

    /// <summary>Invoke the selected view component type.</summary>
    ComponentType,

    /// <summary>Invoke the selected view component name.</summary>
    ComponentName
}

/// <summary>
/// Renders the single normalized dialog command captured by a builder snapshot.
/// </summary>
internal sealed class RazorWireDialogStreamAction : IRazorWireDialogCommandStreamAction
{
    private readonly RazorWireDialogPayload? _payload;

    private RazorWireDialogStreamAction(RazorWireDialogCommand command, RazorWireDialogPayload? payload)
    {
        if (command == RazorWireDialogCommand.Open && payload is null)
        {
            throw new ArgumentNullException(nameof(payload), "An open dialog command requires a payload.");
        }

        if (command == RazorWireDialogCommand.Close && payload is not null)
        {
            throw new ArgumentException("A close dialog command cannot contain an open payload.", nameof(payload));
        }

        Command = command;
        _payload = payload;
    }

    /// <inheritdoc />
    public RazorWireDialogCommand Command { get; }

    /// <summary>Creates the final open command for the selected payload.</summary>
    /// <param name="payload">The immutable body selection captured by the builder.</param>
    /// <returns>The open command action.</returns>
    public static RazorWireDialogStreamAction Open(RazorWireDialogPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new RazorWireDialogStreamAction(RazorWireDialogCommand.Open, payload);
    }

    /// <summary>Creates the final close command.</summary>
    /// <returns>The close command action.</returns>
    public static RazorWireDialogStreamAction Close()
    {
        return new RazorWireDialogStreamAction(RazorWireDialogCommand.Close, payload: null);
    }

    /// <summary>
    /// Rejects rendering without validated request metadata.
    /// </summary>
    /// <param name="viewContext">The MVC view context.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>This method never returns markup.</returns>
    public Task<string> RenderAsync(ViewContext viewContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewContext);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("A RazorWire dialog command requires validated request correlation metadata.");
    }

    /// <inheritdoc />
    public async Task<string> RenderCorrelatedAsync(
        ViewContext viewContext,
        RazorWireRequestMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewContext);
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();

        var attributes = metadata.ToHtmlAttributes(RazorWireDialogPhase.Origin);
        if (Command == RazorWireDialogCommand.Close)
        {
            return $"<turbo-stream action=\"rw-dialog\" dialog-command=\"close\"{attributes}></turbo-stream>";
        }

        var payload = _payload!;
        var encodedTitle = HtmlEncoder.Default.Encode(payload.Title);
        var body = await RenderBodyAsync(payload, viewContext, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return $"<turbo-stream action=\"rw-dialog\" dialog-command=\"open\" dialog-title=\"{encodedTitle}\"{attributes}>"
               + $"<template>{body}</template></turbo-stream>";
    }

    private static async Task<string> RenderBodyAsync(
        RazorWireDialogPayload payload,
        ViewContext viewContext,
        CancellationToken cancellationToken)
    {
        return payload.BodyKind switch
        {
            RazorWireDialogBodyKind.Message => HtmlEncoder.Default.Encode(payload.Message ?? string.Empty),
            RazorWireDialogBodyKind.Partial => await PartialViewStreamAction.RenderPartialContentAsync(
                viewContext,
                payload.ViewName!,
                payload.Model,
                cancellationToken),
            RazorWireDialogBodyKind.ComponentType => await ViewComponentStreamHelper.RenderComponentContentAsync(
                viewContext,
                payload.ComponentType!,
                payload.Arguments,
                cancellationToken),
            RazorWireDialogBodyKind.ComponentName => await ViewComponentStreamHelper.RenderComponentContentAsync(
                viewContext,
                payload.ComponentName!,
                payload.Arguments,
                cancellationToken),
            _ => throw new InvalidOperationException("Unsupported RazorWire dialog body kind.")
        };
    }
}
