using FakeItEasy;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class RepositoryAppSurfaceDocsConsumerLandingTests
{
    private const string ConsumerLandingPath = "Web/ForgeTrust.AppSurface.Docs/use-appsurface-docs.md";

    [Fact]
    public async Task ConsumerLanding_ShouldBeHarvestedAndFeaturedFromRootLanding()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var harvester = new MarkdownHarvester(A.Fake<ILogger<MarkdownHarvester>>());

        var docs = (await harvester.HarvestAsync(repoRoot)).ToList();

        var rootLanding = Assert.Single(docs, doc => string.Equals(doc.Path, "README.md", StringComparison.OrdinalIgnoreCase));
        var consumerLanding = Assert.Single(
            docs,
            doc => string.Equals(doc.Path, ConsumerLandingPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Use AppSurface Docs in your repository", consumerLanding.Metadata?.Title);
        Assert.Equal("guide", consumerLanding.Metadata?.PageType);
        Assert.Equal("Start Here", consumerLanding.Metadata?.NavGroup);
        Assert.Contains("AppSurface Docs is the documentation surface", consumerLanding.Content);

        var featuredGroup = Assert.Single(
            rootLanding.Metadata?.FeaturedPageGroups ?? [],
            group => string.Equals(group.Intent, "build-docs", StringComparison.OrdinalIgnoreCase));
        var featuredPage = Assert.Single(
            featuredGroup.Pages ?? [],
            page => string.Equals(page.Path, ConsumerLandingPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("How do I use AppSurface Docs in my own repository?", featuredPage.Question);
        Assert.Equal(ConsumerLandingPath, featuredPage.Path);
        Assert.Contains("host shape", featuredPage.SupportingCopy);
    }
}
