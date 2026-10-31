using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocConceptContractBoundaryTests
{
    private const string MapSchema = "appsurface-docs-concept-map-v1";
    private static readonly string ReleasePin = new('a', 64);

    [Fact]
    public void CanonicalPagesAndReleaseInputsRejectNullRequiredArgumentsAndDetachSnapshots()
    {
        var routeException = Assert.Throws<ArgumentNullException>(() =>
            new DocConceptCanonicalPage(null!, "guides/setup.html", "en", true, "Setup", [], false, null));
        Assert.Equal("routePath", routeException.ParamName);

        var titleException = Assert.Throws<ArgumentNullException>(() =>
            new DocConceptCanonicalPage("guides/setup", "guides/setup.html", "en", true, null!, [], false, null));
        Assert.Equal("title", titleException.ParamName);

        var anchorsException = Assert.Throws<ArgumentNullException>(() =>
            new DocConceptCanonicalPage("guides/setup", "guides/setup.html", "en", true, "Setup", null!, false, null));
        Assert.Equal("anchors", anchorsException.ParamName);

        var versionException = Assert.Throws<ArgumentNullException>(() =>
            new DocConceptReleaseInput(null!, ReleasePin, true, true, true, true, null, [], null));
        Assert.Equal("version", versionException.ParamName);

        var pinException = Assert.Throws<ArgumentNullException>(() =>
            new DocConceptReleaseInput("1.0.0", null!, true, true, true, true, null, [], null));
        Assert.Equal("releaseManifestSha256", pinException.ParamName);

        var routesException = Assert.Throws<ArgumentNullException>(() =>
            new DocConceptReleaseInput("1.0.0", ReleasePin, true, true, true, true, null, null!, null));
        Assert.Equal("canonicalRoutes", routesException.ParamName);

        var anchors = new List<string> { "configure" };
        var page = new DocConceptCanonicalPage("guides/setup", "guides/setup.html", "en", true, "Setup", anchors, false, "guides.setup");
        anchors[0] = "caller-mutated";
        Assert.Equal("configure", Assert.Single(page.Anchors));

        var routes = new List<DocConceptCanonicalRouteFact>
        {
            new("guides/setup", "guides/setup.html", "en", true)
        };
        var sidecarBytes = Encoding.UTF8.GetBytes("sidecar");
        var release = new DocConceptReleaseInput(
            "1.0.0", ReleasePin, true, true, true, true, "guides/setup", routes,
            new ReadOnlyMemory<byte>(sidecarBytes));
        routes.Clear();
        sidecarBytes[0] = 0;

        Assert.Equal("guides/setup", Assert.Single(release.CanonicalRoutes).RoutePath);
        Assert.Equal(Encoding.UTF8.GetBytes("sidecar"), release.ConceptIndexBytes!.Value.ToArray());
    }

    [Fact]
    public void MapReferencesRejectNullArgumentsAndCopyTheirExactBytes()
    {
        var pathException = Assert.Throws<ArgumentNullException>(() =>
            new DocConceptMapReference(null!, ReleasePin, ReadOnlyMemory<byte>.Empty));
        Assert.Equal("path", pathException.ParamName);

        var pinException = Assert.Throws<ArgumentNullException>(() =>
            new DocConceptMapReference("concept-map.json", null!, ReadOnlyMemory<byte>.Empty));
        Assert.Equal("sha256", pinException.ParamName);

        var bytes = Encoding.UTF8.GetBytes("bridge");
        var reference = new DocConceptMapReference("concept-map.json", ReleasePin, new ReadOnlyMemory<byte>(bytes));
        bytes[0] = 0;

        Assert.Equal(Encoding.UTF8.GetBytes("bridge"), reference.Bytes.ToArray());
    }

    [Fact]
    public void ByteArtifactBudgetCountsEmptyExactAndOverLimitInputsWithoutTruncation()
    {
        Assert.True(DocConceptArtifactBudget.TryMeasure(ReadOnlySpan<byte>.Empty, 1, out var byteCount));
        Assert.Equal(0, byteCount);

        var multibyteText = Encoding.UTF8.GetBytes("é");
        Assert.Equal(2, multibyteText.Length);
        Assert.True(DocConceptArtifactBudget.TryMeasure(multibyteText, 2, out byteCount));
        Assert.Equal(2, byteCount);

        Assert.False(DocConceptArtifactBudget.TryMeasure(multibyteText, 1, out byteCount));
        Assert.Equal(2, byteCount);

        Assert.False(DocConceptArtifactBudget.TryMeasure(ReadOnlySpan<byte>.Empty, 0, out byteCount));
        Assert.Equal(0, byteCount);
        Assert.False(DocConceptArtifactBudget.TryMeasure(multibyteText, DocConceptArtifactBudget.MaximumBytes + 1, out byteCount));
        Assert.Equal(0, byteCount);
    }

    [Fact]
    public void OversizedBridgeFailsAsAWholeAndReturnsNoPartialMemberships()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = MapSchema,
            concepts = new[]
            {
                new
                {
                    conceptId = "guides.setup",
                    memberships = new[]
                    {
                        new
                        {
                            version = "1.0.0",
                            releaseManifestSha256 = ReleasePin,
                            locale = "en",
                            kind = "page",
                            canonicalRoutePath = "guides/setup"
                        }
                    }
                }
            }
        });

        Assert.True(DocConceptSchema.TryLoadMap(bytes, bytes.Length, out var exactMemberships, out var exactIssue));
        Assert.Null(exactIssue);
        Assert.Single(exactMemberships);

        Assert.False(DocConceptSchema.TryLoadMap(bytes, bytes.Length - 1, out var rejectedMemberships, out var issue));
        Assert.Empty(rejectedMemberships);
        Assert.Equal(DocConceptValidationFailure.ArtifactTooLarge, issue!.Failure);
    }

    [Fact]
    public void SourceKeysBindSourceIdentityWithoutDelimiterAmbiguity()
    {
        var identity = new DocConceptPageIdentity("1:2", ReleasePin, "guides/setup", "en", true);
        var equivalentPinCase = identity with { ReleaseManifestSha256 = ReleasePin.ToUpperInvariant() };
        var key = DocConceptIdentityIndex.CreateSourceKey("site", identity);

        Assert.Equal(key, DocConceptIdentityIndex.CreateSourceKey("site", equivalentPinCase));
        Assert.Equal(64, key.Length);
        Assert.All(key, character => Assert.True(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));

        // These tuples collide under naive colon-joined encoding but differ under the length-prefixed contract.
        var delimiterCollision = identity with { Version = "2" };
        var collisionKey = DocConceptIdentityIndex.CreateSourceKey("site:1", delimiterCollision);
        Assert.NotEqual(key, collisionKey);

        var instanceException = Assert.Throws<ArgumentNullException>(() =>
            DocConceptIdentityIndex.CreateSourceKey(null!, identity));
        Assert.Equal("instanceId", instanceException.ParamName);
        var identityException = Assert.Throws<ArgumentNullException>(() =>
            DocConceptIdentityIndex.CreateSourceKey("site", null!));
        Assert.Equal("sourceIdentity", identityException.ParamName);
        var routeException = Assert.Throws<ArgumentException>(() =>
            DocConceptIdentityIndex.CreateSourceKey("site", identity with { RoutePath = "../setup" }));
        Assert.Equal("sourceIdentity", routeException.ParamName);
    }
}
