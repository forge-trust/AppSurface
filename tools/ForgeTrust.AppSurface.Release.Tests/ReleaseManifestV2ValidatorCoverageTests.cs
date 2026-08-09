using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Release;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseManifestV2ValidatorCoverageTests
{
    [Fact]
    public void PackageIndexEntryUsesEmptyPathWhenReleaseLinkIsNull()
    {
        var entry = new PackageIndexEntry("Alpha/Package.csproj", null, null);

        Assert.Equal(string.Empty, entry.ReleaseNotesPath);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"manifest\"")]
    [InlineData("42")]
    public void TryDeserializeRejectsNonObjectJson(string json)
    {
        Assert.False(ReleaseManifestV2Validator.TryDeserialize(json, out var manifest, out var issue));
        Assert.Null(manifest);
        Assert.Equal("Release manifest JSON must be an object.", issue);
    }

    [Fact]
    public void TryDeserializeRejectsMissingRequiredProperty()
    {
        var json = ManifestJson();
        json.Remove("warningIds");

        AssertInvalid(json, "Release manifest has missing, unknown, or V1-only properties.");
    }

    [Fact]
    public void TryDeserializeRejectsMissingConsumedUnreleasedEntryPaths()
    {
        var json = ManifestJson();
        json.Remove("consumedUnreleasedEntryPaths");

        AssertInvalid(json, "Release manifest has missing, unknown, or V1-only properties.");
    }

    [Fact]
    public void TryDeserializeRejectsUnknownProperty()
    {
        var json = ManifestJson();
        json["sourceCommit"] = "abc123";

        AssertInvalid(json, "Release manifest has missing, unknown, or V1-only properties.");
    }

    [Fact]
    public void TryDeserializeRejectsInvalidSchemaProperty()
    {
        var json = ManifestJson();
        json["schema"] = "appsurface-release-manifest-v1";

        AssertInvalid(json, "Release manifest schema must be 'appsurface-release-manifest-v2'.");
    }

    [Theory]
    [InlineData("preparationBaseCommit")]
    [InlineData("date")]
    [InlineData("releaseClassification")]
    public void TryDeserializeRejectsMissingRequiredValue(string property)
    {
        var json = ManifestJson();
        json[property] = null;

        AssertInvalid(json, "Release manifest has missing required V2 values.");
    }

    [Theory]
    [InlineData("version", "not-a-version")]
    [InlineData("tag", "v0.1.0-preview.2")]
    [InlineData("date", "2026-05-25T00:00:00Z")]
    [InlineData("releaseClassification", "canary")]
    public void TryDeserializeRejectsInvalidIdentityValue(string property, string value)
    {
        var json = ManifestJson();
        json[property] = value;

        AssertInvalid(json, "Release manifest has invalid V2 identity values.");
    }

    [Fact]
    public void TryDeserializeRejectsUnorderedPublishedProjects()
    {
        var json = ManifestJson(
            publishedProjects: ["Zeta/Package.csproj", "Alpha/Package.csproj"]);

        AssertInvalid(json, "Release manifest V2 package resolutions are invalid or not ordinally sorted.");
    }

    [Fact]
    public void TryDeserializeRejectsDuplicatePublishedProjects()
    {
        var json = ManifestJson(
            publishedProjects: ["Alpha/Package.csproj", "Alpha/Package.csproj"]);

        AssertInvalid(json, "Release manifest V2 package resolutions are invalid or not ordinally sorted.");
    }

    [Fact]
    public void TryDeserializeRejectsUnorderedResolutionProjects()
    {
        var json = ManifestJson(
            publishedProjects: ["Alpha/Package.csproj", "Zeta/Package.csproj"],
            resolutions:
            [
                Resolution("Zeta/Package.csproj"),
                Resolution("Alpha/Package.csproj")
            ]);

        AssertInvalid(json, "Release manifest V2 package resolutions are invalid or not ordinally sorted.");
    }

    [Fact]
    public void TryDeserializeRejectsDuplicateResolutionProjects()
    {
        var json = ManifestJson(
            publishedProjects: ["Alpha/Package.csproj"],
            resolutions:
            [
                Resolution("Alpha/Package.csproj"),
                Resolution("Alpha/Package.csproj")
            ]);

        AssertInvalid(json, "Release manifest V2 package resolutions are invalid or not ordinally sorted.");
    }

    [Theory]
    [InlineData("releases/unreleased.entries/2026-08-08-zulu.md", "releases/unreleased.entries/2026-08-08-alpha.md")]
    [InlineData("releases/unreleased.entries/2026-08-08-alpha.md", "releases/unreleased.entries/2026-08-08-alpha.md")]
    [InlineData("releases/unreleased.entries/not-an-entry.md")]
    [InlineData("releases\\unreleased.entries\\2026-08-08-backslash.md")]
    public void TryDeserializeRejectsInvalidOrUnorderedConsumedUnreleasedEntries(params string[] paths)
    {
        var json = ManifestJson();
        json["consumedUnreleasedEntryPaths"] = new JsonArray(paths.Select(path => JsonValue.Create(path)).ToArray());

        AssertInvalid(json, "Release manifest V2 package resolutions are invalid or not ordinally sorted.");
    }

    [Theory]
    [InlineData("source", "explicit")]
    [InlineData("aliasPath", "releases/other.md")]
    [InlineData("resolvedPath", "releases/v0.1.0-preview.2.md")]
    [InlineData("releaseTag", "v0.1.0-preview.2")]
    [InlineData("preparationBaseCommit", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public void TryDeserializeRejectsInvalidResolutionData(string property, string value)
    {
        var resolution = Resolution("Alpha/Package.csproj");
        resolution[property] = value;
        var json = ManifestJson(
            publishedProjects: ["Alpha/Package.csproj"],
            resolutions: [resolution]);

        AssertInvalid(json, "Release manifest V2 package resolutions are invalid or not ordinally sorted.");
    }

    [Fact]
    public void TryValidatePackageSetRejectsManifestPublishedProjectMismatch()
    {
        var packages = LoadPackages(
            """
            packages:
              - project: Alpha/Package.csproj
                classification: public
                publish_decision: publish
                release_track: coordinated
              - project: Zeta/Package.csproj
                classification: public
                publish_decision: publish
                release_track: explicit
                release_notes_path: releases/unreleased.md
            """);
        var manifest = Manifest(
            ["Alpha/Package.csproj"],
            [ResolutionRecord("Alpha/Package.csproj")]);

        Assert.False(ReleaseManifestV2Validator.TryValidatePackageSet(manifest, packages, out var issue));
        Assert.Contains("must exactly match package-index public publish rows", issue, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidatePackageSetRejectsManifestWithDuplicatePublishedProject()
    {
        var packages = LoadPackages(
            """
            packages:
              - project: Alpha/Package.csproj
                classification: public
                publish_decision: publish
                release_track: coordinated
            """);
        var manifest = Manifest(
            ["Alpha/Package.csproj", "Alpha/Package.csproj"],
            [ResolutionRecord("Alpha/Package.csproj")]);

        Assert.False(ReleaseManifestV2Validator.TryValidatePackageSet(manifest, packages, out var issue));
        Assert.Contains("Alpha/Package.csproj, Alpha/Package.csproj", issue, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidatePackageSetRejectsPackageIndexWithDuplicatePublishedProject()
    {
        var packages = LoadPackages(
            """
            packages:
              - project: Alpha/Package.csproj
                classification: public
                publish_decision: publish
                release_track: coordinated
              - project: Alpha/Package.csproj
                classification: public
                publish_decision: publish
                release_track: coordinated
            """);
        var manifest = Manifest(
            ["Alpha/Package.csproj"],
            [ResolutionRecord("Alpha/Package.csproj")]);

        Assert.False(ReleaseManifestV2Validator.TryValidatePackageSet(manifest, packages, out var issue));
        Assert.Contains("package-index public publish rows", issue, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidatePackageSetRejectsCoordinatedResolutionMismatch()
    {
        var packages = LoadPackages(
            """
            packages:
              - project: Alpha/Package.csproj
                classification: public
                publish_decision: publish
                release_track: coordinated
              - project: Zeta/Package.csproj
                classification: public
                publish_decision: publish
                release_track: explicit
                release_notes_path: releases/unreleased.md
            """);
        var manifest = Manifest(
            ["Alpha/Package.csproj", "Zeta/Package.csproj"],
            []);

        Assert.False(ReleaseManifestV2Validator.TryValidatePackageSet(manifest, packages, out var issue));
        Assert.Contains("coordinated resolutions []", issue, StringComparison.Ordinal);
        Assert.Contains("Alpha/Package.csproj", issue, StringComparison.Ordinal);
    }

    private static void AssertInvalid(JsonObject json, string expectedIssue)
    {
        Assert.False(ReleaseManifestV2Validator.TryDeserialize(
            json.ToJsonString(),
            out var manifest,
            out var issue));
        Assert.Null(manifest);
        Assert.Equal(expectedIssue, issue);
    }

    private static JsonObject ManifestJson(
        string[]? publishedProjects = null,
        JsonObject[]? resolutions = null)
    {
        return new JsonObject
        {
            ["schema"] = ReleaseManifestV2Validator.Schema,
            ["version"] = "0.1.0-preview.1",
            ["tag"] = "v0.1.0-preview.1",
            ["date"] = "2026-05-25",
            ["preparationBaseCommit"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ["releaseClassification"] = "prerelease",
            ["generatedFiles"] = new JsonArray(),
            ["publishedPackageProjects"] = new JsonArray((publishedProjects ?? []).Select(project => JsonValue.Create(project)).ToArray()),
            ["coordinatedPackageReleaseNoteResolutions"] = new JsonArray((resolutions ?? []).Cast<JsonNode?>().ToArray()),
            ["diagnostics"] = new JsonArray(),
            ["warningIds"] = new JsonArray(),
            ["consumedUnreleasedEntryPaths"] = new JsonArray()
        };
    }

    private static JsonObject Resolution(string project)
    {
        return new JsonObject
        {
            ["project"] = project,
            ["source"] = "coordinated",
            ["aliasPath"] = PackageReleaseLink.CoordinatedReleaseNotesPath,
            ["resolvedPath"] = "releases/v0.1.0-preview.1.md",
            ["releaseTag"] = "v0.1.0-preview.1",
            ["preparationBaseCommit"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
    }

    private static ReleaseManifestV2 Manifest(
        IReadOnlyList<string> publishedProjects,
        IReadOnlyList<CoordinatedPackageReleaseNoteResolution> resolutions)
    {
        return new ReleaseManifestV2(
            ReleaseManifestV2Validator.Schema,
            "0.1.0-preview.1",
            "v0.1.0-preview.1",
            "2026-05-25",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "prerelease",
            [],
            publishedProjects,
            resolutions,
            [],
            []);
    }

    private static CoordinatedPackageReleaseNoteResolution ResolutionRecord(string project)
    {
        return new CoordinatedPackageReleaseNoteResolution(
            project,
            "coordinated",
            PackageReleaseLink.CoordinatedReleaseNotesPath,
            "releases/v0.1.0-preview.1.md",
            "v0.1.0-preview.1",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    }

    private static IReadOnlyList<PackageIndexEntry> LoadPackages(string yaml)
    {
        return PackageIndexSummary.Load(yaml).PublicPublishedPackages;
    }
}
