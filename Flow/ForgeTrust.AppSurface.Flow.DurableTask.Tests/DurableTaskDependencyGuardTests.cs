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

    private static IEnumerable<string> GetIncludes(XDocument project, string elementName) =>
        project.Descendants()
            .Where(element => element.Name.LocalName == elementName)
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);

    private static XDocument LoadProject(string projectPath)
    {
        var repositoryRoot = FindRepositoryRoot();
        return XDocument.Load(Path.Join(repositoryRoot, projectPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
