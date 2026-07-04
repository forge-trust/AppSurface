using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests;

public sealed class AppSurfaceDevAuthEnvironmentPolicyTests
{
    [Fact]
    public void FormatAllowedEnvironmentNames_WithDuplicateAndUnorderedNames_ReturnsDistinctSortedNames()
    {
        var options = new AppSurfaceDevAuthOptions();
        options.AllowedEnvironmentNames.Clear();
        options.AllowedEnvironmentNames.Add(" Staging ");
        options.AllowedEnvironmentNames.Add("development");
        options.AllowedEnvironmentNames.Add("Proof");
        options.AllowedEnvironmentNames.Add("Development");

        var formatted = AppSurfaceDevAuthEnvironmentPolicy.FormatAllowedEnvironmentNames(options);

        Assert.Equal("development, Proof, Staging", formatted);
    }

    [Fact]
    public void FormatAllowedEnvironmentNames_WithOnlyNullOrBlankNames_ReturnsNone()
    {
        var options = new AppSurfaceDevAuthOptions();
        options.AllowedEnvironmentNames.Clear();
        options.AllowedEnvironmentNames.Add(null!);
        options.AllowedEnvironmentNames.Add(" ");

        var formatted = AppSurfaceDevAuthEnvironmentPolicy.FormatAllowedEnvironmentNames(options);

        Assert.Equal("(none)", formatted);
    }
}
