using Microsoft.AspNetCore.Diagnostics;
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
/// Resolves and renders Runnable's conventional browser status page views.
/// </summary>
/// <remarks>
/// View resolution prefers the status-specific app/shared Razor Class Library path first, for example
/// <c>~/Views/Shared/403.cshtml</c>. If that view is missing, the renderer falls back to Runnable's shared
/// framework view. Resolved paths are cached per status code so a 401 override cannot accidentally satisfy
/// a later 403 or 404 render.
/// </remarks>
internal sealed class BrowserStatusPageRenderer
{
    private readonly IActionResultExecutor<ViewResult> _executor;
    private readonly ICompositeViewEngine _viewEngine;
    private readonly IModelMetadataProvider _metadataProvider;
    private readonly ILogger<BrowserStatusPageRenderer> _logger;

    private readonly object _resolvedViewPathsLock = new();
    private readonly Dictionary<int, string> _resolvedViewPaths = [];

    /// <summary>
    /// Initializes a renderer with the MVC services required to validate and execute browser status page views.
    /// </summary>
    /// <param name="executor">Executes the resolved <see cref="ViewResult"/> into the current response.</param>
    /// <param name="viewEngine">Resolves each app override view first, then the framework fallback view.</param>
    /// <param name="metadataProvider">Creates the <see cref="ViewDataDictionary"/> used for the status page model.</param>
    /// <param name="logger">Records which conventional status page view path was selected.</param>
    public BrowserStatusPageRenderer(
        IActionResultExecutor<ViewResult> executor,
        ICompositeViewEngine viewEngine,
        IModelMetadataProvider metadataProvider,
        ILogger<BrowserStatusPageRenderer> logger)
    {
        _executor = executor;
        _viewEngine = viewEngine;
        _metadataProvider = metadataProvider;
        _logger = logger;
    }

    /// <summary>
    /// Performs eager validation of all configured conventional browser status page views.
    /// </summary>
    /// <remarks>
    /// Call this during startup to fail fast if neither the conventional app/shared view nor the framework
    /// fallback view can be resolved for any supported status. Runtime rendering also resolves lazily, but this
    /// method turns a missing view into a predictable startup error instead of a request-time failure.
    /// </remarks>
    public void ValidateConfiguredViews()
    {
        foreach (var descriptor in BrowserStatusPageDescriptor.Supported)
        {
            _ = ResolveViewPath(descriptor);
        }
    }

    /// <summary>
    /// Renders the resolved conventional browser status page view into the current HTTP response.
    /// </summary>
    /// <param name="httpContext">The current request context used to build the model and execute the Razor view.</param>
    /// <remarks>
    /// The rendered model is always a <see cref="BrowserStatusPageModel"/>. Its status defaults to 404 when the
    /// request is a direct render without a re-execute feature or reserved-route status code. This method does
    /// not change <see cref="HttpResponse.StatusCode"/> itself, which lets direct previews keep their existing
    /// 200 response and re-executed status requests preserve their original HTTP status.
    /// </remarks>
    public async Task RenderAsync(HttpContext httpContext)
    {
        var model = CreateModel(httpContext);
        if (!BrowserStatusPageDescriptor.TryGet(model.StatusCode, out var descriptor))
        {
            descriptor = BrowserStatusPageDescriptor.NotFound;
            model = model with { StatusCode = descriptor.StatusCode };
        }

        var viewData = new ViewDataDictionary(_metadataProvider, new ModelStateDictionary())
        {
            Model = model
        };

        var result = new ViewResult
        {
            ViewName = ResolveViewPath(descriptor),
            ViewData = viewData
        };

        var actionContext = new ActionContext(
            httpContext,
            httpContext.GetRouteData() ?? new RouteData(),
            new ActionDescriptor());

        await _executor.ExecuteAsync(actionContext, result);
    }

    private string ResolveViewPath(BrowserStatusPageDescriptor descriptor)
    {
        lock (_resolvedViewPathsLock)
        {
            if (_resolvedViewPaths.TryGetValue(descriptor.StatusCode, out var resolvedViewPath))
            {
                return resolvedViewPath;
            }

            var appViewResult = _viewEngine.GetView(
                executingFilePath: null,
                viewPath: descriptor.AppViewPath,
                isMainPage: true);

            if (appViewResult.Success)
            {
                _resolvedViewPaths[descriptor.StatusCode] = descriptor.AppViewPath;
                _logger.LogInformation(
                    "Resolved conventional browser status page {StatusCode} view to {ViewPath}.",
                    descriptor.StatusCode,
                    descriptor.AppViewPath);
                return descriptor.AppViewPath;
            }

            var frameworkViewResult = _viewEngine.GetView(
                executingFilePath: null,
                viewPath: descriptor.FrameworkFallbackViewPath,
                isMainPage: true);

            if (frameworkViewResult.Success)
            {
                _resolvedViewPaths[descriptor.StatusCode] = descriptor.FrameworkFallbackViewPath;
                _logger.LogInformation(
                    "Resolved conventional browser status page {StatusCode} view to framework fallback {ViewPath}.",
                    descriptor.StatusCode,
                    descriptor.FrameworkFallbackViewPath);
                return descriptor.FrameworkFallbackViewPath;
            }

            var searchedLocations = appViewResult.SearchedLocations
                .Concat(frameworkViewResult.SearchedLocations)
                .Distinct()
                .ToArray();

            var locationsMessage = searchedLocations.Length == 0
                ? "No Razor view locations were reported."
                : string.Join(Environment.NewLine, searchedLocations);

            throw new InvalidOperationException(
                $"Runnable browser status pages are enabled for {descriptor.StatusCode}, but no Razor view could be resolved. Add '{descriptor.AppViewPath}' or restore the framework fallback view '{descriptor.FrameworkFallbackViewPath}'.{Environment.NewLine}Searched locations:{Environment.NewLine}{locationsMessage}");
        }
    }

    private static BrowserStatusPageModel CreateModel(HttpContext httpContext)
    {
        var reExecuteFeature = httpContext.Features.Get<IStatusCodeReExecuteFeature>();
        var statusCode = reExecuteFeature?.OriginalStatusCode
            ?? TryGetRouteStatusCode(httpContext)
            ?? StatusCodes.Status404NotFound;

        return new BrowserStatusPageModel(
            statusCode,
            reExecuteFeature?.OriginalPath,
            reExecuteFeature?.OriginalQueryString);
    }

    private static int? TryGetRouteStatusCode(HttpContext httpContext)
    {
        if (httpContext.GetRouteData()?.Values.TryGetValue("statusCode", out var value) != true)
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => null
        };
    }
}
