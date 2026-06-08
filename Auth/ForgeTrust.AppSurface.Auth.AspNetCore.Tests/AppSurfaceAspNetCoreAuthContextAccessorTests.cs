using System.Security.Claims;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Tests;

public sealed class AppSurfaceAspNetCoreAuthContextAccessorTests
{
    [Fact]
    public void GetCurrentContext_WithoutHttpContext_ReturnsMissingServices()
    {
        using var scope = CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IAppSurfaceAspNetCoreAuthContextAccessor>();

        var snapshot = accessor.GetCurrentContext();

        Assert.False(snapshot.Succeeded);
        Assert.Equal(AppSurfaceAuthReason.MissingServices, snapshot.Failure?.Reason);
        Assert.Equal("missing_http_context", snapshot.Failure?.Metadata[AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode]);
    }

    [Fact]
    public void GetCurrentContext_WithUnauthenticatedIdentity_ReturnsAnonymousSuccess()
    {
        using var scope = CreateScope();
        SetHttpContext(scope, new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "ignored")], authenticationType: null)));

        var snapshot = scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCoreAuthContextAccessor>()
            .GetCurrentContext();

        Assert.True(snapshot.Succeeded);
        Assert.False(snapshot.Context.IsAuthenticated);
    }

    [Theory]
    [InlineData(ClaimTypes.NameIdentifier, "name-id-subject")]
    [InlineData("sub", "oidc-subject")]
    [InlineData(AppSurfaceAuthMetadataKeys.SubjectId, "appsurface-subject")]
    public void GetCurrentContext_UsesDefaultSubjectClaimOrder(string claimType, string expectedSubject)
    {
        using var scope = CreateScope();
        SetHttpContext(scope, Principal(claimType, expectedSubject));

        var snapshot = scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCoreAuthContextAccessor>()
            .GetCurrentContext();

        Assert.True(snapshot.Succeeded);
        Assert.Equal(expectedSubject, snapshot.Context.User?.Id);
        Assert.Equal(expectedSubject, snapshot.Context.Metadata[AppSurfaceAuthMetadataKeys.SubjectId]);
    }

    [Fact]
    public void GetCurrentContext_CustomSubjectClaimWinsBeforeDefaults()
    {
        using var scope = CreateScope(options => options.MapSubjectClaim("tenant-subject"));
        SetHttpContext(
            scope,
            Principal([
                new Claim(ClaimTypes.NameIdentifier, "default-subject"),
                new Claim("tenant-subject", "tenant-subject-1"),
            ]));

        var snapshot = scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCoreAuthContextAccessor>()
            .GetCurrentContext();

        Assert.True(snapshot.Succeeded);
        Assert.Equal("tenant-subject-1", snapshot.Context.User?.Id);
    }

    [Fact]
    public void GetCurrentContext_WithMixedIdentities_IgnoresUnauthenticatedClaims()
    {
        using var scope = CreateScope();
        var unauthenticated = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ignored")]);
        var authenticated = new ClaimsIdentity([new Claim("sub", "real-subject")], "Test");
        SetHttpContext(scope, new ClaimsPrincipal([unauthenticated, authenticated]));

        var snapshot = scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCoreAuthContextAccessor>()
            .GetCurrentContext();

        Assert.True(snapshot.Succeeded);
        Assert.Equal("real-subject", snapshot.Context.User?.Id);
    }

    [Fact]
    public void GetCurrentContext_WithDuplicateClaims_UsesFirstConfiguredResolver()
    {
        using var scope = CreateScope();
        SetHttpContext(
            scope,
            Principal([
                new Claim("sub", "sub-subject"),
                new Claim(ClaimTypes.NameIdentifier, "name-subject"),
            ]));

        var snapshot = scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCoreAuthContextAccessor>()
            .GetCurrentContext();

        Assert.True(snapshot.Succeeded);
        Assert.Equal("name-subject", snapshot.Context.User?.Id);
    }

    [Fact]
    public void GetCurrentContext_WithAuthenticatedPrincipalWithoutSubject_ReturnsMissingSubject()
    {
        using var scope = CreateScope();
        SetHttpContext(scope, Principal("role", "operator"));

        var snapshot = scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCoreAuthContextAccessor>()
            .GetCurrentContext();

        Assert.False(snapshot.Succeeded);
        Assert.Equal(AppSurfaceAuthReason.MissingSubject, snapshot.Failure?.Reason);
        Assert.Equal("missing_subject_claim", snapshot.Failure?.Metadata[AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode]);
    }

    [Fact]
    public void GetCurrentContext_MemoizesWithinScope()
    {
        using var scope = CreateScope();
        var httpContext = SetHttpContext(scope, Principal("sub", "first"));
        var accessor = scope.ServiceProvider.GetRequiredService<IAppSurfaceAspNetCoreAuthContextAccessor>();

        var first = accessor.GetCurrentContext();
        httpContext.User = Principal("sub", "second");
        var second = accessor.GetCurrentContext();

        Assert.Same(first, second);
        Assert.Equal("first", second.Context.User?.Id);
    }

    [Fact]
    public void GetCurrentContext_DoesNotShareMemoizedContextAcrossScopes()
    {
        using var firstScope = CreateScope();
        using var secondScope = CreateScope();
        SetHttpContext(firstScope, Principal("sub", "first"));
        var first = firstScope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCoreAuthContextAccessor>()
            .GetCurrentContext();

        SetHttpContext(secondScope, Principal("sub", "second"));
        var second = secondScope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCoreAuthContextAccessor>()
            .GetCurrentContext();

        Assert.Equal("first", first.Context.User?.Id);
        Assert.Equal("second", second.Context.User?.Id);
    }

    internal static IServiceScope CreateScope(Action<AppSurfaceAspNetCoreAuthOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceAspNetCoreAuth(configure);
        return services.BuildServiceProvider().CreateScope();
    }

    internal static DefaultHttpContext SetHttpContext(IServiceScope scope, ClaimsPrincipal principal)
    {
        var httpContext = new DefaultHttpContext
        {
            User = principal,
            RequestServices = scope.ServiceProvider,
        };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;
        return httpContext;
    }

    internal static ClaimsPrincipal Principal(string claimType, string value)
    {
        return Principal([new Claim(claimType, value)]);
    }

    internal static ClaimsPrincipal Principal(IEnumerable<Claim> claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }
}
