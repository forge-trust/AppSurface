using FakeItEasy;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public sealed class RepositoryRazorDocsConsumerLandingTests
{
    private const string ConsumerLandingPath = "Web/ForgeTrust.Runnable.Web.RazorDocs/use-razordocs.md";

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
        Assert.Equal("Use RazorDocs in your repository", consumerLanding.Metadata?.Title);
        Assert.Equal("guide", consumerLanding.Metadata?.PageType);
        Assert.Equal("Start Here", consumerLanding.Metadata?.NavGroup);
        Assert.Contains("RazorDocs is the documentation surface", consumerLanding.Content);

        var featuredGroup = Assert.Single(
            rootLanding.Metadata?.FeaturedPageGroups ?? [],
            group => string.Equals(group.Intent, "build-docs", StringComparison.OrdinalIgnoreCase));
        var featuredPage = Assert.Single(featuredGroup.Pages ?? []);
        Assert.Equal("How do I use RazorDocs in my own repository?", featuredPage.Question);
        Assert.Equal(ConsumerLandingPath, featuredPage.Path);
        Assert.Contains("host shape", featuredPage.SupportingCopy);
    }
}
