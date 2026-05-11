using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// Internal helper for rendering view components into Turbo Stream fragments.
/// </summary>
internal static class ViewComponentStreamHelper
{
    /// <summary>
    /// Renders a view component into a Turbo Stream XML fragment.
    /// </summary>
    /// <param name="viewContext">The current Razor view context used as the basis for rendering.</param>
    /// <param name="action">The Turbo Stream action (e.g., "replace", "append").</param>
    /// <param name="target">The DOM element identifier to update.</param>
    /// <param name="componentIdentifier">The view component to invoke; either a CLR <see cref="Type"/> or the component's string name.</param>
    /// <param name="arguments">Optional arguments to pass to the view component.</param>
    /// <returns>A Turbo Stream XML fragment whose `action` and `target` attributes are HTML-encoded and whose &lt;template&gt; contains the rendered component HTML.</returns>
    public static async Task<string> RenderComponentStreamAsync(
        ViewContext viewContext,
        string action,
        string target,
        dynamic componentIdentifier,
        object? arguments)
    {
        await using var writer = new StringWriter();

        var services = viewContext.HttpContext.RequestServices;
        var tempDataProvider = services.GetRequiredService<ITempDataDictionaryFactory>();

        var componentViewContext = new ViewContext(
            viewContext,
            viewContext.View,
            viewContext.ViewData,
            tempDataProvider.GetTempData(viewContext.HttpContext),
            writer,
            new HtmlHelperOptions()
        );
        var viewComponentHelper = services.GetRequiredService<IViewComponentHelper>();

        ((IViewContextAware)viewComponentHelper).Contextualize(componentViewContext);

        var result = await viewComponentHelper.InvokeAsync(componentIdentifier, arguments);
        result.WriteTo(writer, HtmlEncoder.Default);

        var content = writer.ToString();
        var encodedTarget = HtmlEncoder.Default.Encode(target);
        var encodedAction = HtmlEncoder.Default.Encode(action);

        return
            $"<turbo-stream action=\"{encodedAction}\" target=\"{encodedTarget}\"><template>{content}</template></turbo-stream>";
    }
}

/// <summary>
/// A Turbo Stream action that renders a view component by its <see cref="Type"/>.
/// </summary>
public class ViewComponentStreamAction : IRazorWireStreamAction
{
    private readonly string _action;
    private readonly string _target;
    private readonly Type _componentType;
    private readonly object? _arguments;

    /// <summary>
    /// Creates an action that renders the specified view component type into a Turbo Stream fragment targeting the given element.
    /// </summary>
    /// <param name="action">The Turbo Stream action to perform (e.g., "replace", "append", "prepend").</param>
    /// <param name="target">The DOM element id or selector to update.</param>
    /// <param name="componentType">The CLR <see cref="Type"/> of the view component to invoke.</param>
    /// <param name="arguments">Optional arguments to pass to the view component when invoking it.</param>
    public ViewComponentStreamAction(
        string action,
        string target,
        Type componentType,
        object? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(componentType);

        _action = action;
        _target = target;
        _componentType = componentType;
        _arguments = arguments;
    }

    /// <summary>
    /// Renders the configured view component into a Turbo Stream fragment.
    /// </summary>
    /// <param name="viewContext">The current MVC view context used to execute and render the view component.</param>
    /// <param name="cancellationToken">A token to observe while deciding whether to proceed.</param>
    /// <returns>A string containing a &lt;turbo-stream&gt; element whose <c>action</c> and <c>target</c> attributes are HTML-encoded and whose &lt;template&gt; contains the component's rendered HTML.</returns>
    public async Task<string> RenderAsync(ViewContext viewContext, CancellationToken cancellationToken = default)
    {
        return await ViewComponentStreamHelper.RenderComponentStreamAsync(
            viewContext,
            _action,
            _target,
            _componentType,
            _arguments);
    }
}

/// <summary>
/// A Turbo Stream action that renders a view component by its name.
/// </summary>
public class ViewComponentByNameStreamAction : IRazorWireStreamAction
{
    private readonly string _action;
    private readonly string _target;
    private readonly string _componentName;
    private readonly object? _arguments;

    /// <summary>
    /// Creates an action that renders a named view component into a Turbo Stream fragment targeting the specified element.
    /// </summary>
    /// <param name="action">Turbo Stream action to perform (e.g., "replace", "append", "prepend").</param>
    /// <param name="target">DOM element id or selector to update.</param>
    /// <param name="componentName">Name of the view component to invoke.</param>
    /// <param name="arguments">Optional arguments to pass to the view component; may be null.</param>
    public ViewComponentByNameStreamAction(
        string action,
        string target,
        string componentName,
        object? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(componentName);

        _action = action;
        _target = target;
        _componentName = componentName;
        _arguments = arguments;
    }

    /// <summary>
    /// Render the configured view component (by name) into a Turbo Stream fragment.
    /// </summary>
    /// <param name="viewContext">The current Razor view context used to execute and render the view component.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A string containing a &lt;turbo-stream&gt; element whose action and target attributes are HTML-encoded and whose &lt;template&gt; contains the rendered component HTML.</returns>
    public async Task<string> RenderAsync(ViewContext viewContext, CancellationToken cancellationToken = default)
    {
        return await ViewComponentStreamHelper.RenderComponentStreamAsync(
            viewContext,
            _action,
            _target,
            _componentName,
            _arguments);
    }
}
