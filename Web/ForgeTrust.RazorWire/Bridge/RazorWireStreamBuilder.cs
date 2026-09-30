using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// A fluent builder for creating Turbo Stream responses.
/// </summary>
public class RazorWireStreamBuilder
{
    private readonly Controller? _controller;
    private readonly List<IRazorWireStreamAction> _actions = new();
    private int? _dialogSlotIndex;
    private RazorWireDialogPayload? _dialogPayload;
    private bool _hasFormError;

    /// <summary>
    /// Initializes a new instance of <see cref="RazorWireStreamBuilder"/> and captures an optional <see cref="Controller"/> for rendering partials or view components.
    /// </summary>
    /// <param name="controller">Optional <see cref="Controller"/> whose context will be used when rendering partials or view components; may be <c>null</c>.</param>
    public RazorWireStreamBuilder(Controller? controller = null)
    {
        _controller = controller;
    }

    /// <summary>
    /// Queues an append action that inserts HTML-encoded text into the specified target element.
    /// </summary>
    /// <remarks>
    /// This method treats <paramref name="content"/> as plain text and HTML-encodes it before writing it to the stream
    /// template. Use <see cref="AppendHtml(string,string?)"/> only when the caller already owns a trusted HTML fragment
    /// and has encoded any user-supplied values inside it.
    /// </remarks>
    /// <param name="target">The target DOM selector or element identifier to which the encoded text will be appended.</param>
    /// <param name="content">Plain-text content to append inside the target's template; <c>null</c> renders as empty text.</param>
    /// <returns>The same <see cref="RazorWireStreamBuilder"/> instance to allow fluent chaining.</returns>
    public RazorWireStreamBuilder Append(string target, string? content)
    {
        _actions.Add(new TemplateStreamAction("append", target, content, TemplateContentKind.PlainText));

        return this;
    }

    /// <summary>
    /// Queues an append action that inserts a trusted HTML fragment into the specified target element without encoding or sanitizing it.
    /// </summary>
    /// <remarks>
    /// This is the raw-markup escape hatch for server-authored fragments. RazorWire does not encode or sanitize
    /// <paramref name="trustedHtml"/>; callers must ensure the fragment is trusted and that any user-supplied values have
    /// already been HTML-encoded. Prefer <see cref="Append(string,string?)"/> for text and
    /// <see cref="AppendPartial(string,string,object?)"/> or <see cref="AppendComponent{T}(string,object?)"/> for Razor-rendered markup.
    /// </remarks>
    /// <param name="target">The target DOM selector or element identifier to which the trusted HTML will be appended.</param>
    /// <param name="trustedHtml">Trusted HTML to append inside the target's template; <c>null</c> renders as an empty fragment.</param>
    /// <returns>The same <see cref="RazorWireStreamBuilder"/> instance to allow fluent chaining.</returns>
    public RazorWireStreamBuilder AppendHtml(string target, string? trustedHtml)
    {
        _actions.Add(new TemplateStreamAction("append", target, trustedHtml, TemplateContentKind.TrustedHtml));

        return this;
    }

    /// <summary>
    /// Queues an action to append the rendered partial view to the specified DOM target.
    /// </summary>
    /// <param name="target">The DOM target selector or element identifier where the partial will be appended.</param>
    /// <param name="viewName">The name of the partial view to render.</param>
    /// <param name="model">An optional model to pass to the partial view.</param>
    /// <returns>The current <see cref="RazorWireStreamBuilder"/> instance for fluent chaining.</returns>
    public RazorWireStreamBuilder AppendPartial(string target, string viewName, object? model = null)
    {
        _actions.Add(new PartialViewStreamAction("append", target, viewName, model));

        return this;
    }

    /// <summary>
    /// Queues a prepend action that inserts HTML-encoded text into the specified target element.
    /// </summary>
    /// <param name="target">The DOM target selector or identifier to receive the content.</param>
    /// <param name="content">Plain-text content to insert before the target element's existing content; <c>null</c> renders as empty text.</param>
    /// <returns>The builder instance for fluent chaining.</returns>
    /// <remarks>
    /// RazorWire HTML-encodes <paramref name="content"/> before placing it in the stream template. Use
    /// <see cref="PrependHtml(string,string?)"/> only for trusted, already-safe HTML fragments.
    /// </remarks>
    public RazorWireStreamBuilder Prepend(string target, string? content)
    {
        _actions.Add(new TemplateStreamAction("prepend", target, content, TemplateContentKind.PlainText));

        return this;
    }

    /// <summary>
    /// Queues a prepend action that inserts a trusted HTML fragment without encoding or sanitizing it.
    /// </summary>
    /// <remarks>
    /// RazorWire writes <paramref name="trustedHtml"/> directly into the stream template. Use this only for
    /// server-authored trusted markup; encode user values before composing the fragment. Prefer
    /// <see cref="Prepend(string,string?)"/> for text and partial or component helpers for Razor-rendered markup.
    /// </remarks>
    /// <param name="target">The DOM target selector or identifier to receive the trusted HTML.</param>
    /// <param name="trustedHtml">Trusted HTML content to insert before the target element's existing content; <c>null</c> renders as an empty fragment.</param>
    /// <returns>The builder instance for fluent chaining.</returns>
    public RazorWireStreamBuilder PrependHtml(string target, string? trustedHtml)
    {
        _actions.Add(new TemplateStreamAction("prepend", target, trustedHtml, TemplateContentKind.TrustedHtml));

        return this;
    }

