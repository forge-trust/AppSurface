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
    /// Queues an append action that inserts the provided HTML into the specified target element.
    /// </summary>
    /// <param name="target">The target DOM selector or element identifier to which the HTML will be appended.</param>
    /// <param name="templateHtml">The HTML fragment to append inside the target's template.</param>
    /// <returns>The same <see cref="RazorWireStreamBuilder"/> instance to allow fluent chaining.</returns>
    public RazorWireStreamBuilder Append(string target, string templateHtml)
    {
        _actions.Add(new RawHtmlStreamAction("append", target, templateHtml));

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
    /// Queues a raw HTML prepend action targeting the specified DOM element.
    /// </summary>
    /// <param name="target">The DOM target selector or identifier to receive the content.</param>
    /// <param name="templateHtml">The HTML content to insert before the target element's existing content.</param>
    /// <returns>The builder instance for fluent chaining.</returns>
    public RazorWireStreamBuilder Prepend(string target, string templateHtml)
    {
        _actions.Add(new RawHtmlStreamAction("prepend", target, templateHtml));

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
    /// Queues a raw HTML replace action targeting the specified DOM element.
    /// </summary>
    /// <param name="target">The DOM element selector or identifier to target.</param>
    /// <param name="templateHtml">The HTML content used to replace the target's contents.</param>
    /// <returns>The current RazorWireStreamBuilder instance.</returns>
    public RazorWireStreamBuilder Replace(string target, string templateHtml)
    {
        _actions.Add(new RawHtmlStreamAction("replace", target, templateHtml));

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
    /// Queues a raw HTML "update" turbo-stream action for the specified DOM target using the provided HTML template.
    /// </summary>
    /// <param name="target">The DOM target selector or identifier to apply the update to.</param>
    /// <param name="templateHtml">The HTML fragment to use as the action's template.</param>
    /// <returns>The same RazorWireStreamBuilder instance for fluent chaining.</returns>
    public RazorWireStreamBuilder Update(string target, string templateHtml)
    {
        _actions.Add(new RawHtmlStreamAction("update", target, templateHtml));

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
    /// Queues a remove action targeting the specified DOM element.
    /// </summary>
    /// <param name="target">The DOM target selector or identifier whose element will be removed.</param>
    /// <returns>The current <see cref="RazorWireStreamBuilder"/> instance for fluent chaining.</returns>
    public RazorWireStreamBuilder Remove(string target)
    {
        _actions.Add(new RawHtmlStreamAction("remove", target, null));

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
        _actions.Add(new RawHtmlStreamAction(
            "update",
            target,
            BuildGeneratedFormErrorHtml(title, message, [], "server")));

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

        _actions.Add(new RawHtmlStreamAction(
            "update",
            target,
            BuildGeneratedFormErrorHtml(
                title,
                fallbackMessage,
                visibleErrors,
                "validation",
                hiddenCount)));

        return this;
    }

    /// <summary>
    /// Builds a single concatenated Turbo Stream markup string from the queued raw HTML actions.
    /// </summary>
    /// <returns>The concatenated Turbo Stream markup representing the queued raw HTML actions.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the builder contains actions that require asynchronous rendering (such as partial views or view components); use RenderAsync(viewContext) or BuildResult() instead.</exception>
    public string Build()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var action in _actions)
        {
            if (action is RawHtmlStreamAction raw)
            {
                var encodedTarget = HtmlEncoder.Default.Encode(raw.Target);
                var encodedAction = HtmlEncoder.Default.Encode(raw.Action);
                if (raw.Action == "remove")
                    sb.Append($"<turbo-stream action=\"remove\" target=\"{encodedTarget}\"></turbo-stream>");
                else
                    sb.Append(
                        $"<turbo-stream action=\"{encodedAction}\" target=\"{encodedTarget}\"><template>{raw.Html}</template></turbo-stream>");
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
    public async Task<string> RenderAsync(
        Microsoft.AspNetCore.Mvc.Rendering.ViewContext viewContext,
        CancellationToken cancellationToken = default)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var action in _actions)
        {
            var html = await action.RenderAsync(viewContext, cancellationToken);
            sb.Append(html);
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
            _actions.ToList(),
            _controller,
            statusCode,
            formHandled: _hasFormError);
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

    private sealed record RazorWireValidationError(string Key, string Message);

    private class RawHtmlStreamAction : IRazorWireStreamAction
    {
        public string Action { get; }
        public string Target { get; }
        public string? Html { get; }

        /// <summary>
        /// Initializes a new instance with the specified turbo-stream action, target, and optional HTML template.
        /// </summary>
        /// <param name="action">The turbo-stream action name (e.g., "append", "prepend", "replace", "update", or "remove").</param>
        /// <param name="target">The DOM target selector or identifier that the action will be applied to.</param>
        /// <param name="html">The HTML template to use for the action; pass <c>null</c> for actions that do not require a template (such as "remove").</param>
        public RawHtmlStreamAction(string action, string target, string? html)
        {
            Action = action;
            Target = target;
            Html = html;
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
            var encodedTarget = HtmlEncoder.Default.Encode(Target);
            var encodedAction = HtmlEncoder.Default.Encode(Action);
            if (Action == "remove")
            {
                return Task.FromResult(
                    $"<turbo-stream action=\"remove\" target=\"{encodedTarget}\">"
                    + $"</turbo-stream>");
            }

            return Task.FromResult(
                $"<turbo-stream action=\"{encodedAction}\" target=\"{encodedTarget}\">"
                + $"<template>{Html}</template></turbo-stream>");
        }
    }
}
