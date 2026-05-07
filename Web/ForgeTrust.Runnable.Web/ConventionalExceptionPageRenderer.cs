using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Web;

/// <summary>
/// Resolves and renders Runnable's conventional production exception page view.
/// </summary>
/// <remarks>
/// View resolution prefers <see cref="ConventionalExceptionPageDefaults.AppViewPath"/> first so apps and shared
/// Razor Class Libraries can override the page conventionally. If that view is missing, the renderer falls back to
/// <see cref="ConventionalExceptionPageDefaults.FrameworkFallbackViewPath"/>. The rendered model is deliberately
/// small and safe: status code plus request id only.
/// </remarks>
internal sealed class ConventionalExceptionPageRenderer
{
    private readonly IActionResultExecutor<ViewResult> _executor;
    private readonly ICompositeViewEngine _viewEngine;
    private readonly IModelMetadataProvider _metadataProvider;
    private readonly ILogger<ConventionalExceptionPageRenderer> _logger;

    private string? _resolvedViewPath;

    /// <summary>
    /// Initializes a renderer with the MVC services required to validate and execute the conventional 500 view.
    /// </summary>
    /// <param name="executor">Executes the resolved <see cref="ViewResult"/> into the current response.</param>
    /// <param name="viewEngine">Resolves the app override view first, then the framework fallback view.</param>
    /// <param name="metadataProvider">Creates the <see cref="ViewDataDictionary"/> used for the exception model.</param>
    /// <param name="logger">Records which conventional 500 view path was selected.</param>
    public ConventionalExceptionPageRenderer(
        IActionResultExecutor<ViewResult> executor,
        ICompositeViewEngine viewEngine,
        IModelMetadataProvider metadataProvider,
        ILogger<ConventionalExceptionPageRenderer> logger)
    {
        _executor = executor;
        _viewEngine = viewEngine;
        _metadataProvider = metadataProvider;
        _logger = logger;
    }

    /// <summary>
    /// Performs eager validation of the configured conventional 500 view.
    /// </summary>
    /// <remarks>
    /// Call this during startup to fail fast in production-like environments if neither the conventional app/shared
    /// view nor the framework fallback view can be resolved. Runtime rendering also resolves lazily, but this method
    /// turns a missing view into a predictable startup error instead of a request-time failure.
    /// </remarks>
    public void ValidateConfiguredViews()
    {
        _ = ResolveViewPath();
    }

    /// <summary>
    /// Renders the resolved conventional 500 view into the current HTTP response.
    /// </summary>
    /// <param name="httpContext">The current request context used to build the model and execute the Razor view.</param>
    /// <remarks>
    /// This method sets the response status to 500 and passes only <see cref="ExceptionPageModel"/> to the view. It
    /// intentionally does not inspect <c>IExceptionHandlerFeature</c> or request data, so exception messages, stack
    /// traces, headers, cookies, route values, and form values cannot be disclosed through the default model.
    /// </remarks>
    public async Task RenderAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var viewData = new ViewDataDictionary(_metadataProvider, new ModelStateDictionary())
        {
            Model = CreateModel(httpContext)
        };

        var result = new ViewResult
        {
            ViewName = ResolveViewPath(),
            ViewData = viewData
        };

        var actionContext = new ActionContext(
            httpContext,
            httpContext.GetRouteData() ?? new RouteData(),
            new ActionDescriptor());

        await _executor.ExecuteAsync(actionContext, result);
    }

    private string ResolveViewPath()
    {
        if (_resolvedViewPath is not null)
        {
            return _resolvedViewPath;
        }

        var appViewResult = _viewEngine.GetView(
            executingFilePath: null,
            viewPath: ConventionalExceptionPageDefaults.AppViewPath,
            isMainPage: true);

        if (appViewResult.Success)
        {
            _resolvedViewPath = ConventionalExceptionPageDefaults.AppViewPath;
            _logger.LogInformation(
                "Resolved conventional 500 view to {ViewPath}.",
                _resolvedViewPath);
            return _resolvedViewPath;
        }

        var frameworkViewResult = _viewEngine.GetView(
            executingFilePath: null,
            viewPath: ConventionalExceptionPageDefaults.FrameworkFallbackViewPath,
            isMainPage: true);

        if (frameworkViewResult.Success)
        {
            _resolvedViewPath = ConventionalExceptionPageDefaults.FrameworkFallbackViewPath;
            _logger.LogInformation(
                "Resolved conventional 500 view to framework fallback {ViewPath}.",
                _resolvedViewPath);
            return _resolvedViewPath;
        }

        var searchedLocations = appViewResult.SearchedLocations
            .Concat(frameworkViewResult.SearchedLocations)
            .Distinct()
            .ToArray();

        var locationsMessage = searchedLocations.Length == 0
            ? "No Razor view locations were reported."
            : string.Join(Environment.NewLine, searchedLocations);

        throw new InvalidOperationException(
            $"Runnable conventional 500 pages are enabled, but neither '{ConventionalExceptionPageDefaults.AppViewPath}' nor '{ConventionalExceptionPageDefaults.FrameworkFallbackViewPath}' could be resolved.{Environment.NewLine}{locationsMessage}");
    }

    private static ExceptionPageModel CreateModel(HttpContext httpContext)
    {
        var requestId = httpContext.TraceIdentifier;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            requestId = Activity.Current?.Id ?? string.Empty;
        }

        return new ExceptionPageModel(StatusCodes.Status500InternalServerError, requestId);
    }
}
