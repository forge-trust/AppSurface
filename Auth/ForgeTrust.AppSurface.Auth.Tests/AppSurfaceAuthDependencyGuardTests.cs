using System.Reflection;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Auth.Tests;

public sealed class AppSurfaceAuthDependencyGuardTests
{
    public static TheoryData<string, Assembly> SurfaceNeutralProjects => new()
    {
        { "Auth/ForgeTrust.AppSurface.Auth/ForgeTrust.AppSurface.Auth.csproj", typeof(AppSurfaceAuthModule).Assembly },
        { "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", typeof(IAppSurfaceModule).Assembly },
    };

    [Theory]
    [MemberData(nameof(SurfaceNeutralProjects))]
    public void SurfaceNeutralProject_DoesNotReferenceAspNetCore(string projectPath, Assembly assembly)
    {
        _ = assembly;
        var project = LoadProject(projectPath);

        var frameworkReferences = project.Descendants()
            .Where(element => element.Name.LocalName == "FrameworkReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value));

        Assert.DoesNotContain(
            frameworkReferences,
            value => string.Equals(value, "Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase));

        var packageReferences = project.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value));

        Assert.DoesNotContain(
            packageReferences,
            value => value!.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));

        var projectReferences = project.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Replace('\\', '/'));

        Assert.DoesNotContain(projectReferences, value => value.Contains("/Web/", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(SurfaceNeutralProjects))]
    public void SurfaceNeutralAssembly_DoesNotReferenceAspNetCoreAssemblies(string projectPath, Assembly assembly)
    {
        _ = projectPath;

        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void LoadProject_WithRootedPath_ThrowsArgumentException()
    {
        var rootedPath = Path.GetFullPath("ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj");

        var exception = Assert.Throws<ArgumentException>(() => LoadProject(rootedPath));

        Assert.Contains("rooted", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument LoadProject(string projectPath)
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        return XDocument.Load(TestPathUtils.PathUnder(repositoryRoot, projectPath));
    }
}
