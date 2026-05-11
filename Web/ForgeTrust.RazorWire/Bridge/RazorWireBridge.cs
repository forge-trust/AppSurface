using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// Provides static methods for creating Turbo Frame results and RazorWire stream builders.
/// </summary>
public static class RazorWireBridge
{
    /// <summary>
    /// Creates a partial result that renders a turbo frame containing the specified inner partial view and model.
    /// </summary>
    /// <remarks>
    /// Also exposes the frame identifier by setting <c>controller.ViewData["TurboFrameId"]</c>.
    /// </remarks>
    /// <param name="controller">The controller used to produce the partial view result.</param>
    /// <param name="id">The identifier to assign to the turbo frame.</param>
    /// <param name="partialView">The name of the inner partial view to render inside the frame.</param>
    /// <param name="model">An optional model to pass to the inner partial view.</param>
    /// <returns>A <see cref="PartialViewResult"/> that renders the <c>RazorWire/_TurboFrame</c> partial populated with the specified id, partial view, and model.</returns>
    public static PartialViewResult Frame(
        Controller controller,
        string id,
        string partialView,
        object? model = null)
    {
        controller.ViewData["TurboFrameId"] = id;

        return controller.PartialView(
            "RazorWire/_TurboFrame",
            new TurboFrameViewModel
            {
                Id = id,
                PartialView = partialView,
                Model = model
            });
    }

    /// <summary>
    /// Creates a partial result that renders a Turbo Frame containing the specified view component.
    /// The frame identifier is exposed via <c>Controller.ViewData["TurboFrameId"]</c>.
    /// </summary>
    /// <param name="controller">The controller instance from which to produce results.</param>
    /// <param name="id">Identifier for the turbo frame.</param>
    /// <param name="componentName">Name of the view component to render inside the frame.</param>
    /// <param name="model">Optional model to pass to the view component.</param>
    /// <returns>A <see cref="PartialViewResult"/> that renders the <c>RazorWire/_TurboFrame</c> partial with a <see cref="TurboFrameViewModel"/>.</returns>
    public static PartialViewResult FrameComponent(
        Controller controller,
        string id,
        string componentName,
        object? model = null)
    {
        controller.ViewData["TurboFrameId"] = id;

        return controller.PartialView(
            "RazorWire/_TurboFrame",
            new TurboFrameViewModel
            {
                Id = id,
                ViewComponent = componentName,
                Model = model
            });
    }

    /// <summary>
    /// Creates a new <see cref="RazorWireStreamBuilder"/> for fluidly configuring and producing Turbo Stream actions.
    /// </summary>
    /// <returns>A new <see cref="RazorWireStreamBuilder"/> instance ready to configure stream updates.</returns>
    public static RazorWireStreamBuilder CreateStream() => new();

    /// <summary>
    /// Creates a <see cref="Microsoft.AspNetCore.Mvc.Rendering.ViewContext"/> configured to render outside of a regular view using the controller's context and data.
    /// </summary>
    /// <param name="controller">The controller whose context and data are used as the basis for the new <see cref="Microsoft.AspNetCore.Mvc.Rendering.ViewContext"/>.</param>
    /// <returns>A <see cref="Microsoft.AspNetCore.Mvc.Rendering.ViewContext"/> configured with the controller's data and a no-op view.</returns>
    public static Microsoft.AspNetCore.Mvc.Rendering.ViewContext CreateViewContext(this Controller controller)
    {
        var services = controller.HttpContext.RequestServices;
        var tempDataProvider =
            services.GetRequiredService<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory>();

        return new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(
            controller.ControllerContext,
            new NullView(),
            controller.ViewData,
            tempDataProvider.GetTempData(controller.HttpContext),
            TextWriter.Null,
            new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions()
        );
    }

    private class NullView : Microsoft.AspNetCore.Mvc.ViewEngines.IView
    {
        public string Path => string.Empty;

        /// <summary>
        /// A no-op view that does not render any content.
        /// </summary>
        /// <param name="viewContext">The view context provided for rendering; this implementation ignores it.</param>
        /// <returns>A completed <see cref="Task"/>.</returns>
        public Task RenderAsync(Microsoft.AspNetCore.Mvc.Rendering.ViewContext viewContext) => Task.CompletedTask;
    }
}

/// <summary>
/// Data model used for rendering the Turbo Frame partial view.
/// </summary>
public class TurboFrameViewModel
{
    /// <summary>
    /// Gets or sets the unique identifier for the Turbo Frame.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Gets or sets the name of the partial view to render inside the frame, if any.
    /// </summary>
    public string? PartialView { get; set; }

    /// <summary>
    /// Gets or sets the name of the view component to render inside the frame, if any.
    /// </summary>
    public string? ViewComponent { get; set; }

    /// <summary>
    /// Gets or sets the optional model to pass to the partial view or view component.
    /// </summary>
    public object? Model { get; set; }
}
