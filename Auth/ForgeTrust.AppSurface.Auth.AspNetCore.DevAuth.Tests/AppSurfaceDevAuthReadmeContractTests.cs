namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests;

public sealed class AppSurfaceDevAuthReadmeContractTests
{
    [Theory]
    [InlineData("data-appsurface-dev-auth")]
    [InlineData("appsurface-dev-auth-marker")]
    public void Readme_DoesNotPublishStaticExportRejectedDevAuthMarkers(string forbiddenMarker)
    {
        var readme = File.ReadAllText(Path.Join(
            FindRepositoryRoot(),
            "Auth",
            "ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth",
            "README.md"));

        Assert.DoesNotContain(forbiddenMarker, readme, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root from the test output directory.");
    }
}
