using System.Security.Claims;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Tests;

public sealed class AppSurfaceAspNetCoreAuthContextMapperTests
{
    [Fact]
    public void Map_WithNullPrincipal_ReturnsAnonymousSuccess()
    {
        var mapper = CreateMapper();

        var snapshot = mapper.Map(null);

        Assert.True(snapshot.Succeeded);
        Assert.False(snapshot.Context.IsAuthenticated);
    }

    [Fact]
    public void Map_WithWhitespaceAuthenticationType_DoesNotEmitAuthenticationSchemeMetadata()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("sub", "user-1")], authenticationType: "   "));
        var mapper = CreateMapper();

        var snapshot = mapper.Map(principal);

        Assert.True(snapshot.Succeeded);
        Assert.Equal("user-1", snapshot.Context.User?.Id);
        Assert.False(snapshot.Context.Metadata.ContainsKey(AppSurfaceAuthMetadataKeys.AuthenticationScheme));
    }

    private static AppSurfaceAspNetCoreAuthContextMapper CreateMapper()
    {
        return new AppSurfaceAspNetCoreAuthContextMapper(Options.Create(new AppSurfaceAspNetCoreAuthOptions()));
    }
}