    /// <summary>
    /// Queues an action to prepend the rendered partial view into the specified DOM target.
    /// </summary>
    /// <param name="target">The DOM selector or element identifier to receive the rendered partial.</param>
    /// <param name="viewName">The name or path of the partial view to render.</param>
    /// <param name="model">The model to pass to the partial view, or null if none.</param>
    /// <returns>The current <see cref="RazorWireStreamBuilder"/> instance for fluent chaining.</returns>
    public RazorWireStreamBuilder PrependPartial(string target, string viewName, object? model = null)
    {
        _actions.Add(new PartialViewStreamAction("prepend", target, viewName, model));

        return this;
    }

    /// <summary>
    /// Queues a replace action that inserts HTML-encoded text into the specified target element.
    /// </summary>
    /// <param name="target">The DOM element selector or identifier to target.</param>
    /// <param name="content">Plain-text content used to replace the target's contents; <c>null</c> renders as empty text.</param>
    /// <returns>The current RazorWireStreamBuilder instance.</returns>
    /// <remarks>
    /// RazorWire HTML-encodes <paramref name="content"/> before placing it in the stream template. Use
    /// <see cref="ReplaceHtml(string,string?)"/> only for trusted HTML fragments.
    /// </remarks>
    public RazorWireStreamBuilder Replace(string target, string? content)
    {
        _actions.Add(new TemplateStreamAction("replace", target, content, TemplateContentKind.PlainText));

        return this;
    }

    /// <summary>
    /// Queues a replace action that inserts a trusted HTML fragment without encoding or sanitizing it.
    /// </summary>
    /// <remarks>
    /// RazorWire writes <paramref name="trustedHtml"/> directly into the stream template. Use this only for
    /// server-authored trusted markup; encode user values before composing the fragment. Prefer
    /// <see cref="Replace(string,string?)"/> for text and partial or component helpers for Razor-rendered markup.
    /// </remarks>
    /// <param name="target">The DOM element selector or identifier to target.</param>
    /// <param name="trustedHtml">Trusted HTML content used to replace the target's contents; <c>null</c> renders as an empty fragment.</param>
    /// <returns>The current RazorWireStreamBuilder instance.</returns>
    public RazorWireStreamBuilder ReplaceHtml(string target, string? trustedHtml)
    {
        _actions.Add(new TemplateStreamAction("replace", target, trustedHtml, TemplateContentKind.TrustedHtml));

        return this;
    }

    /// <summary>
    /// Queues a partial view to replace the contents of the specified DOM target with the rendered partial.
    /// </summary>
    /// <param name="target">The DOM element selector or identifier to target for the replace action.</param>
    /// <param name="viewName">The name of the partial view to render.</param>
    /// <param name="model">An optional model passed to the partial view.</param>
    /// <returns>The same <see cref="RazorWireStreamBuilder"/> instance for fluent chaining.</returns>
    public RazorWireStreamBuilder ReplacePartial(string target, string viewName, object? model = null)
    {
        _actions.Add(new PartialViewStreamAction("replace", target, viewName, model));

        return this;
    }

    /// <summary>
    /// Queues an update action that inserts HTML-encoded text into the specified target element.
    /// </summary>
    /// <param name="target">The DOM target selector or identifier to apply the update to.</param>
    /// <param name="content">Plain-text content to use as the action's template; <c>null</c> renders as empty text.</param>
    /// <returns>The same RazorWireStreamBuilder instance for fluent chaining.</returns>
    /// <remarks>
    /// RazorWire HTML-encodes <paramref name="content"/> before placing it in the stream template. Use
    /// <see cref="UpdateHtml(string,string?)"/> only for trusted HTML fragments.
    /// </remarks>
    public RazorWireStreamBuilder Update(string target, string? content)
    {
        _actions.Add(new TemplateStreamAction("update", target, content, TemplateContentKind.PlainText));

        return this;
    }

    /// <summary>
    /// Queues an update action that inserts a trusted HTML fragment without encoding or sanitizing it.
    /// </summary>
    /// <remarks>
    /// RazorWire writes <paramref name="trustedHtml"/> directly into the stream template. Use this only for
    /// server-authored trusted markup; encode user values before composing the fragment. Prefer
    /// <see cref="Update(string,string?)"/> for text and partial or component helpers for Razor-rendered markup.
    /// </remarks>
    /// <param name="target">The DOM target selector or identifier to apply the update to.</param>
    /// <param name="trustedHtml">Trusted HTML fragment to use as the action's template; <c>null</c> renders as an empty fragment.</param>
    /// <returns>The same RazorWireStreamBuilder instance for fluent chaining.</returns>
    public RazorWireStreamBuilder UpdateHtml(string target, string? trustedHtml)
    {
        _actions.Add(new TemplateStreamAction("update", target, trustedHtml, TemplateContentKind.TrustedHtml));

        return this;
    }

    /// <summary>
    /// Queues an "update" turbo-stream action that renders the specified partial view into the given target element.
    /// </summary>
    /// <param name="target">The DOM target selector or identifier to update.</param>
    /// <param name="viewName">The name of the partial view to render.</param>
    /// <param name="model">An optional model to pass to the partial view.</param>
    /// <returns>The builder instance for further chaining.</returns>
    public RazorWireStreamBuilder UpdatePartial(string target, string viewName, object? model = null)
    {
        _actions.Add(new PartialViewStreamAction("update", target, viewName, model));

        return this;
    }

