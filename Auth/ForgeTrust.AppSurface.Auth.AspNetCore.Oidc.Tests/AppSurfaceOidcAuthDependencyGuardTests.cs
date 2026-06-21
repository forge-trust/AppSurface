using System.Xml.Linq;
using ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Oidc.Tests;

public sealed class AppSurfaceOidcAuthDependencyGuardTests
{
    [Fact]
    public void PackageReferencesExpectedAuthAndOidcDependenciesOnly()
    {
        var document = LoadProject("Auth/ForgeTrust.AppSurface.Auth.AspNetCore.Oidc/ForgeTrust.AppSurface.Auth.AspNetCore.Oidc.csproj");

        var packageReferences = document.Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .ToArray();
        var projectReferences = document.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value.Replace('\\', '/'))
            .Where(value => value is not null)
            .ToArray();

        Assert.Contains("Microsoft.AspNetCore.Authentication.OpenIdConnect", packageReferences);
        Assert.Contains("../ForgeTrust.AppSurface.Auth.AspNetCore/ForgeTrust.AppSurface.Auth.AspNetCore.csproj", projectReferences);
        Assert.Contains("../ForgeTrust.AppSurface.Auth/ForgeTrust.AppSurface.Auth.csproj", projectReferences);
        Assert.DoesNotContain(packageReferences, IsForbiddenDependency);
        Assert.DoesNotContain(projectReferences, IsForbiddenDependency);
    }

    [Fact]
    public void PublicAssemblyReferencesAspNetCoreOidcButNotProviderOrPersistencePackages()
    {
        var referencedAssemblies = typeof(AppSurfaceOidcAuthOptions).Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => name is not null)
            .ToArray();

        Assert.Contains("Microsoft.AspNetCore.Authentication.OpenIdConnect", referencedAssemblies);
        Assert.DoesNotContain(referencedAssemblies, IsForbiddenDependency);
    }

    private static bool IsForbiddenDependency(string? value)
    {
        if (value is null)
        {
            return false;
        }

        return value.Contains("Aspire", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Keycloak", StringComparison.OrdinalIgnoreCase)
            || value.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Identity.Web", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Auth0", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Okta", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument LoadProject(string projectPath)
    {
        var repositoryRoot = FindRepositoryRoot();
        return XDocument.Load(TestPathUtils.PathUnder(repositoryRoot, projectPath));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.GetFiles("ForgeTrust.AppSurface.slnx").Length == 0)
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find repository root.");
    }
}
