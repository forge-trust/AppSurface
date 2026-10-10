using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>Renders the single owned-failure view through the existing Docs shell before committing any bytes.</summary>
internal static class AppSurfaceDocsAliasRecoveryRenderer
{
    /// <summary>Buffers the normal Razor view so GET and HEAD share headers and no partial recovery can be sent.</summary>
    /// <param name="context">Authorized context carrying the actual Docs runtime endpoint metadata.</param>
    /// <param name="archiveRoot">App-relative family archive URL; Razor adds PathBase once and encodes it.</param>
    /// <returns>The shell-rendered generic recovery document.</returns>
    internal static async Task<string> RenderAsync(HttpContext context, string archiveRoot)
    {
        var viewEngine = context.RequestServices.GetRequiredService<ICompositeViewEngine>();
        var result = viewEngine.GetView(null, "/Views/Docs/AliasNotFound.cshtml", isMainPage: true);
        if (!result.Success)
        {
            throw new InvalidOperationException("The AppSurface Docs alias recovery view is missing from the application parts.");
        }

        var routeData = new RouteData();
        routeData.Values["controller"] = "Docs";
        routeData.Values["action"] = "AliasNotFound";
        var action = new ActionContext(context, routeData, new ActionDescriptor());
        var metadata = context.RequestServices.GetRequiredService<IModelMetadataProvider>();
        var data = new ViewDataDictionary(metadata, new ModelStateDictionary()) { Model = archiveRoot };
        var tempData = new TempDataDictionary(context, context.RequestServices.GetRequiredService<ITempDataProvider>());
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var view = new ViewContext(action, result.View, data, tempData, writer, new HtmlHelperOptions());
        await result.View.RenderAsync(view);
        return writer.ToString();
    }
}
