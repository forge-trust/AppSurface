using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// A Turbo Stream action that renders a partial view as its content.
/// </summary>
public class PartialViewStreamAction : IRazorWireStreamAction
{
    private readonly string _action;
    private readonly string _target;
    private readonly string _viewName;
    private readonly object? _model;

    /// <summary>
    /// Initializes a new <see cref="PartialViewStreamAction"/> configured to render the specified partial view and wrap its output in a Turbo Stream element.
    /// </summary>
    /// <param name="action">The Turbo Stream action to apply (for example, "replace", "append", or "update").</param>
    /// <param name="target">The identifier of the target element the Turbo Stream will affect.</param>
    /// <param name="viewName">The name or path of the partial view to render.</param>
    /// <param name="model">An optional model to supply to the partial view; may be null.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="action"/>, <paramref name="target"/>, or <paramref name="viewName"/> is null, empty, or consists only of whitespace.</exception>
    public PartialViewStreamAction(
        string action,
        string target,
        string viewName,
        object? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);

        _action = action;
        _target = target;
        _viewName = viewName;
        _model = model;
    }

    /// <summary>
    /// Renders the configured partial view and wraps its output in a Turbo Stream element.
    /// </summary>
    /// <param name="viewContext">The current view context used to locate services and render the partial view.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>The turbo-stream HTML string containing the rendered partial inside a &lt;template&gt; element.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the partial view cannot be located.</exception>
    public async Task<string> RenderAsync(ViewContext viewContext, CancellationToken cancellationToken = default)
    {
        var services = viewContext.HttpContext.RequestServices;
        var viewEngine = services.GetRequiredService<ICompositeViewEngine>();
        var tempDataProvider = services.GetRequiredService<ITempDataDictionaryFactory>();

        // Preserve parent context (ViewBag/ViewData) and just override the Model
        var viewData = new ViewDataDictionary(viewContext.ViewData) { Model = _model };

        await using var writer = new StringWriter();

        var viewResult = viewEngine.FindView(viewContext, _viewName, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.GetView(executingFilePath: null, _viewName, isMainPage: false);
        }

        if (!viewResult.Success)
        {
            throw new InvalidOperationException($"The partial view '{_viewName}' was not found.");
        }

        var partialViewContext = new ViewContext(
            viewContext,
            viewResult.View,
            viewData,
            tempDataProvider.GetTempData(viewContext.HttpContext),
            writer,
            new HtmlHelperOptions()
        );

        // We can check cancellation before rendering
        cancellationToken.ThrowIfCancellationRequested();

        await viewResult.View.RenderAsync(partialViewContext);
        var content = writer.ToString();

        var encodedTarget = HtmlEncoder.Default.Encode(_target);
        var encodedAction = HtmlEncoder.Default.Encode(_action);

        return
            $"<turbo-stream action=\"{encodedAction}\" target=\"{encodedTarget}\"><template>{content}</template></turbo-stream>";
    }
}
