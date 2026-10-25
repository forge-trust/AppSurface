using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class EnvironmentConfigCandidateInspectorTests
{
    private readonly ITestOutputHelper _output;

    public EnvironmentConfigCandidateInspectorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData((int)ConfigKeyInputOrigin.StrictString)]
    [InlineData((int)ConfigKeyInputOrigin.TranslatedDot)]
    // protects=typed provenance suppression and strict aliases; fails_when=typed leaks compatibility origin; no_claim=manager migration behavior.
    public void TypedAndStrictOverloadsExposeIndependentNativeRules(int origin)
    {
        var inspector = CreateInspector();
        var provenanceKey = AppSurfaceConfigKey.Parse("Service:ApiKey")
            .WithInput((ConfigKeyInputOrigin)origin, "Service:ApiKey");
        var typed = inspector.InspectNativeKey("Production", provenanceKey);
        var strict = inspector.InspectNativeKey("Production", "Service:ApiKey");

        Assert.Equal(new[] { "PRODUCTION__SERVICE__APIKEY", "SERVICE__APIKEY" }, typed.Candidates.Select(x => x.Name));
        Assert.All(typed.Candidates, candidate => Assert.False(candidate.IsLegacyAlias));
        Assert.Equal(new[] { "PRODUCTION__SERVICE__APIKEY", "SERVICE__APIKEY", "PRODUCTION_SERVICE:APIKEY", "PRODUCTION__SERVICE:APIKEY", "SERVICE:APIKEY" }, strict.Candidates.Select(x => x.Name));
        Assert.Equal(new[] { false, false, true, true, true }, strict.Candidates.Select(x => x.IsLegacyAlias));
        Assert.All(typed.Candidates.Concat(strict.Candidates), candidate => Assert.False(candidate.IsExplicitMapping));
    }

    [Theory]
    [InlineData("Service:Api-Key", "PRODUCTION__SERVICE__API-KEY", "SERVICE__API-KEY", "PRODUCTION_SERVICE:API_KEY", "PRODUCTION__SERVICE:API_KEY", "SERVICE:API_KEY")]
    [InlineData("A-B", "PRODUCTION__A-B", "A-B", "PRODUCTION_A_B", "PRODUCTION__A_B", "A_B")]
    // protects=native names order scope and alias metadata; fails_when=metadata diverges from independently expected table; no_claim=value resolution.
    public void StrictNativeMetadataMatchesIndependentNameLayerAliasTable(string input, string scoped, string unscoped, params string[] aliases)
    {
        var plan = CreateInspector().InspectNativeKey("Production", input);
        Assert.Equal(new[] { scoped, unscoped }.Concat(aliases), plan.Candidates.Select(candidate => candidate.Name));
        Assert.Equal(new[] { EnvironmentCandidateScope.Scoped, EnvironmentCandidateScope.Unscoped }
            .Concat(Enumerable.Repeat(EnvironmentCandidateScope.Scoped, aliases.Length - 1))
            .Append(EnvironmentCandidateScope.Unscoped), plan.Candidates.Select(candidate => candidate.Scope));
        Assert.Equal(new[] { false, false }.Concat(Enumerable.Repeat(true, aliases.Length)), plan.Candidates.Select(candidate => candidate.IsLegacyAlias));
    }

    [Fact]
    // protects=exact mapping scope and case; fails_when=ancestor mapping rewrites descendants or composition; no_claim=ownership.
    public void ExactMappingPreservesSuffixCaseAndDoesNotRewriteDescendantsOrComposition()
    {
        var inspector = CreateInspector(new AppSurfaceEnvironmentConfigOptions().MapKey("Service", "Root_Config"));

        var root = inspector.InspectNativeKey("Production", "Service");
        var child = inspector.InspectNativeKey("Production", "Service:ApiKey");
        var path = inspector.InspectCompositionPath("Production", AppSurfaceConfigKey.Parse("Service"));

        Assert.Equal(new[] { "PRODUCTION__Root_Config", "Root_Config" }, root.Candidates.Select(x => x.Name));
        Assert.All(root.Candidates, candidate => Assert.True(candidate.IsExplicitMapping));
        Assert.Equal(new[] { "PRODUCTION__SERVICE__APIKEY", "SERVICE__APIKEY", "PRODUCTION_SERVICE:APIKEY", "PRODUCTION__SERVICE:APIKEY", "SERVICE:APIKEY" }, child.Candidates.Select(x => x.Name));
        Assert.All(child.Candidates, candidate => Assert.False(candidate.IsExplicitMapping));
        Assert.Equal(new[] { "PRODUCTION_SERVICE", "SERVICE", "PRODUCTION__SERVICE" }, path.Candidates.Select(x => x.Name));
    }

    [Theory]
    [InlineData("A.B", "A_B", "A__B")]
    [InlineData("A-B", "A_B", "A__B")]
    [InlineData("A:B", "A_B", "A__B")]
    // protects=existing noninjective literal projection; fails_when=composition normalizes unlike the established rules; no_claim=logical identity from names.
    public void CompositionProjectionRetainsLiteralSegmentRules(string input, string flatName, string hierarchicalName)
    {
        var literalPath = input == "A:B" ? AppSurfaceConfigKey.Parse(input) : AppSurfaceConfigKey.FromSegments(input);
        var plan = CreateInspector().InspectCompositionPath("Production", literalPath);
        Assert.Equal(new[] { $"PRODUCTION_{flatName}", flatName, $"PRODUCTION__{hierarchicalName}", hierarchicalName }, plan.Candidates.Select(x => x.Name));
        Assert.Equal(new[] { false, false, true, true }, plan.Candidates.Select(x => x.IsHierarchical));
    }

    [Theory]
    [InlineData("", typeof(FormatException))]
    [InlineData("A::B", typeof(FormatException))]
    [InlineData(" A", typeof(FormatException))]
    [InlineData("A ", typeof(FormatException))]
    [InlineData("A\nB", typeof(FormatException))]
    // protects=strict syntax error contract; fails_when=parse failure becomes success or changes type; no_claim=manager translation.
    public void StrictMalformedInputFailsWithOriginalParseException(string input, Type exceptionType)
    {
        var error = Assert.Throws(exceptionType, () => CreateInspector().InspectNativeKey("Production", input));
        Assert.Equal("config-key-invalid", error.Data["Code"]);
        Assert.Equal("https://appsurface.dev/guides/config-logical-keys#config-key-invalid", error.HelpLink);
        Assert.Equal("none", error.Data["SideEffects"]);
    }

    [Fact]
    // protects=materialized native encoding rejection; fails_when=unsupported key returns empty success or leaks input; no_claim=mapping authorization.
    public void UnencodableTypedKeyFailsImmediatelyWithSafeMetadata()
    {
        var error = Assert.Throws<InvalidOperationException>(() => CreateInspector().InspectNativeKey("Prod", AppSurfaceConfigKey.Parse("a__b")));
        Assert.Equal("config-candidate-unencodable", error.Data["Code"]);
        Assert.Equal("https://appsurface.dev/guides/config-logical-keys#config-candidate-unencodable", error.HelpLink);
        Assert.Equal("none", error.Data["SideEffects"]);
        Assert.DoesNotContain("a__b", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    // protects=long valid inputs keep exact candidates over repeated calls; fails_when any output drifts; no_claim=allocation observation has no performance threshold.
    public void LongInputsRemainCorrectAndRecordBoundedAllocationObservation()
    {
        var inspector = CreateInspector();
        var smallEnvironment = "Prod";
        var smallKey = AppSurfaceConfigKey.Parse("Key9:Leaf7");
        var largeEnvironment = new string('E', 4096);
        var largeKey = AppSurfaceConfigKey.Parse(string.Join(':', Enumerable.Repeat("Key9", 64)));
        var smallExpected = "PROD__KEY9__LEAF7";
        var largeExpected = largeEnvironment.ToUpperInvariant() + "__" + string.Join("__", Enumerable.Repeat("KEY9", 64));

        _ = inspector.InspectNativeKey(smallEnvironment, smallKey);
        _ = inspector.InspectNativeKey(largeEnvironment, largeKey);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 20; index++)
        {
            Assert.Equal(smallExpected, inspector.InspectNativeKey(smallEnvironment, smallKey).Candidates[0].Name);
        }
        var smallAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 20; index++)
        {
            Assert.Equal(largeExpected, inspector.InspectNativeKey(largeEnvironment, largeKey).Candidates[0].Name);
        }
        var largeAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _output.WriteLine("Repeated input observation: small=20 calls/{0} allocated bytes; large=20 calls/{1} allocated bytes; no pass/fail budget.", smallAllocated, largeAllocated);
    }

    [Theory]
    [InlineData("env")]
    [InlineData("strict-env")]
    [InlineData("composition-env")]
    [InlineData("typed-key")]
    [InlineData("strict-input")]
    [InlineData("path")]
    // protects=public null argument identity and remediation; fails_when=null loses parameter or safe fields; no_claim=arbitrary host failure redaction.
    public void NullInputsNameTheRejectedArgumentAndCarrySafeMetadata(string argument)
    {
        var inspector = CreateInspector();
        var error = argument switch
        {
            "env" => Assert.Throws<ArgumentNullException>(() => inspector.InspectNativeKey(null!, AppSurfaceConfigKey.Parse("A"))),
            "strict-env" => Assert.Throws<ArgumentNullException>(() => inspector.InspectNativeKey(null!, "A")),
            "composition-env" => Assert.Throws<ArgumentNullException>(() => inspector.InspectCompositionPath(null!, AppSurfaceConfigKey.Parse("A"))),
            "typed-key" => Assert.Throws<ArgumentNullException>(() => inspector.InspectNativeKey("Prod", (AppSurfaceConfigKey)null!)),
            "strict-input" => Assert.Throws<ArgumentNullException>(() => inspector.InspectNativeKey("Prod", (string)null!)),
            _ => Assert.Throws<ArgumentNullException>(() => inspector.InspectCompositionPath("Prod", (AppSurfaceConfigKey)null!)),
        };
        Assert.Equal(argument is "env" or "strict-env" or "composition-env" ? "environment" : argument == "typed-key" ? "key" : argument == "strict-input" ? "strictInput" : "path", error.ParamName);
        Assert.Equal("config-candidate-null", error.Data["Code"]);
        Assert.Equal("https://appsurface.dev/guides/config-logical-keys#config-candidate-null", error.HelpLink);
        Assert.Equal("none", error.Data["SideEffects"]);
    }

    [Theory]
    [InlineData("", "__A.B", "_A_B", "__A__B")]
    [InlineData("dev:us-east.blue", "DEV_US_EAST_BLUE__A.B", "DEV:US_EAST_BLUE_A_B", "DEV:US_EAST_BLUE__A__B")]
    // protects=empty and unusual environments keep separate normalizers; fails_when=inspection imposes a grammar or unifies families; no_claim=platform portability.
    public void UnusualEnvironmentSpellingRetainsEachFamiliesExistingRules(string environment, string nativeScoped, string flatScoped, string hierarchicalScoped)
    {
        var inspector = CreateInspector();
        var key = AppSurfaceConfigKey.FromSegments("A.B");
        Assert.Equal(new[] { nativeScoped, "A.B" }, inspector.InspectNativeKey(environment, key).Candidates.Select(c => c.Name));
        Assert.Equal(new[] { flatScoped, "A_B", hierarchicalScoped, "A__B" }, inspector.InspectCompositionPath(environment, key).Candidates.Select(c => c.Name));
    }

    [Theory]
    [InlineData("A/B")]
    [InlineData("A_B")]
    [InlineData("Service")]
    // protects=literal slash/underscore spelling and first producer flags; fails_when=dedup infers hierarchy from final shape; no_claim=injective composition identity.
    public void LiteralSingleSegmentsRetainIndependentCompositionMetadata(string segment)
    {
        var inspector = CreateInspector();
        var name = segment.ToUpperInvariant();
        var key = AppSurfaceConfigKey.FromSegments(segment);
        Assert.Equal(new[]
        {
            new EnvironmentCompositionPathCandidate("PROD_" + name, EnvironmentCandidateScope.Scoped, false),
            new EnvironmentCompositionPathCandidate(name, EnvironmentCandidateScope.Unscoped, false),
            new EnvironmentCompositionPathCandidate("PROD__" + name, EnvironmentCandidateScope.Scoped, true),
        }, inspector.InspectCompositionPath("Prod", key).Candidates);
        Assert.Equal(new[] { "PROD__" + name, name }, inspector.InspectNativeKey("Prod", key).Candidates.Select(c => c.Name));
    }

    [Fact]
    // protects=exact mapping bypasses unsupported canonical encoding; fails_when=valid mapping is ignored or legacy aliases remain; no_claim=native ownership.
    public void ExactMappingCanRepresentOtherwiseUnencodableKeyAndStrictDuplicatesKeepCanonicalFlags()
    {
        var inspector = CreateInspector(new AppSurfaceEnvironmentConfigOptions().MapKey("_Secret", "Mixed_Key"));
        Assert.Equal(new[]
        {
            new EnvironmentNativeCandidate("PROD__Mixed_Key", EnvironmentCandidateScope.Scoped, false, true),
            new EnvironmentNativeCandidate("Mixed_Key", EnvironmentCandidateScope.Unscoped, false, true),
        }, inspector.InspectNativeKey("Prod", "_Secret").Candidates);
        Assert.Equal(new[]
        {
            new EnvironmentNativeCandidate("PROD__A", EnvironmentCandidateScope.Scoped, false, false),
            new EnvironmentNativeCandidate("A", EnvironmentCandidateScope.Unscoped, false, false),
            new EnvironmentNativeCandidate("PROD_A", EnvironmentCandidateScope.Scoped, true, false),
        }, inspector.InspectNativeKey("Prod", "A").Candidates);
    }

    private static IEnvironmentConfigCandidateInspector CreateInspector(AppSurfaceEnvironmentConfigOptions? options = null) =>
        new EnvironmentConfigCandidateInspector(new EnvironmentConfigMappingSnapshot(options ?? new AppSurfaceEnvironmentConfigOptions()));
}
