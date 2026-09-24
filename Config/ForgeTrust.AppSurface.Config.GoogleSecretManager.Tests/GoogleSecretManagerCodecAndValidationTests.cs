using ForgeTrust.AppSurface.Config;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

public sealed class GoogleSecretManagerCodecAndValidationTests
{
    [Fact]
    public void SecretReference_Should_RejectInvalidFullResourceShapesAndExtractMissingVersionSafely()
    {
        Assert.False(GoogleSecretManagerSecretReference.IsFullVersionResourceName(null!));
        Assert.False(GoogleSecretManagerSecretReference.IsFullVersionResourceName(""));
        Assert.False(GoogleSecretManagerSecretReference.IsFullVersionResourceName("projects/p/secrets/s"));
        Assert.False(GoogleSecretManagerSecretReference.IsFullVersionResourceName("projects/p/versions/5"));
        Assert.False(GoogleSecretManagerSecretReference.IsFullVersionResourceName("projects/p/secrets/s/versions/"));
        Assert.Equal(string.Empty, GoogleSecretManagerSecretReference.GetVersionFromFullVersionResourceName("short-name"));
        Assert.Equal("5", GoogleSecretManagerSecretReference.GetVersionFromFullVersionResourceName(
            "projects/p/secrets/s/versions/5"));
    }

    [Fact]
    public void SecretReference_Should_EncodeOnlySupportedInjectiveSegments()
    {
        Assert.True(GoogleSecretManagerSecretReference.TryEncodeKey(
            AppSurfaceConfigKey.Parse("Payments:Api_Key-2"), out var encoded));
        Assert.Equal("payments--api_key-2", encoded);

        foreach (var value in new[] { "-leading", "trailing-", "double--dash", "contains/slash", "contains.dot" })
        {
            Assert.False(GoogleSecretManagerSecretReference.TryEncodeKey(AppSurfaceConfigKey.Parse(value), out _), value);
        }

        var tooLong = new string('a', 256);
        Assert.False(GoogleSecretManagerSecretReference.TryEncodeKey(AppSurfaceConfigKey.Parse(tooLong), out _));
    }

