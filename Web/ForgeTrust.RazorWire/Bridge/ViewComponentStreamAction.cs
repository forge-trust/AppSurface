using System.Linq;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
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
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A Turbo Stream XML fragment whose `action` and `target` attributes are HTML-encoded and whose &lt;template&gt; contains the rendered component HTML.</returns>
    public static async Task<string> RenderComponentStreamAsync(
        ViewContext viewContext,
        string action,
        string target,
        object componentIdentifier,
        object? arguments,
        CancellationToken cancellationToken = default)
    {
        var content = await RenderComponentContentAsync(viewContext, componentIdentifier, arguments, cancellationToken);
        return RazorWireStreamMarkup.RenderTargeted(action, target, content, hasTemplate: true);
    }

    /// <summary>
    /// Renders a view component body without adding an outer Turbo Stream element.
    /// </summary>
    /// <param name="viewContext">The current Razor view context used as the basis for rendering.</param>
    /// <param name="componentIdentifier">The component type or name.</param>
    /// <param name="arguments">Optional arguments passed to the component.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The rendered component HTML.</returns>
    public static async Task<string> RenderComponentContentAsync(
        ViewContext viewContext,
        object componentIdentifier,
        object? arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewContext);
        ArgumentNullException.ThrowIfNull(componentIdentifier);
        cancellationToken.ThrowIfCancellationRequested();

        await using var writer = new StringWriter();

        var services = viewContext.HttpContext.RequestServices;
        var tempDataProvider = services.GetRequiredService<ITempDataDictionaryFactory>();

        if (componentIdentifier is Type requestedType
            && services.GetService<IViewComponentDescriptorCollectionProvider>() is { } descriptorProvider
            && !descriptorProvider.ViewComponents.Items.Any(descriptor => descriptor.TypeInfo.AsType() == requestedType))
        {
            var componentName = requestedType.FullName ?? requestedType.Name;
            var mvcMessage = $"MVC did not discover a view component whose type is '{componentName}'.";
            throw new InvalidOperationException(
                $"The view component '{componentName}' could not be resolved by MVC. {mvcMessage} "
                + $"Check the component type and application discovery. See {RazorWireRequestMetadata.DocumentationPath}.",
                new InvalidOperationException(mvcMessage));
        }

        if (componentIdentifier is string requestedName
            && services.GetService<IViewComponentSelector>() is { } selector)
        {
            ViewComponentDescriptor? descriptor;
            try
            {
                descriptor = selector.SelectComponent(requestedName);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"The view component '{requestedName}' could not be resolved by MVC. "
                    + $"MVC reported: {exception.Message} See {RazorWireRequestMetadata.DocumentationPath}.",
                    exception);
            }

            if (descriptor is null)
            {
                throw CreateMissingComponentException(
                    requestedName,
                    $"MVC did not discover a view component named '{requestedName}'.");
            }
        }

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

        var result = componentIdentifier switch
        {
            Type componentType => await viewComponentHelper.InvokeAsync(componentType, arguments),
            string componentName => await viewComponentHelper.InvokeAsync(componentName, arguments),
            _ => throw new ArgumentException("A view component identifier must be a component Type or name.", nameof(componentIdentifier))
        };

        cancellationToken.ThrowIfCancellationRequested();

        result.WriteTo(writer, HtmlEncoder.Default);
        cancellationToken.ThrowIfCancellationRequested();
        return writer.ToString();
    }

    private static InvalidOperationException CreateMissingComponentException(string componentName, string mvcMessage)
    {
        return new InvalidOperationException(
            $"The view component '{componentName}' could not be resolved by MVC. {mvcMessage} "
            + $"Check the component name and application discovery. See {RazorWireRequestMetadata.DocumentationPath}.",
            new InvalidOperationException(mvcMessage));
    }
}

/// <summary>
/// A Turbo Stream action that renders a view component by its <see cref="Type"/>.
/// </summary>
public class ViewComponentStreamAction : IRazorWireTargetedStreamAction
{
    private readonly string _action;
    private readonly string _target;
    private readonly Type _componentType;
    private readonly object? _arguments;

    string IRazorWireTargetedStreamAction.Target => _target;

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
            _arguments,
            cancellationToken);
    }

    /// <inheritdoc />
    async Task<string> IRazorWireTargetedStreamAction.RenderCorrelatedAsync(
        ViewContext viewContext,
        RazorWireRequestMetadata metadata,
        RazorWireDialogPhase phase,
        CancellationToken cancellationToken)
    {
        var content = await ViewComponentStreamHelper.RenderComponentContentAsync(
            viewContext,
            _componentType,
            _arguments,
            cancellationToken);
        return RazorWireStreamMarkup.RenderTargeted(
            _action,
            _target,
            content,
            hasTemplate: true,
            metadata,
            phase);
    }
}

/// <summary>
/// A Turbo Stream action that renders a view component by its name.
/// </summary>
public class ViewComponentByNameStreamAction : IRazorWireTargetedStreamAction
{
    private readonly string _action;
    private readonly string _target;
    private readonly string _componentName;
    private readonly object? _arguments;

    string IRazorWireTargetedStreamAction.Target => _target;

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
            _arguments,
            cancellationToken);
    }

    /// <inheritdoc />
    async Task<string> IRazorWireTargetedStreamAction.RenderCorrelatedAsync(
        ViewContext viewContext,
        RazorWireRequestMetadata metadata,
        RazorWireDialogPhase phase,
        CancellationToken cancellationToken)
    {
        var content = await ViewComponentStreamHelper.RenderComponentContentAsync(
            viewContext,
            _componentName,
            _arguments,
            cancellationToken);
        return RazorWireStreamMarkup.RenderTargeted(
            _action,
            _target,
            content,
            hasTemplate: true,
            metadata,
            phase);
    }
}
