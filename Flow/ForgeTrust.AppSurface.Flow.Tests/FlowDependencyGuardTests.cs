using System.Reflection;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Flow.Tests;

public sealed class FlowDependencyGuardTests
{
    [Fact]
    public void FlowProject_DoesNotReferenceDurableTaskOrSemanticKernel()
    {
        var project = LoadProject("Flow/ForgeTrust.AppSurface.Flow/ForgeTrust.AppSurface.Flow.csproj");
        var packageReferences = GetIncludes(project, "PackageReference");
        var projectReferences = GetIncludes(project, "ProjectReference");

        Assert.DoesNotContain(packageReferences, value => value.Contains("DurableTask", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(packageReferences, value => value.Contains("SemanticKernel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(projectReferences, value => value.Contains("DurableTask", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(projectReferences, value => value.Contains("SemanticKernel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FlowAssembly_DoesNotReferenceDurableTaskOrSemanticKernel()
    {
        var references = typeof(AppSurfaceFlowModule).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, IsDurableTaskOrSemanticKernel);
    }

    [Fact]
    public void LoadProject_WithRootedPath_ThrowsArgumentException()
    {
        var rootedPath = Path.GetFullPath("Flow/ForgeTrust.AppSurface.Flow/ForgeTrust.AppSurface.Flow.csproj");

        var exception = Assert.Throws<ArgumentException>(() => LoadProject(rootedPath));

        Assert.Contains("rooted", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDurableTaskOrSemanticKernel(AssemblyName reference) =>
        reference.Name?.Contains("DurableTask", StringComparison.OrdinalIgnoreCase) == true ||
        reference.Name?.Contains("SemanticKernel", StringComparison.OrdinalIgnoreCase) == true;

    private static IEnumerable<string> GetIncludes(XDocument project, string elementName) =>
        project.Descendants()
            .Where(element => element.Name.LocalName == elementName)
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);

    private static XDocument LoadProject(string projectPath)
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        return XDocument.Load(TestPathUtils.PathUnder(repositoryRoot, projectPath));
    }
}
