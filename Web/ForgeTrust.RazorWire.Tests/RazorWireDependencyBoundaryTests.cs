using System.Xml.Linq;

namespace ForgeTrust.RazorWire.Tests;

public sealed class RazorWireDependencyBoundaryTests
{
    [Fact]
    public void RazorWireProjectReferencesNeutralAuthButNotAspNetCoreAuth()
    {
        var projectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../ForgeTrust.RazorWire/ForgeTrust.RazorWire.csproj"));
        var document = XDocument.Load(projectPath);
        var references = document
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value.Replace('\\', '/'))
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();

        Assert.Contains(
            "../../Auth/ForgeTrust.AppSurface.Auth/ForgeTrust.AppSurface.Auth.csproj",
            references);
        Assert.DoesNotContain(
            references,
            reference => reference.Contains("ForgeTrust.AppSurface.Auth.AspNetCore", StringComparison.Ordinal));
    }
}