    /// <summary>
    /// Queues an append action that will render the specified view component into the given DOM target.
    /// </summary>
    /// <typeparam name="T">The <see cref="ViewComponent"/> type to render.</typeparam>
    /// <param name="target">The DOM element selector or identifier to target for the append action.</param>
    /// <param name="arguments">Optional arguments passed to the view component when rendering.</param>
    /// <returns>The same <see cref="RazorWireStreamBuilder"/> instance for fluent chaining.</returns>
    public RazorWireStreamBuilder AppendComponent<T>(string target, object? arguments = null) where T : ViewComponent
    {
        _actions.Add(new ViewComponentStreamAction("append", target, typeof(T), arguments));

        return this;
    }

    /// <summary>
    /// Queues a view component render action that will prepend the component's output into the specified DOM target.
    /// </summary>
    /// <typeparam name="T">The view component type to render.</typeparam>
    /// <param name="target">The DOM target selector or identifier to prepend the component into.</param>
    /// <param name="arguments">Optional arguments passed to the view component.</param>
    /// <returns>The same <see cref="RazorWireStreamBuilder"/> instance for fluent chaining.</returns>
    public RazorWireStreamBuilder PrependComponent<T>(string target, object? arguments = null) where T : ViewComponent
    {
        _actions.Add(new ViewComponentStreamAction("prepend", target, typeof(T), arguments));

        return this;
    }

    /// <summary>
    /// Queues a view component replace action targeting the specified DOM element.
    /// </summary>
    /// <param name="target">The DOM target selector or identifier to apply the replace action to.</param>
    /// <param name="arguments">Optional arguments to pass to the view component.</param>
    /// <returns>The builder instance for fluent chaining.</returns>
    public RazorWireStreamBuilder ReplaceComponent<T>(string target, object? arguments = null) where T : ViewComponent
    {
        _actions.Add(new ViewComponentStreamAction("replace", target, typeof(T), arguments));

        return this;
    }

    /// <summary>
    /// Queues an "update" turbo-stream action that renders the specified view component type into the given target element.
    /// </summary>
    /// <typeparam name="T">The view component type to render.</typeparam>
    /// <param name="target">The DOM element selector or identifier that the turbo-stream will target.</param>
    /// <param name="arguments">Optional arguments to pass to the view component.</param>
    /// <returns>The same <see cref="RazorWireStreamBuilder"/> instance for fluent chaining.</returns>
    public RazorWireStreamBuilder UpdateComponent<T>(string target, object? arguments = null) where T : ViewComponent
    {
        _actions.Add(new ViewComponentStreamAction("update", target, typeof(T), arguments));

        return this;
    }

    /// <summary>
    /// Queues an "append" turbo-stream action that will render the specified view component (by name) into the given target element.
    /// </summary>
    /// <param name="target">The DOM element selector or identifier to target.</param>
    /// <param name="componentName">The name of the view component to render.</param>
    /// <param name="arguments">Optional arguments to pass to the view component.</param>
    /// <returns>The current <see cref="RazorWireStreamBuilder"/> instance for method chaining.</returns>
    public RazorWireStreamBuilder AppendComponent(string target, string componentName, object? arguments = null)
    {
        _actions.Add(new ViewComponentByNameStreamAction("append", target, componentName, arguments));

        return this;
    }

    /// <summary>
    /// Queues a view component prepend action targeting a DOM element by name.
    /// </summary>
    /// <param name="target">The DOM element selector or identifier to target.</param>
    /// <param name="componentName">The name of the view component to render and prepend.</param>
    /// <param name="arguments">Optional arguments to pass to the view component.</param>
    /// <returns>The current builder instance for fluent chaining.</returns>
    public RazorWireStreamBuilder PrependComponent(string target, string componentName, object? arguments = null)
    {
        _actions.Add(new ViewComponentByNameStreamAction("prepend", target, componentName, arguments));

        return this;
    }

    /// <summary>
    /// Queue a replace action that renders the specified view component by name into the given DOM target.
    /// </summary>
    /// <param name="target">The DOM target selector or identifier to apply the replace action to.</param>
    /// <param name="componentName">The name of the view component to render.</param>
    /// <param name="arguments">Optional arguments to pass to the view component.</param>
    /// <returns>The same RazorWireStreamBuilder instance for fluent chaining.</returns>
    public RazorWireStreamBuilder ReplaceComponent(string target, string componentName, object? arguments = null)
    {
        _actions.Add(new ViewComponentByNameStreamAction("replace", target, componentName, arguments));

        return this;
    }

    /// <summary>
    /// Queues a view component update action for a named view component.
    /// </summary>
    /// <param name="target">The DOM target selector or identifier to apply the update to.</param>
    /// <param name="componentName">The name of the view component to render.</param>
    /// <param name="arguments">Optional arguments to pass to the view component.</param>
    /// <returns>The builder instance for fluent chaining.</returns>
    public RazorWireStreamBuilder UpdateComponent(string target, string componentName, object? arguments = null)
    {
        _actions.Add(new ViewComponentByNameStreamAction("update", target, componentName, arguments));

        return this;
    }

    /// <summary>
    /// Queues a dialog command that opens a titled dialog containing an HTML-encoded plain-text message.
    /// </summary>
    /// <remarks>
    /// A builder can hold only one pending dialog payload. Calling another <c>OpenDialog*</c> method while
    /// <see cref="HasActiveDialog"/> is true throws immediately; use a <c>ReplaceDialog*</c> method to explicitly
    /// overwrite the pending title and body. A null message produces an empty body. The dialog command occupies the
    /// position of the first dialog operation, so page actions retain their order around that position. The command
    /// requires request correlation and therefore can be emitted with <see cref="BuildResult(int?)"/> or rendered
    /// with <see cref="RenderAsync(Microsoft.AspNetCore.Mvc.Rendering.ViewContext,CancellationToken)"/>; it cannot be
    /// emitted with <see cref="Build"/>.
    /// </remarks>
    /// <param name="title">Nonblank plain-text dialog title; RazorWire encodes it as an HTML attribute.</param>
    /// <param name="message">Optional plain-text body; RazorWire HTML-encodes it, and <see langword="null"/> means an empty body.</param>
    /// <returns>This builder for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="title"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another dialog open payload is already pending.</exception>
    public RazorWireStreamBuilder OpenDialog(string title, string? message)
    {
        QueueDialogOpen(RazorWireDialogPayload.ForMessage(title, message));
        return this;
    }

