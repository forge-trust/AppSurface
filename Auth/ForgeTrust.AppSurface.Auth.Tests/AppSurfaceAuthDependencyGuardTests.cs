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

        Assert.DoesNotContain("Microsoft.AspNetCore.App", frameworkReferences);

        var packageReferences = project.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value));

        Assert.DoesNotContain(
            packageReferences,
            value => value!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));

        var projectReferences = project.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Replace('\\', '/'));

        Assert.DoesNotContain(projectReferences, value => value.Contains("/Web/", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SurfaceNeutralProjects))]
    public void SurfaceNeutralAssembly_DoesNotReferenceAspNetCoreAssemblies(string projectPath, Assembly assembly)
    {
        _ = projectPath;

        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
    }

    private static XDocument LoadProject(string projectPath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var fullPath = Path.Combine(repositoryRoot, projectPath.Replace('/', Path.DirectorySeparatorChar));

        return XDocument.Load(fullPath);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find the AppSurface repository root.");
    }
}
