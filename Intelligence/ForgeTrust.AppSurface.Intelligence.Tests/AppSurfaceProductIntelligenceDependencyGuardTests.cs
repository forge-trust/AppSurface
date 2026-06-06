using System.Reflection;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Intelligence;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Intelligence.Tests;

public sealed class AppSurfaceProductIntelligenceDependencyGuardTests
{
    [Fact]
    public void IntelligenceProject_DoesNotReferenceAspNetCoreOrPostHog()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var project = XDocument.Load(TestPathUtils.PathUnder(
            repositoryRoot,
            "Intelligence/ForgeTrust.AppSurface.Intelligence/ForgeTrust.AppSurface.Intelligence.csproj"));

        var packageReferences = project.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        Assert.DoesNotContain(packageReferences, value => value!.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(packageReferences, value => value!.Contains("PostHog", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IntelligenceAssembly_DoesNotReferenceAspNetCoreOrPostHogAssemblies()
    {
        var referencedAssemblies = typeof(AppSurfaceProductIntelligenceModule)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(referencedAssemblies, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("PostHog", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegistryExamplesAndDocs_DoNotContainForbiddenSecrets()
    {
        var readme = File.ReadAllText(Path.Combine(
            TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
            "Intelligence",
            "ForgeTrust.AppSurface.Intelligence",
            "README.md"));
        var renderedContracts = string.Join(
            Environment.NewLine,
            AppSurfaceProductEventRegistry.All.SelectMany(contract => contract.ForbiddenExamples));

        Assert.DoesNotContain("sk_", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requestToken", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("stack trace value", renderedContracts, StringComparison.OrdinalIgnoreCase);
    }
}