    /// <summary>
    /// Queues a dialog command that opens a titled dialog containing a rendered partial view.
    /// </summary>
    /// <remarks>
    /// Only the final buffered dialog payload is rendered. A later <c>ReplaceDialog*</c> call overwrites this partial
    /// without invoking its view. The partial uses the existing trusted Razor markup boundary. The command requires
    /// request correlation, so use <see cref="BuildResult(int?)"/> or
    /// <see cref="RenderAsync(Microsoft.AspNetCore.Mvc.Rendering.ViewContext,CancellationToken)"/>.
    /// </remarks>
    /// <param name="title">Nonblank plain-text dialog title; RazorWire encodes it as an HTML attribute.</param>
    /// <param name="viewName">The MVC partial view name or path to render as the dialog body.</param>
    /// <param name="model">Optional model passed to the partial view.</param>
    /// <returns>This builder for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="title"/> or <paramref name="viewName"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another dialog open payload is already pending.</exception>
    public RazorWireStreamBuilder OpenDialogPartial(string title, string viewName, object? model = null)
    {
        QueueDialogOpen(RazorWireDialogPayload.ForPartial(title, viewName, model));
        return this;
    }

    /// <summary>
    /// Queues a dialog command that opens a titled dialog containing a rendered view component selected by type.
    /// </summary>
    /// <remarks>
    /// Only the final buffered dialog payload is rendered. A later <c>ReplaceDialog*</c> call overwrites this component
    /// without invoking it. The component uses the existing trusted Razor markup boundary. The command requires request
    /// correlation, so use <see cref="BuildResult(int?)"/> or
    /// <see cref="RenderAsync(Microsoft.AspNetCore.Mvc.Rendering.ViewContext,CancellationToken)"/>.
    /// </remarks>
    /// <typeparam name="T">The <see cref="ViewComponent"/> type to render.</typeparam>
    /// <param name="title">Nonblank plain-text dialog title; RazorWire encodes it as an HTML attribute.</param>
    /// <param name="arguments">Optional arguments passed to the view component.</param>
    /// <returns>This builder for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="title"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another dialog open payload is already pending.</exception>
    public RazorWireStreamBuilder OpenDialogComponent<T>(string title, object? arguments = null) where T : ViewComponent
    {
        QueueDialogOpen(RazorWireDialogPayload.ForComponent(title, typeof(T), arguments));
        return this;
    }

    /// <summary>
    /// Queues a dialog command that opens a titled dialog containing a rendered view component selected by name.
    /// </summary>
    /// <remarks>
    /// Only the final buffered dialog payload is rendered. A later <c>ReplaceDialog*</c> call overwrites this component
    /// without invoking it. The component uses the existing trusted Razor markup boundary. The command requires request
    /// correlation, so use <see cref="BuildResult(int?)"/> or
    /// <see cref="RenderAsync(Microsoft.AspNetCore.Mvc.Rendering.ViewContext,CancellationToken)"/>.
    /// </remarks>
    /// <param name="title">Nonblank plain-text dialog title; RazorWire encodes it as an HTML attribute.</param>
    /// <param name="componentName">The MVC view component name to render as the dialog body.</param>
    /// <param name="arguments">Optional arguments passed to the view component.</param>
    /// <returns>This builder for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="title"/> or <paramref name="componentName"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when another dialog open payload is already pending.</exception>
    public RazorWireStreamBuilder OpenDialogComponent(string title, string componentName, object? arguments = null)
    {
        QueueDialogOpen(RazorWireDialogPayload.ForNamedComponent(title, componentName, arguments));
        return this;
    }

    /// <summary>
    /// Replaces the pending dialog title and body with an HTML-encoded plain-text message.
    /// </summary>
    /// <remarks>
    /// Replacement changes the single unsent dialog slot and creates no intermediate browser command. It requires a
    /// pending open payload, as reported by <see cref="HasActiveDialog"/>. A null message produces an empty body.
    /// </remarks>
    /// <param name="title">Nonblank plain-text dialog title; RazorWire encodes it as an HTML attribute.</param>
    /// <param name="message">Optional plain-text body; RazorWire HTML-encodes it, and <see langword="null"/> means an empty body.</param>
    /// <returns>This builder for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="title"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no dialog open payload is pending.</exception>
    public RazorWireStreamBuilder ReplaceDialog(string title, string? message)
    {
        ReplaceDialogPayload(RazorWireDialogPayload.ForMessage(title, message));
        return this;
    }

    /// <summary>
    /// Replaces the pending dialog title and body with a rendered partial view.
    /// </summary>
    /// <remarks>
    /// Replacement changes the single unsent dialog slot and creates no intermediate browser command. It requires a
    /// pending open payload, as reported by <see cref="HasActiveDialog"/>. Only the final partial is rendered.
    /// </remarks>
    /// <param name="title">Nonblank plain-text dialog title; RazorWire encodes it as an HTML attribute.</param>
    /// <param name="viewName">The MVC partial view name or path to render as the dialog body.</param>
    /// <param name="model">Optional model passed to the partial view.</param>
    /// <returns>This builder for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="title"/> or <paramref name="viewName"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no dialog open payload is pending.</exception>
    public RazorWireStreamBuilder ReplaceDialogPartial(string title, string viewName, object? model = null)
    {
        ReplaceDialogPayload(RazorWireDialogPayload.ForPartial(title, viewName, model));
        return this;
    }

