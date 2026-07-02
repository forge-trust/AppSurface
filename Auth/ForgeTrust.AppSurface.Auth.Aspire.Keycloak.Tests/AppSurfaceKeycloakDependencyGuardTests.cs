using System.Xml.Linq;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;
using ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakDependencyGuardTests
{
    [Fact]
    public void KeycloakPackageReferencesAspireHostingKeycloakAndNotRuntimeOidcPackage()
    {
        var document = LoadProject("Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.csproj");

        var packageReferences = document.Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .ToArray();
        var projectReferences = document.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value.Replace('\\', '/'))
            .Where(value => value is not null)
            .ToArray();

        Assert.Contains("Aspire.Hosting.Keycloak", packageReferences);
        Assert.Contains("../../Aspire/ForgeTrust.AppSurface.Aspire/ForgeTrust.AppSurface.Aspire.csproj", projectReferences);
        Assert.DoesNotContain(projectReferences, value => value?.Contains("Auth.AspNetCore.Oidc", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void RuntimeOidcPackageDoesNotReferenceKeycloakPackage()
    {
        var referencedAssemblies = typeof(AppSurfaceOidcAuthOptions).Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => name is not null)
            .ToArray();

        Assert.DoesNotContain(referencedAssemblies, value => value?.Contains("Keycloak", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(referencedAssemblies, value => string.Equals(value, typeof(AppSurfaceKeycloakOptions).Assembly.GetName().Name, StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeAuthAndWebPackagesDoNotReferenceKeycloakHostingOrAppHostPackage()
    {
        var runtimeProjects = new[]
        {
            "Auth/ForgeTrust.AppSurface.Auth/ForgeTrust.AppSurface.Auth.csproj",
            "Auth/ForgeTrust.AppSurface.Auth.AspNetCore/ForgeTrust.AppSurface.Auth.AspNetCore.csproj",
            "Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.csproj",
            "Auth/ForgeTrust.AppSurface.Auth.AspNetCore.Oidc/ForgeTrust.AppSurface.Auth.AspNetCore.Oidc.csproj",
            "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
            "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj",
            "Web/ForgeTrust.AppSurface.Web.Scalar/ForgeTrust.AppSurface.Web.Scalar.csproj",
            "Web/ForgeTrust.RazorWire/ForgeTrust.RazorWire.csproj",
            "Web/ForgeTrust.RazorWire.Auth.AspNetCore/ForgeTrust.RazorWire.Auth.AspNetCore.csproj",
        };

        foreach (var project in runtimeProjects)
        {
            var document = LoadProject(project);
            var packageReferences = document.Descendants("PackageReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => value is not null)
                .ToArray();
            var projectReferences = document.Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value.Replace('\\', '/'))
                .Where(value => value is not null)
                .ToArray();

            Assert.DoesNotContain(packageReferences, value => value?.Contains("Keycloak", StringComparison.OrdinalIgnoreCase) == true);
            Assert.DoesNotContain(projectReferences, value => value?.Contains("Auth.Aspire.Keycloak", StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    [Fact]
    public void DuplicatedCallbackDefaultsStayAlignedWithRuntimeOidcPackage()
    {
        Assert.Equal(AppSurfaceOidcAuthOptions.DefaultCallbackPath, AppSurfaceKeycloakDefaults.CallbackPath);
        Assert.Equal(AppSurfaceOidcAuthOptions.DefaultSignedOutCallbackPath, AppSurfaceKeycloakDefaults.SignedOutCallbackPath);
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