    [Fact]
    public void SecretReference_Should_PreserveExactMappingIdsAndSelectExplicitOrDefaultVersion()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "Project", DefaultVersion = "5" };
        var inherited = GoogleSecretManagerSecretReference.FromMapping(
            options, new AppSurfaceGoogleSecretMapping("Payments:Key", "Exact_Id", null));
        var overridden = GoogleSecretManagerSecretReference.FromMapping(
            options, new AppSurfaceGoogleSecretMapping("Payments:Key", "Exact_Id", "7"));
        const string fullName = "projects/OtherProject/secrets/Exact_Id/versions/9";
        var full = GoogleSecretManagerSecretReference.FromMapping(
            options, new AppSurfaceGoogleSecretMapping("Payments:Key", fullName, null));

        Assert.Equal("projects/Project/secrets/Exact_Id/versions/5", inherited.ResourceName);
        Assert.Equal("5", inherited.RequestedVersion);
        Assert.Equal("projects/Project/secrets/Exact_Id/versions/7", overridden.ResourceName);
        Assert.Equal("7", overridden.RequestedVersion);
        Assert.Equal(fullName, full.ResourceName);
        Assert.Null(full.RequestedVersion);
        Assert.Equal(AppSurfaceConfigKey.Parse("Payments:Key"), full.Key);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void SecretReference_Should_EnforceEncodedLengthBoundary(int length, bool expected)
    {
        var key = AppSurfaceConfigKey.Parse(new string('A', length));

        Assert.Equal(expected, GoogleSecretManagerSecretReference.TryEncodeKey(key, out var encoded));
        Assert.Equal(new string('a', length), encoded);
        Assert.Equal(expected, GoogleSecretManagerSecretReference.IsValidSecretId(encoded));
    }

    [Theory]
    [InlineData(126, true)]
    [InlineData(127, false)]
    public void SecretReference_Should_CountHierarchyDelimitersInLengthLimit(int leafLength, bool expected)
    {
        var key = AppSurfaceConfigKey.FromSegments(new string('a', 127), new string('b', leafLength));

        Assert.Equal(expected, GoogleSecretManagerSecretReference.TryEncodeKey(key, out var encoded));
        Assert.Equal(129 + leafLength, encoded.Length);
    }

    [Fact]
    public void SecretReference_Should_ValidateNativeSecretIdBoundaries()
    {
        Assert.True(GoogleSecretManagerSecretReference.IsValidSecretId("a-b_c9"));
        Assert.False(GoogleSecretManagerSecretReference.IsValidSecretId(string.Empty));
        Assert.False(GoogleSecretManagerSecretReference.IsValidSecretId(new string('a', 256)));
        Assert.False(GoogleSecretManagerSecretReference.IsValidSecretId("bad.secret"));
    }

    [Fact]
    public void SecretReference_Should_RejectConventionOutsidePrefixAndInvalidGeneratedId()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project", DefaultVersion = "5" };
        var convention = new AppSurfaceGoogleSecretConvention("Payments", "shared-", null);

        Assert.Throws<ArgumentException>(() => GoogleSecretManagerSecretReference.FromConvention(
            options, convention, "Other:Key"));
        Assert.Throws<FormatException>(() => GoogleSecretManagerSecretReference.FromConvention(
            options, new AppSurfaceGoogleSecretConvention("Payments", "bad.", null), "Payments:Key"));
    }

    [Fact]
    public void MigrationInventory_Should_ReportOnlyRepresentableLegacyDifferencesInStableOrder()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project", DefaultVersion = "5" };
        options.EnableConventionResolver("Payments", "shared-");
        var keys = new[]
        {
            AppSurfaceConfigKey.Parse("Payments:Zed"),
            AppSurfaceConfigKey.Parse("Payments:Api_Key"),
            AppSurfaceConfigKey.Parse("Other:Key"),
            AppSurfaceConfigKey.Parse("Payments:Bad/Key")
        };

        var entries = AppSurfaceGoogleSecretMigrationInventory.Inventory(options, keys);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Payments:Api_Key", entries[0].LogicalKey.Value);
        Assert.Equal("Payments:Zed", entries[1].LogicalKey.Value);
        Assert.Equal("5", entries[0].Version);
        Assert.Contains("MapSecret", entries[0].MapSecretSnippet, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationInventory_Should_RenderOptionalAndOverriddenVersionsExactly()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project" };
        options.EnableConventionResolver("Payments", "shared-");
        options.EnableConventionResolver("Logging", "shared-", "7");

        var entries = AppSurfaceGoogleSecretMigrationInventory.Inventory(options,
            [AppSurfaceConfigKey.Parse("Payments:Key"), AppSurfaceConfigKey.Parse("Logging:Key")]);

        Assert.Collection(entries,
            entry =>
            {
                Assert.Equal("Logging:Key", entry.LogicalKey.Value);
                Assert.Equal("7", entry.Version);
                Assert.Equal("options.MapSecret(AppSurfaceConfigKey.Parse(\"Logging:Key\"), \"shared--key\", version: \"7\");",
                    entry.MapSecretSnippet);
            },
            entry =>
            {
                Assert.Equal("Payments:Key", entry.LogicalKey.Value);
                Assert.Null(entry.Version);
                Assert.Equal("options.MapSecret(AppSurfaceConfigKey.Parse(\"Payments:Key\"), \"shared--key\");",
                    entry.MapSecretSnippet);
            });
    }

    [Fact]
    public void DeclarationValidator_Should_HandleMappedUnmatchedValidAndUnrepresentableDeclarations()
    {
        var mapped = AppSurfaceConfigKey.Parse("Payments:Mapped");
        var valid = AppSurfaceConfigKey.Parse("Payments:Valid");
        var invalid = AppSurfaceConfigKey.Parse("Payments:Bad/Key");
        var registry = CreateRegistry(mapped, valid, invalid);
        var validator = new AppSurfaceGoogleSecretManagerDeclarationValidator(registry);

        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project", DefaultVersion = "5" };
        options.MapSecret(mapped, "mapped", "5");
        options.EnableConventionResolver("Payments", "shared-");

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Bad/Key", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Failures, failure => failure.Contains("Mapped", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclarationValidator_Should_SucceedWhenDeclarationsAreMappedOrUnmatched()
    {
        var mapped = AppSurfaceConfigKey.Parse("Payments:Mapped");
        var unmatched = AppSurfaceConfigKey.Parse("Other:Key");
        var registry = CreateRegistry(mapped, unmatched);
        var validator = new AppSurfaceGoogleSecretManagerDeclarationValidator(registry);
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project", DefaultVersion = "5" };
        options.MapSecret(mapped, "mapped", "5");
        options.EnableConventionResolver("Payments", "shared-");

        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void DeclarationValidator_Should_RetainShapeFailuresAndSkipMalformedMappingAndConventionKeys()
    {
        var validator = new AppSurfaceGoogleSecretManagerDeclarationValidator(
            CreateRegistry(AppSurfaceConfigKey.Parse("Payments:Key")));
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project", DefaultVersion = "5" };
        options.MapSecret("Invalid::Key", "mapped");
        options.EnableConventionResolver("Invalid::Prefix", "shared-");
        options.EnableConventionResolver("Payments", "shared-");

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Google Secret Manager logical key is not a valid logical key.", result.Failures);
        Assert.Contains("Google Secret Manager convention prefix is not a valid logical key.", result.Failures);
        Assert.DoesNotContain(result.Failures, failure => failure.Contains("declaration", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_Should_RejectUnsupportedNonemptySecretIdPrefix()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project", DefaultVersion = "5" };
        options.EnableConventionResolver("Payments", "bad.");

        var result = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Convention 'Payments' has an invalid SecretIdPrefix.", result.Failures);
    }

    private static ConfigDeclarationRegistry CreateRegistry(params AppSurfaceConfigKey[] keys) =>
        new(
            keys.Select(key => new ConfigAuditKnownEntry(key, null, typeof(string))),
            [],
            new ConfigKeyInputParser(Options.Create(new AppSurfaceConfigKeyOptions())));
}
