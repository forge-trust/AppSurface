using System.Xml.Linq;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Testing.Tests;

public sealed class TestPathUtilsProjectTests
{
    [Fact]
    public void HelperProject_ShouldRemainNonPackableAndOutsideTestRunnerSurface()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var project = XDocument.Load(TestPathUtils.PathUnder(
            repoRoot,
            "tests",
            "ForgeTrust.AppSurface.Testing",
            "ForgeTrust.AppSurface.Testing.csproj"));

        Assert.Equal("false", RequiredProperty(project, "IsPackable"));
        Assert.Equal("false", RequiredProperty(project, "IsTestProject"));
        Assert.DoesNotContain(project.Descendants(), element => element.Name.LocalName == "PackageReference");
    }

    private static string RequiredProperty(XDocument project, string propertyName)
    {
        return project.Descendants()
            .Single(element => element.Name.LocalName == propertyName)
            .Value;
    }
}
