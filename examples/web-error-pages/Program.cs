using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;

await WebApp<ErrorPagesProofModule>.RunAsync(
    args,
    options =>
    {
        // docs:snippet web-error-page-options:start
        options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.Controllers };
        options.Errors.UseConventionalBrowserStatusPages();
        options.Errors.UseConventionalExceptionPage();
        // docs:snippet web-error-page-options:end

        options.MapEndpoints = endpoints =>
        {
            endpoints.MapGet(
                "/",
                () => Results.Text("AppSurface Web error-page proof is running.", "text/plain"));

            MapEmptyStatus(endpoints, "/empty-401", StatusCodes.Status401Unauthorized);
            MapEmptyStatus(endpoints, "/empty-403", StatusCodes.Status403Forbidden);
            MapEmptyStatus(endpoints, "/empty-404", StatusCodes.Status404NotFound);

            endpoints.MapGet(
                "/api/not-found",
                () => Results.Json(
                    new
                    {
                        status = StatusCodes.Status404NotFound,
                        route = "/api/not-found"
                    },
                    statusCode: StatusCodes.Status404NotFound));

            endpoints.MapGet(
                "/throws",
                () => ThrowProofException("synthetic-browser-exception-secret"));

            endpoints.MapGet(
                "/api/throws",
                () => ThrowProofException("synthetic-api-exception-secret"));

            endpoints.MapPost(
                "/throws/{routeValue}",
                async (string routeValue, HttpContext httpContext) =>
                {
                    var headerValue = httpContext.Request.Headers["X-Proof-Sentinel"].ToString();
                    var cookieValue = httpContext.Request.Cookies["proof-cookie"] ?? string.Empty;
                    var formValue = string.Empty;

                    if (httpContext.Request.HasFormContentType)
                    {
                        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
                        formValue = form["proof-form"].ToString();
                    }

                    throw new InvalidOperationException(
                        $"synthetic-post-exception-secret route={routeValue} header={headerValue} cookie={cookieValue} form={formValue}");
                });
        };
    });

static IResult ThrowProofException(string message)
{
    throw new InvalidOperationException(message);
}

static void MapEmptyStatus(IEndpointRouteBuilder endpoints, string pattern, int statusCode)
{
    endpoints.MapGet(
        pattern,
        httpContext =>
        {
            httpContext.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });
}

internal sealed class ErrorPagesProofModule : IAppSurfaceWebModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}
