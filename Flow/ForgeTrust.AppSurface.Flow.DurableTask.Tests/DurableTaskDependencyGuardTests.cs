using System.Reflection;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Flow.DurableTask.Tests;

public sealed class DurableTaskDependencyGuardTests
{
    [Fact]
    public void DurableTaskProject_DoesNotReferenceSemanticKernel()
    {
        var project = LoadProject("Flow/ForgeTrust.AppSurface.Flow.DurableTask/ForgeTrust.AppSurface.Flow.DurableTask.csproj");
        var packageReferences = GetIncludes(project, "PackageReference");
        var projectReferences = GetIncludes(project, "ProjectReference");

        Assert.DoesNotContain(packageReferences, value => value.Contains("SemanticKernel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(projectReferences, value => value.Contains("SemanticKernel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DurableTaskAssembly_DoesNotReferenceSemanticKernel()
    {
        var references = typeof(AppSurfaceFlowDurableTaskModule).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference => reference.Name?.Contains("SemanticKernel", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void LoadProject_WithRootedPath_ThrowsArgumentException()
    {
        var rootedPath = Path.GetFullPath("Flow/ForgeTrust.AppSurface.Flow.DurableTask/ForgeTrust.AppSurface.Flow.DurableTask.csproj");

        var exception = Assert.Throws<ArgumentException>(() => LoadProject(rootedPath));

        Assert.Contains("rooted", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

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
