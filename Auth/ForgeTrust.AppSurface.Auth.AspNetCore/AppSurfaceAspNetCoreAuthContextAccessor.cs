using ForgeTrust.AppSurface.Auth;
using Microsoft.AspNetCore.Http;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

internal sealed class AppSurfaceAspNetCoreAuthContextAccessor : IAppSurfaceAspNetCoreAuthContextAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AppSurfaceAspNetCoreAuthContextMapper _mapper;
    private AppSurfaceAspNetCoreAuthContextSnapshot? _snapshot;

    public AppSurfaceAspNetCoreAuthContextAccessor(
        IHttpContextAccessor httpContextAccessor,
        AppSurfaceAspNetCoreAuthContextMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(mapper);

        _httpContextAccessor = httpContextAccessor;
        _mapper = mapper;
    }

    public AppSurfaceAspNetCoreAuthContextSnapshot GetCurrentContext()
    {
        if (_snapshot is not null)
        {
            return _snapshot;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            _snapshot = new AppSurfaceAspNetCoreAuthContextSnapshot(
                AppSurfaceAuthContext.Anonymous,
                AppSurfaceAuthResult.MissingServices(
                    AppSurfaceAuthContext.Anonymous,
                    "No current ASP.NET Core HTTP request is available. Resolve AppSurface ASP.NET Core auth services inside a request.",
                    AppSurfaceAspNetCoreAuthDiagnostics.MissingService(
                        typeof(HttpContext),
                        diagnosticCode: "missing_http_context")));
            return _snapshot;
        }

        _snapshot = _mapper.Map(httpContext.User);
        return _snapshot;
    }
}