    /// <summary>
    /// Replaces the pending dialog title and body with a rendered view component selected by type.
    /// </summary>
    /// <remarks>
    /// Replacement changes the single unsent dialog slot and creates no intermediate browser command. It requires a
    /// pending open payload, as reported by <see cref="HasActiveDialog"/>. Only the final component is rendered.
    /// </remarks>
    /// <typeparam name="T">The <see cref="ViewComponent"/> type to render.</typeparam>
    /// <param name="title">Nonblank plain-text dialog title; RazorWire encodes it as an HTML attribute.</param>
    /// <param name="arguments">Optional arguments passed to the view component.</param>
    /// <returns>This builder for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="title"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no dialog open payload is pending.</exception>
    public RazorWireStreamBuilder ReplaceDialogComponent<T>(string title, object? arguments = null) where T : ViewComponent
    {
        ReplaceDialogPayload(RazorWireDialogPayload.ForComponent(title, typeof(T), arguments));
        return this;
    }

    /// <summary>
    /// Replaces the pending dialog title and body with a rendered view component selected by name.
    /// </summary>
    /// <remarks>
    /// Replacement changes the single unsent dialog slot and creates no intermediate browser command. It requires a
    /// pending open payload, as reported by <see cref="HasActiveDialog"/>. Only the final component is rendered.
    /// </remarks>
    /// <param name="title">Nonblank plain-text dialog title; RazorWire encodes it as an HTML attribute.</param>
    /// <param name="componentName">The MVC view component name to render as the dialog body.</param>
    /// <param name="arguments">Optional arguments passed to the view component.</param>
    /// <returns>This builder for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="title"/> or <paramref name="componentName"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no dialog open payload is pending.</exception>
    public RazorWireStreamBuilder ReplaceDialogComponent(string title, string componentName, object? arguments = null)
    {
        ReplaceDialogPayload(RazorWireDialogPayload.ForNamedComponent(title, componentName, arguments));
        return this;
    }

    /// <summary>
    /// Queues an explicit close command in the builder's single dialog slot.
    /// </summary>
    /// <remarks>
    /// Closing clears any pending open payload, so <see cref="HasActiveDialog"/> becomes false. Repeated calls are
    /// idempotent. A later <c>OpenDialog*</c> call changes the same slot to open; a close command requires
    /// <see cref="BuildResult(int?)"/> or request-aware <see cref="RenderAsync(Microsoft.AspNetCore.Mvc.Rendering.ViewContext,CancellationToken)"/>
    /// and cannot be emitted by <see cref="Build"/>.
    /// </remarks>
    /// <returns>This builder for fluent chaining.</returns>
    public RazorWireStreamBuilder CloseDialog()
    {
        EnsureDialogSlot();
        _dialogPayload = null;
        return this;
    }

    /// <summary>
    /// Gets whether this builder currently has a pending dialog open payload.
    /// </summary>
    /// <remarks>
    /// This reports only the builder's response buffer, not whether a browser currently displays a dialog. It is true
    /// after an open or replacement and false initially or after <see cref="CloseDialog"/>.
    /// </remarks>
    public bool HasActiveDialog => _dialogPayload is not null;

    /// <summary>
    /// Queues a remove action targeting the specified DOM element.
    /// </summary>
    /// <param name="target">The DOM target selector or identifier whose element will be removed.</param>
    /// <returns>The current <see cref="RazorWireStreamBuilder"/> instance for fluent chaining.</returns>
    public RazorWireStreamBuilder Remove(string target)
    {
        _actions.Add(new TemplateStreamAction("remove", target, content: null, TemplateContentKind.TrustedHtml));

        return this;
    }

    /// <summary>
    /// Queues a one-shot Turbo Drive visit stream command that advances browser history.
    /// </summary>
    /// <remarks>
    /// RazorWire emits this command as a <c>rw-visit</c> Turbo Stream action with a <c>visit-action</c> value of
    /// <c>advance</c>. The browser runtime validates that the resolved URL stays on the current origin before calling
    /// Turbo. Visit commands are not idempotent state snapshots, so publish them only to live subscribers and never to
    /// retained replay channels.
    /// </remarks>
    /// <param name="url">The relative or same-origin URL that the browser should visit.</param>
    /// <returns>The current <see cref="RazorWireStreamBuilder"/> instance for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="url"/> is null, empty, whitespace, or contains an ASCII control character.</exception>
    public RazorWireStreamBuilder Visit(string url)
    {
        return Visit(url, RazorWireVisitAction.Advance);
    }

    /// <summary>
    /// Queues a one-shot Turbo Drive visit stream command.
    /// </summary>
    /// <remarks>
    /// The generated stream contains no <c>target</c> or <c>template</c>; it is a command for the RazorWire browser
    /// runtime. Server-side validation rejects only input that cannot safely be serialized. Origin checks happen in the
    /// browser because relative URLs resolve against the current document location. Do not use this method for fallback
    /// content or replayable state; render a normal link or retained state stream for those cases.
    /// </remarks>
    /// <param name="url">The relative or same-origin URL that the browser should visit.</param>
    /// <param name="action">The Turbo Drive history action to apply.</param>
    /// <returns>The current <see cref="RazorWireStreamBuilder"/> instance for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="url"/> is null, empty, whitespace, or contains an ASCII control character.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="action"/> is not a supported <see cref="RazorWireVisitAction"/> value.</exception>
    public RazorWireStreamBuilder Visit(string url, RazorWireVisitAction action)
    {
        ValidateVisitUrl(url);
        _actions.Add(new VisitStreamAction(url, action));

        return this;
    }

