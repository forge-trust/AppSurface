using ForgeTrust.AppSurface.Config;

namespace ForgeTrust.AppSurface.Config.Tests;

public class ConfigEnvironmentCandidatesTests
{
    [Theory]
    [InlineData("Production", "Service.Options", "PRODUCTION_SERVICE_OPTIONS", "SERVICE_OPTIONS", "PRODUCTION__SERVICE__OPTIONS", "SERVICE__OPTIONS")]
    [InlineData("prod-us", "api-key", "PROD_US_API_KEY", "API_KEY", "PROD_US__API__KEY", "API__KEY")]
    [InlineData("Production", "Service", "PRODUCTION_SERVICE", "SERVICE", "PRODUCTION__SERVICE")]
    public void GetDirectCandidates_PreservesLegacyOrderAndDeduplicates(
        string environment,
        string key,
        params string[] expected)
    {
        Assert.Equal(expected, ConfigEnvironmentCandidates.GetDirectCandidates(environment, key));
    }

    [Fact]
    public void GetPathCandidates_ProjectsCanonicalSegmentsThroughDottedLegacySpelling()
    {
        var candidates = ConfigEnvironmentCandidates.GetPathCandidates(
            "Production",
            ["Service", "Api-Key"]);

        Assert.Equal(
            [
                "PRODUCTION_SERVICE_API_KEY",
                "SERVICE_API_KEY",
                "PRODUCTION__SERVICE__API__KEY",
                "SERVICE__API__KEY"
            ],
            candidates);
    }

    [Theory]
    [InlineData("A.B", "A_B", "A__B")]
    [InlineData("A-B", "A_B", "A__B")]
    [InlineData("A:B", "A_B", "A__B")]
    public void CompositionPathLiteralSeparatorsRetainLegacyProjectionAndFirstProducer(string literal, string flat, string hierarchical)
    {
        var pathSegments = literal == "A:B" ? new[] { "A", "B" } : new[] { literal };
        var candidates = ConfigEnvironmentCandidates.GetPathCandidates("Production", pathSegments);
        Assert.Equal(new[] { $"PRODUCTION_{flat}", flat, $"PRODUCTION__{hierarchical}", hierarchical }, candidates);
    }

    [Theory]
    [InlineData("production.api-key", "PRODUCTION_API_KEY")]
    [InlineData(" Prod-Us ", " PROD_US ")]
    public void NormalizeSegment_UppercasesAndFlattensLegacySeparators(string value, string expected)
    {
        Assert.Equal(expected, ConfigEnvironmentCandidates.NormalizeSegment(value));
    }

    [Theory]
    [InlineData("service.api-key", "SERVICE__API__KEY")]
    [InlineData("Service..Options", "SERVICE__OPTIONS")]
    [InlineData("SERVICE-API", "SERVICE__API")]
    public void NormalizeHierarchicalKey_PreservesPathBoundaries(string value, string expected)
    {
        Assert.Equal(expected, ConfigEnvironmentCandidates.NormalizeHierarchicalKey(value));
    }
    [Fact]
    // protects=compatibility adapter names and first producer flags; fails_when=adapter diverges or duplicate changes provenance; no_claim=source presence.
    public void BuildDirectAdapterAndMetadataRetainIndependentFirstProducerTable()
    {
        Assert.Equal(new[] { "PROD_SERVICE", "SERVICE", "PROD__SERVICE" },
            ConfigEnvironmentCandidates.BuildDirectCandidates("PROD", "SERVICE", "SERVICE"));
        Assert.Equal(new[]
        {
            new EnvironmentCompositionPathCandidate("PROD_SERVICE", EnvironmentCandidateScope.Scoped, false),
            new EnvironmentCompositionPathCandidate("SERVICE", EnvironmentCandidateScope.Unscoped, false),
            new EnvironmentCompositionPathCandidate("PROD__SERVICE", EnvironmentCandidateScope.Scoped, true),
        }, ConfigEnvironmentCandidates.BuildMetadataCandidates("PROD", "SERVICE", "SERVICE"));
    }

}
