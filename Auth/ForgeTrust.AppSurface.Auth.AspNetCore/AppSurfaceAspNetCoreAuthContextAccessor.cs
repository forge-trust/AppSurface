using ForgeTrust.AppSurface.Auth;
using Microsoft.AspNetCore.Http;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Provides the request-scoped AppSurface auth context mapped from the current ASP.NET Core <see cref="HttpContext" />.
/// </summary>
/// <remarks>
/// The accessor is intentionally passive: it reads the host-populated <see cref="HttpContext.User" /> and does not
/// authenticate, challenge, forbid, redirect, or register host schemes. The mapped snapshot is memoized for the
/// lifetime of the accessor scope so repeated module code sees a stable view of the request. Resolve it only from
/// an ASP.NET Core request scope; outside a request it returns a missing-services snapshot instead of throwing.
/// </remarks>
internal sealed class AppSurfaceAspNetCoreAuthContextAccessor : IAppSurfaceAspNetCoreAuthContextAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AppSurfaceAspNetCoreAuthContextMapper _mapper;
    private AppSurfaceAspNetCoreAuthContextSnapshot? _snapshot;

    /// <summary>
    /// Creates a request-context accessor that reads the current HTTP request and maps its principal.
    /// </summary>
    /// <param name="httpContextAccessor">ASP.NET Core accessor used to obtain the current request.</param>
    /// <param name="mapper">Mapper that converts the ASP.NET Core principal into an AppSurface snapshot.</param>
    public AppSurfaceAspNetCoreAuthContextAccessor(
        IHttpContextAccessor httpContextAccessor,
        AppSurfaceAspNetCoreAuthContextMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(mapper);

        _httpContextAccessor = httpContextAccessor;
        _mapper = mapper;
    }

    /// <summary>
    /// Gets the current request auth context snapshot, memoizing the result for the accessor scope.
    /// </summary>
    /// <returns>
    /// A successful anonymous or authenticated snapshot when a request principal can be mapped; otherwise a snapshot
    /// carrying a safe missing-services or missing-subject setup failure.
    /// </returns>
    /// <remarks>
    /// A missing <see cref="HttpContext" /> is reported as <see cref="AppSurfaceAuthReason.MissingServices" /> so
    /// consumers can distinguish host setup/order issues from authorization denials.
    /// </remarks>
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