    /// <summary>
    /// Queues a form-local failure summary for an enhanced RazorWire form.
    /// </summary>
    /// <remarks>
    /// The <paramref name="target"/> value is emitted as Turbo's <c>target</c> attribute, so it should name the DOM
    /// element that Turbo will update, usually a form-local error container. Calling this method marks the stream as a
    /// handled form response; <see cref="BuildResult(int?)"/> will emit <see cref="Forms.RazorWireFormHeaders.FormHandled"/>
    /// so the browser runtime does not add a second fallback block for the same failed submission.
    /// </remarks>
    /// <param name="target">The Turbo target id whose element should receive the generated failure block.</param>
    /// <param name="title">Plain-text failure title. RazorWire HTML-encodes this value.</param>
    /// <param name="message">Plain-text failure message. RazorWire HTML-encodes this value.</param>
    /// <returns>The current <see cref="RazorWireStreamBuilder"/> instance for fluent chaining.</returns>
    public RazorWireStreamBuilder FormError(string target, string title, string message)
    {
        _hasFormError = true;
        _actions.Add(new TemplateStreamAction(
            "update",
            target,
            BuildGeneratedFormErrorHtml(title, message, [], "server"),
            TemplateContentKind.TrustedHtml));

        return this;
    }

    /// <summary>
    /// Queues a form-local validation summary from an MVC <see cref="ModelStateDictionary"/>.
    /// </summary>
    /// <remarks>
    /// The <paramref name="target"/> value is emitted as Turbo's <c>target</c> attribute, so it should name the DOM
    /// element that Turbo will update. Calling this method marks the stream as a handled form response;
    /// <see cref="BuildResult(int?)"/> will emit <see cref="Forms.RazorWireFormHeaders.FormHandled"/> so the browser
    /// runtime does not render its default fallback UI. <paramref name="maxErrors"/> is clamped to zero or greater.
    /// RazorWire orders collected errors by <c>error.Key</c> using <see cref="StringComparer.Ordinal"/>, then renders
    /// only the first <paramref name="maxErrors"/> errors and adds an overflow line when more errors were hidden. For
    /// errors that share the same field key, original model-state insertion order is preserved. When the model state
    /// has no displayable errors, the provided <paramref name="message"/> is rendered as the fallback copy.
    /// </remarks>
    /// <param name="target">The Turbo target id whose element should receive the generated validation block.</param>
    /// <param name="modelState">The MVC model state to render into a validation summary.</param>
    /// <param name="title">Plain-text summary title. RazorWire HTML-encodes this value.</param>
    /// <param name="maxErrors">Maximum number of individual validation errors to show before an overflow line is added; values less than zero are treated as zero.</param>
    /// <param name="message">Plain-text fallback message used when the model state contains no displayable errors.</param>
    /// <returns>The current <see cref="RazorWireStreamBuilder"/> instance for fluent chaining.</returns>
    public RazorWireStreamBuilder FormValidationErrors(
        string target,
        ModelStateDictionary modelState,
        string title = "Please fix the highlighted fields.",
        int maxErrors = 10,
        string message = "We could not submit this form. Check your input and try again.")
    {
        ArgumentNullException.ThrowIfNull(modelState);

        _hasFormError = true;
        var errors = CollectModelStateErrors(modelState);
        var normalizedMaxErrors = Math.Max(0, maxErrors);
        var visibleErrors = errors.Take(normalizedMaxErrors).ToList();
        var hiddenCount = Math.Max(0, errors.Count - visibleErrors.Count);
        var fallbackMessage = errors.Count == 0
            ? message
            : "Check the validation messages and try again.";

        _actions.Add(new TemplateStreamAction(
            "update",
            target,
            BuildGeneratedFormErrorHtml(
                title,
                fallbackMessage,
                visibleErrors,
                "validation",
                hiddenCount),
            TemplateContentKind.TrustedHtml));

        return this;
    }

