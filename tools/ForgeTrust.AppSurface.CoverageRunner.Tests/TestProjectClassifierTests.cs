namespace ForgeTrust.AppSurface.CoverageRunner.Tests;

public sealed class TestProjectClassifierTests
{
    [Theory]
    [InlineData("Aspire/ForgeTrust.AppSurface.Aspire.Tests/ForgeTrust.AppSurface.Aspire.Tests.csproj", "core")]
    [InlineData("tools/ForgeTrust.AppSurface.PackageIndex.Tests/ForgeTrust.AppSurface.PackageIndex.Tests.csproj", "tools")]
    [InlineData("Web/ForgeTrust.AppSurface.Docs.Tests/ForgeTrust.AppSurface.Docs.Tests.csproj", "docs")]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj", "integration")]
    [InlineData("Web/ForgeTrust.RazorWire.Cli.Tests/ForgeTrust.RazorWire.Cli.Tests.csproj", "razorwire")]
    [InlineData("Web/ForgeTrust.AppSurface.Web.Tests/ForgeTrust.AppSurface.Web.Tests.csproj", "web")]
    public void GetGroup_ShouldMatchCoverageScriptGroups(string projectPath, string expectedGroup)
    {
        Assert.Equal(expectedGroup, TestProjectClassifier.GetGroup(projectPath));
    }

    [Fact]
    public void IsExclusive_ShouldTreatIntegrationProjectsAsExclusive()
    {
        Assert.True(TestProjectClassifier.IsExclusive(
            "Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj",
            "<Project />"));
    }

    [Fact]
    public void IsExclusive_ShouldTreatPlaywrightProjectsAsExclusive()
    {
        Assert.True(TestProjectClassifier.IsExclusive(
            "Web/Some.Tests/Some.Tests.csproj",
            "<PackageReference Include=\"Microsoft.Playwright\" />"));
    }

    [Fact]
    public void CreateSlug_ShouldUseProjectFileName()
    {
        Assert.Equal(
            "ForgeTrust.AppSurface.Web.Tests",
            TestProjectClassifier.CreateSlug("Web/ForgeTrust.AppSurface.Web.Tests/ForgeTrust.AppSurface.Web.Tests.csproj"));
    }
}
