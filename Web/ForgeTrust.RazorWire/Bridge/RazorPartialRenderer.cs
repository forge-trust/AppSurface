using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// Implements <see cref="IRazorPartialRenderer"/> using the Razor view engine.
/// </summary>
internal class RazorPartialRenderer : IRazorPartialRenderer
{
    private readonly IRazorViewEngine _viewEngine;
    private readonly ITempDataDictionaryFactory _tempDataFactory;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="RazorPartialRenderer"/> class.
    /// </summary>
    /// <param name="viewEngine">The Razor view engine used to locate partials.</param>
    /// <param name="tempDataFactory">The factory used to create temporary data dictionaries for rendering.</param>
    /// <param name="serviceScopeFactory">The factory used to create service scopes for the rendering context.</param>
    public RazorPartialRenderer(
        IRazorViewEngine viewEngine,
        ITempDataDictionaryFactory tempDataFactory,
        IServiceScopeFactory serviceScopeFactory)
    {
        _viewEngine = viewEngine;
        _tempDataFactory = tempDataFactory;
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Because this method executes outside of an HTTP request context, it uses a blank <see cref="ActionContext"/>
    /// (with empty <see cref="RouteData"/> and <see cref="ActionDescriptor"/>). As a result, the
    /// <see cref="IRazorViewEngine"/> may not be able to locate views that are scoped to a specific controller
    /// (e.g., <c>Views/ControllerName/ViewName.cshtml</c>) unless the full path is provided (e.g., <c>~/Views/Reactivity/_MyPartial.cshtml</c>).
    /// Shared views (e.g., <c>Views/Shared/_MyPartial.cshtml</c>) are generally resolvable by name.
    /// </para>
    /// </remarks>
    public async Task<string> RenderPartialToStringAsync(
        string viewName,
        object? model = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        httpContext.RequestAborted = cancellationToken;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

        await using var sw = new StringWriter();
        var viewResult = _viewEngine.FindView(actionContext, viewName, isMainPage: false);

        if (!viewResult.Success)
        {
            var findViewLocations = viewResult.SearchedLocations;
            viewResult = _viewEngine.GetView(executingFilePath: null, viewPath: viewName, isMainPage: false);

            if (!viewResult.Success)
            {
                var searchedLocations = findViewLocations.Concat(viewResult.SearchedLocations);
                var locationsMessage = string.Join(Environment.NewLine, searchedLocations);

                throw new InvalidOperationException(
                    $"Could not find view with name '{viewName}'. Searched locations:{Environment.NewLine}{locationsMessage}");
            }
        }

        var viewDictionary =
            new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };

        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewDictionary,
            _tempDataFactory.GetTempData(httpContext),
            sw,
            new HtmlHelperOptions()
        );

        await viewResult.View.RenderAsync(viewContext);

        return sw.ToString();
    }
}