    /// <summary>
    /// Builds a single concatenated Turbo Stream markup string from the queued synchronous actions.
    /// </summary>
    /// <returns>The concatenated Turbo Stream markup representing the queued synchronous actions.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the builder contains actions that require asynchronous rendering (such as partial views or view components); use RenderAsync(viewContext) or BuildResult() instead.</exception>
    public string Build()
    {
        if (_dialogSlotIndex is not null)
        {
            throw new InvalidOperationException(
                "Cannot synchronously build a stream containing a dialog command because request correlation metadata is required. "
                + $"Return BuildResult() from a controller action or call RenderAsync(viewContext) with the current request context. "
                + $"See {RazorWireRequestMetadata.DocumentationPath}.");
        }

        var sb = new System.Text.StringBuilder();
        foreach (var action in _actions)
        {
            if (action is ISynchronousRazorWireStreamAction synchronous)
            {
                sb.Append(synchronous.Render());
            }
            else
            {
                throw new InvalidOperationException(
                    """
                    Cannot synchronously build a stream containing asynchronous actions
                    (like Partial Views or View Components).
                    Use RenderAsync(viewContext) or return BuildResult() from an action.
                    """);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders all queued stream actions using the provided ViewContext and concatenates their rendered HTML into a single string.
    /// </summary>
    /// <param name="viewContext">The view rendering context to use for each action.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The concatenated HTML string produced by rendering each queued action.</returns>
    /// <remarks>
    /// Snapshots the dialog slot before awaiting renderers and validates request correlation when a dialog is present.
    /// After successful rendering, echoes a correlated request UUID in the response's <c>X-RazorWire-Request</c> header
    /// so handled validation can be associated with its submission. Callers own the response content type, status,
    /// and handled-form header; prefer <see cref="BuildResult(int?)"/> when returning a controller response.
    /// See <c>Docs/dialog-responses.md</c> for the complete presentation contract.
    /// </remarks>
    public async Task<string> RenderAsync(
        Microsoft.AspNetCore.Mvc.Rendering.ViewContext viewContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewContext);
        var actions = CreateActionSnapshot();
        var dialogIndex = RazorWireStreamRendering.FindDialogCommandIndex(actions);
        var metadata = RazorWireRequestMetadata.Read(viewContext.HttpContext.Request, required: dialogIndex >= 0);
        var command = dialogIndex >= 0
            ? ((IRazorWireDialogCommandStreamAction)actions[dialogIndex]).Command
            : (RazorWireDialogCommand?)null;
        var sb = new System.Text.StringBuilder();
        for (var index = 0; index < actions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var phase = RazorWireStreamRendering.GetPhase(index, dialogIndex, command);
            var html = await RazorWireStreamRendering.RenderActionAsync(
                actions[index],
                viewContext,
                metadata,
                phase,
                cancellationToken);
            sb.Append(html);
        }

        if (metadata is not null)
        {
            viewContext.HttpContext.Response.Headers[RazorWireRequestMetadata.ResponseRequestHeaderName] =
                metadata.RequestId.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        }

        return sb.ToString();
    }


    /// <summary>
    /// Creates a <see cref="RazorWireStreamResult"/> containing the builder's queued stream actions and associated controller.
    /// </summary>
    /// <param name="statusCode">Optional HTTP status code to apply to the response, commonly <c>422</c> for handled validation failures.</param>
    /// <returns>A <see cref="RazorWireStreamResult"/> initialized with a copy of the queued actions and the builder's controller.</returns>
    /// <remarks>
    /// If <see cref="FormError(string,string,string)"/> or
    /// <see cref="FormValidationErrors(string,ModelStateDictionary,string,int,string)"/> was queued, the result also
    /// emits the <see cref="Forms.RazorWireFormHeaders.FormHandled"/> response header. That header is the runtime
    /// contract that prevents the package default failed-form fallback from rendering on top of server-authored UI.
    /// </remarks>
    public RazorWireStreamResult BuildResult(int? statusCode = null)
    {
        return new RazorWireStreamResult(
            CreateActionSnapshot(),
            _controller,
            statusCode,
            formHandled: _hasFormError);
    }

    private void QueueDialogOpen(RazorWireDialogPayload payload)
    {
        if (HasActiveDialog)
        {
            throw new InvalidOperationException(
                $"A dialog is already queued. Use a ReplaceDialog* method to replace it or call CloseDialog() first. See {RazorWireRequestMetadata.DocumentationPath}.");
        }

        EnsureDialogSlot();
        _dialogPayload = payload;
    }

    private void ReplaceDialogPayload(RazorWireDialogPayload payload)
    {
        if (!HasActiveDialog)
        {
            throw new InvalidOperationException(
                $"No dialog open payload is queued. Call an OpenDialog* method first or check HasActiveDialog before replacing. See {RazorWireRequestMetadata.DocumentationPath}.");
        }

        _dialogPayload = payload;
    }

    private void EnsureDialogSlot()
    {
        _dialogSlotIndex ??= _actions.Count;
    }

    private List<IRazorWireStreamAction> CreateActionSnapshot()
    {
        var snapshot = new List<IRazorWireStreamAction>(_actions);
        if (_dialogSlotIndex is int dialogSlotIndex)
        {
            var command = _dialogPayload is null
                ? RazorWireDialogStreamAction.Close()
                : RazorWireDialogStreamAction.Open(_dialogPayload);
            snapshot.Insert(dialogSlotIndex, command);
        }

        return snapshot;
    }

    private static string BuildGeneratedFormErrorHtml(
        string title,
        string message,
        IReadOnlyList<RazorWireValidationError> validationErrors,
        string kind,
        int hiddenErrorCount = 0)
    {
        var encodedTitle = HtmlEncoder.Default.Encode(title);
        var encodedMessage = HtmlEncoder.Default.Encode(message);
        var encodedKind = HtmlEncoder.Default.Encode(kind);
        var validationMarkup = validationErrors.Count == 0
            ? string.Empty
            : "<ul data-rw-form-error-list=\"true\">"
              + string.Concat(validationErrors.Select(RenderValidationError))
              + "</ul>";
        var overflowMarkup = hiddenErrorCount <= 0
            ? string.Empty
            : $"<p data-rw-form-error-overflow=\"true\">{HtmlEncoder.Default.Encode($"There are {hiddenErrorCount} more validation errors.")}</p>";

        return $"""
                <div data-rw-form-error-generated="true" data-rw-form-error-kind="{encodedKind}" role="status" aria-live="polite" tabindex="-1">
                  <strong data-rw-form-error-title="true">{encodedTitle}</strong>
                  <p data-rw-form-error-message="true">{encodedMessage}</p>
                  {validationMarkup}
                  {overflowMarkup}
                </div>
                """;
    }

    private static string RenderValidationError(RazorWireValidationError error)
    {
        var fieldAttribute = string.IsNullOrEmpty(error.Key)
            ? string.Empty
            : $" data-rw-form-error-field=\"{HtmlEncoder.Default.Encode(error.Key)}\"";
        var fieldName = string.IsNullOrEmpty(error.Key)
            ? string.Empty
            : $"<span data-rw-form-error-field-name=\"true\">{HtmlEncoder.Default.Encode(error.Key)}</span>: ";

        return $"<li{fieldAttribute}>{fieldName}{HtmlEncoder.Default.Encode(error.Message)}</li>";
    }

    private static List<RazorWireValidationError> CollectModelStateErrors(ModelStateDictionary modelState)
    {
        var errors = new List<RazorWireValidationError>();
        foreach (var entry in modelState)
        {
            foreach (var error in entry.Value.Errors)
            {
                var message = string.IsNullOrWhiteSpace(error.ErrorMessage)
                    ? "The value is invalid."
                    : error.ErrorMessage;
                errors.Add(new RazorWireValidationError(entry.Key, message));
            }
        }

        return errors
            .OrderBy(error => error.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static void ValidateVisitUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (url.Any(IsAsciiControlCharacter))
        {
            throw new ArgumentException("Visit stream URLs cannot contain ASCII control characters.", nameof(url));
        }
    }

    private static bool IsAsciiControlCharacter(char value)
    {
        return value <= '\u001F' || value == '\u007F';
    }

    private sealed record RazorWireValidationError(string Key, string Message);

    private enum TemplateContentKind
    {
        PlainText,
        TrustedHtml
    }

    private class TemplateStreamAction : ISynchronousRazorWireStreamAction, IRazorWireTargetedStreamAction
    {
        public string Action { get; }
        public string Target { get; }
        public string? Content { get; }
        public TemplateContentKind ContentKind { get; }
        string IRazorWireTargetedStreamAction.Target => Target;

        /// <summary>
        /// Initializes a new instance with the specified turbo-stream action, target, and optional template content.
        /// </summary>
        /// <param name="action">The turbo-stream action name (e.g., "append", "prepend", "replace", "update", or "remove").</param>
        /// <param name="target">The DOM target selector or identifier that the action will be applied to.</param>
        /// <param name="content">The template content to use for the action; pass <c>null</c> for empty templates or actions that do not require a template.</param>
        /// <param name="contentKind">Whether the template content is plain text that must be encoded or trusted HTML that can be written as-is.</param>
        public TemplateStreamAction(
            string action,
            string target,
            string? content,
            TemplateContentKind contentKind)
        {
            Action = action;
            Target = target;
            Content = content;
            ContentKind = contentKind;
        }

        /// <summary>
        /// Renders the action as a turbo-stream HTML string.
        /// </summary>
        /// <returns>The turbo-stream element for the action and target; for action "remove" the element has no &lt;template&gt;, otherwise its &lt;template&gt; contains the action's HTML.</returns>
        public string Render()
        {
            return RazorWireStreamMarkup.RenderTargeted(
                Action,
                Target,
                RenderTemplateContent(),
                hasTemplate: Action != "remove");
        }

        /// <inheritdoc />
        public Task<string> RenderCorrelatedAsync(
            Microsoft.AspNetCore.Mvc.Rendering.ViewContext viewContext,
            RazorWireRequestMetadata metadata,
            RazorWireDialogPhase phase,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RazorWireStreamMarkup.RenderTargeted(
                Action,
                Target,
                RenderTemplateContent(),
                hasTemplate: Action != "remove",
                metadata,
                phase));
        }

        private string RenderTemplateContent()
        {
            var content = Content ?? string.Empty;

            return ContentKind switch
            {
                TemplateContentKind.PlainText => HtmlEncoder.Default.Encode(content),
                TemplateContentKind.TrustedHtml => content,
                _ => throw new InvalidOperationException("Unsupported RazorWire template content kind.")
            };
        }

        /// <summary>
        /// Renders the action as a turbo-stream HTML string.
        /// </summary>
        /// <param name="viewContext">The rendering context used when rendering the action.</param>
        /// <param name="cancellationToken">Cancellation token (ignored for raw HTML).</param>
        /// <returns>The turbo-stream element for the action and target; for action "remove" the element has no &lt;template&gt;, otherwise its &lt;template&gt; contains the action's HTML.</returns>
        public Task<string> RenderAsync(
            Microsoft.AspNetCore.Mvc.Rendering.ViewContext viewContext,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Render());
        }
    }

    private sealed class VisitStreamAction : ISynchronousRazorWireStreamAction
    {
        private readonly string _url;
        private readonly RazorWireVisitAction _action;

        public VisitStreamAction(string url, RazorWireVisitAction action)
        {
            ValidateVisitUrl(url);
            _action = action switch
            {
                RazorWireVisitAction.Advance or RazorWireVisitAction.Replace => action,
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported RazorWire visit action.")
            };
            _url = url;
        }

        public string Render()
        {
            var encodedUrl = HtmlEncoder.Default.Encode(_url);
            var encodedAction = HtmlEncoder.Default.Encode(ToWireValue(_action));

            return $"<turbo-stream action=\"rw-visit\" url=\"{encodedUrl}\" visit-action=\"{encodedAction}\"></turbo-stream>";
        }

        public Task<string> RenderAsync(
            Microsoft.AspNetCore.Mvc.Rendering.ViewContext viewContext,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Render());
        }

        private static string ToWireValue(RazorWireVisitAction action)
        {
            return action switch
            {
                RazorWireVisitAction.Advance => "advance",
                RazorWireVisitAction.Replace => "replace",
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported RazorWire visit action.")
            };
        }
    }
}
