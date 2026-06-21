using ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Oidc.Tests;

public sealed class AppSurfaceOidcAuthPromptTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("/", "/")]
    [InlineData("/account/after-login", "/account/after-login")]
    public void CreateLoginPrompt_UsesLocalOnlyReturnUrlPolicy(string? targetPath, string? expected)
    {
        var options = new AppSurfaceOidcAuthOptions();

        var prompt = options.CreateLoginPrompt(targetPath, "Sign in");

        Assert.Equal(expected, prompt.TargetPath);
        Assert.Equal("Sign in", prompt.DisplayText);
        Assert.Equal(AppSurfaceOidcAuthOptions.DefaultCookieScheme, prompt.Metadata[AppSurfaceOidcAuthMetadataKeys.CookieScheme]);
        Assert.Equal(AppSurfaceOidcAuthOptions.DefaultOidcScheme, prompt.Metadata[AppSurfaceOidcAuthMetadataKeys.OidcScheme]);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("//example.com")]
    [InlineData("/\\example")]
    [InlineData("/some\\path")]
    [InlineData("/line\rbreak")]
    [InlineData("/line\nbreak")]
    public void CreateLoginPrompt_WhenTargetIsUnsafe_Throws(string targetPath)
    {
        var options = new AppSurfaceOidcAuthOptions();

        Assert.Throws<ArgumentException>(() => options.CreateLoginPrompt(targetPath));
    }

    [Fact]
    public void CreateLogoutPrompt_IsPassiveAndUsesSameReturnUrlPolicy()
    {
        var options = new AppSurfaceOidcAuthOptions();

        var prompt = options.CreateLogoutPrompt("/signed-out", "Sign out");

        Assert.Equal("/signed-out", prompt.TargetPath);
        Assert.Equal("Sign out", prompt.DisplayText);
        Assert.Equal(AppSurfaceOidcAuthOptions.DefaultCookieScheme, prompt.Metadata[AppSurfaceOidcAuthMetadataKeys.CookieScheme]);
        Assert.Equal(AppSurfaceOidcAuthOptions.DefaultOidcScheme, prompt.Metadata[AppSurfaceOidcAuthMetadataKeys.OidcScheme]);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("//example.com")]
    [InlineData("/\\example")]
    [InlineData("/some\\path")]
    [InlineData("/line\rbreak")]
    [InlineData("/line\nbreak")]
    public void CreateLogoutPrompt_WhenTargetIsUnsafe_Throws(string targetPath)
    {
        var options = new AppSurfaceOidcAuthOptions();

        Assert.Throws<ArgumentException>(() => options.CreateLogoutPrompt(targetPath));
    }
}
