using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// testcard protects: public recipe, policy, and canonical identity contracts.
// fails_when: a reader accepts unknown fields, a digest changes silently, or a bound is widened.
// why_new: the private regression vectors need a native xUnit promotion gate.
// seam: public Read/Write/Validate APIs, with InternalsVisibleTo only for diagnostics tests.
// no_claim: these tests do not prove network acquisition, image assembly, or native CI.
public sealed class ContractsTests
{
    [Theory]
    [InlineData("https://example.test/%252e")]
    [InlineData("https://example.test/%2E")]
    [InlineData("https://example.test/a/../b")]
    public void EncodedAndRelativeUriAliasesCannotAcquireAuthority(string value)
        => Assert.Throws<ArgumentException>(() => KeycloakCanonicalization.CanonicalizeHttpsUri(value));

    [Fact]
    public void EmptyAlignmentGroupCannotPassAsADeclaredOperation()
    {
        var empty = new ArtifactReplacementGroup("empty", [], []);
        var recipe = TestRecipes.Recipe() with { Operations = [new KeycloakOperation.ArtifactReplacementGroupOperation(empty)] };
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe), KeycloakFailureCode.Invalid);
    }

    [Fact]
    // protects: the approved production recipe remains valid through its public contract.
    public void ValidRecipeAndThemeOnlyZeroOperationsPass()
    {
        Assert.Equal(KeycloakRecipeMode.Security, KeycloakRecipeValidation.Validate(TestRecipes.Recipe()).Mode);
        Assert.Equal(KeycloakRecipeMode.ThemeOnly, KeycloakRecipeValidation.Validate(TestRecipes.Recipe(KeycloakRecipeMode.ThemeOnly, [])).Mode);
    }

    [Fact]
    // protects: one Maven copy can upgrade versions while preserving identity and destination.
    public void UpgradeCoordinatesPassAndCanonicalDigestChanges()
    {
        var recipe = TestRecipes.Recipe();
        var upgraded = recipe with { Operations = recipe.Operations.SetItem(0, TestRecipes.ArtifactGroup("artifacts", "/opt/keycloak/lib/server.jar", "3")) };
        var originalBytes = KeycloakCanonicalJson.Write(recipe);
        var upgradedBytes = KeycloakCanonicalJson.Write(upgraded);
        Assert.NotEqual(Convert.ToHexString(originalBytes), Convert.ToHexString(upgradedBytes));
        Assert.NotEmpty(upgradedBytes);
    }

    [Fact]
    // protects: canonical serializers expose exact fields and omit ambient ArtifactReference.Path.
    public void CanonicalRecipeAndPolicyRoundTripWithExactFieldInventory()
    {
        var recipeBytes = KeycloakCanonicalJson.Write(TestRecipes.Recipe());
        Assert.Equal(Convert.ToHexString(recipeBytes), Convert.ToHexString(KeycloakCanonicalJson.Write(KeycloakCanonicalJson.ReadRecipe(recipeBytes))));
        using var recipe = JsonDocument.Parse(recipeBytes);
        Assert.Equal(["schemaVersion", "sourceImage", "runtimeFrontendOciIdentities", "operations", "templateBaseline", "mode", "keycloakVersion", "entrypointPath", "themeInputs", "capturedHostInputs", "sourceProfiles", "materialReferences", "resourceLimits"], recipe.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, recipe.RootElement.GetProperty("templateBaseline").ValueKind);
        Assert.False(recipe.RootElement.GetProperty("operations")[0].GetProperty("group").GetProperty("replacements")[0].GetProperty("original").TryGetProperty("path", out _));
        var policyBytes = KeycloakCanonicalJson.Write(TestRecipes.Policy());
        Assert.Equal(Convert.ToHexString(policyBytes), Convert.ToHexString(KeycloakCanonicalJson.Write(KeycloakCanonicalJson.ReadPolicy(policyBytes))));
    }

    [Fact]
    // protects: an FTL replacement carries an exact captured upstream baseline with stable nested fields.
    public void TemplateBaselineIsRequiredForFtlAndHasExactCanonicalShape()
    {
        var recipe = TestRecipes.RecipeWithTemplateBaseline();
        var bytes = KeycloakCanonicalJson.Write(recipe);
        Assert.Equal(Convert.ToHexString(bytes), Convert.ToHexString(KeycloakCanonicalJson.Write(KeycloakCanonicalJson.ReadRecipe(bytes))));
        using var document = JsonDocument.Parse(bytes);
        var baseline = document.RootElement.GetProperty("templateBaseline");
        Assert.Equal(["archivePath", "archiveSha256", "entry", "sha256", "materialId"], baseline.EnumerateObject().Select(x => x.Name).ToArray());
        var changed = recipe with { TemplateBaseline = recipe.TemplateBaseline! with { Sha256 = new string('e', 64) } };
        Assert.NotEqual(Convert.ToHexString(bytes), Convert.ToHexString(KeycloakCanonicalJson.Write(changed)));
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe with { TemplateBaseline = null }), KeycloakFailureCode.Required);
    }

    [Fact]
    public void TemplateBaselineRejectsInvalidIdentityFields()
    {
        var recipe = TestRecipes.RecipeWithTemplateBaseline();
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe with { TemplateBaseline = recipe.TemplateBaseline! with { ArchiveSha256 = "bad" } }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe with { TemplateBaseline = recipe.TemplateBaseline! with { Sha256 = "bad" } }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe with { TemplateBaseline = recipe.TemplateBaseline! with { MaterialId = "missing" } }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe with { TemplateBaseline = recipe.TemplateBaseline! with { ArchivePath = "/opt/keycloak/../keycloak.jar" } }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe with { TemplateBaseline = recipe.TemplateBaseline! with { Entry = "/theme/login.ftl" } }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe with { TemplateBaseline = recipe.TemplateBaseline! with { Entry = "theme\\login.ftl" } }), KeycloakFailureCode.Invalid);
    }

    [Fact]
    // protects: JSON equivalence ignores object member order but changed values remain identity changes.
    public void CanonicalEquivalenceAndDigestSensitivityArePreserved()
    {
        var bytes = KeycloakCanonicalJson.Write(TestRecipes.Recipe());
        Assert.Equal(Convert.ToHexString(bytes), Convert.ToHexString(KeycloakCanonicalJson.Write(KeycloakCanonicalJson.ReadRecipe(ReverseRootProperties(bytes)))));
        var changed = TestRecipes.Recipe() with { EntrypointPath = "/opt/keycloak/bin/other.sh" };
        Assert.NotEqual(Convert.ToHexString(bytes), Convert.ToHexString(KeycloakCanonicalJson.Write(changed)));
        var digestChanged = TestRecipes.Recipe() with { SourceImage = TestRecipes.Recipe().SourceImage with { Index = "sha256:" + new string('b', 64) } };
        Assert.NotEqual(Convert.ToHexString(bytes), Convert.ToHexString(KeycloakCanonicalJson.Write(digestChanged)));
    }

    [Theory]
    [InlineData("unknown", "\"schemaVersion\":1,\"unknown\":1", "unknown field")]
    [InlineData("duplicate", "\"schemaVersion\":1,\"schemaVersion\":1", "duplicate field")]
    [InlineData("ambient path", "\"path\":\"ambient.jar\",\"destination\":\"/opt/keycloak/lib/server.jar\"", "ambient path")]
    public void CanonicalReaderRejectsInvalidFieldVectors(string shape, string replacement, string description)
    {
        Assert.False(string.IsNullOrWhiteSpace(description));
        var canonical = KeycloakCanonicalJson.Write(TestRecipes.Recipe());
        string json;
        if (shape == "ambient path")
        {
            var root = JsonNode.Parse(canonical)!;
            var artifact = root["operations"]![0]!["group"]!["replacements"]![0]!["original"]!;
            artifact["path"] = "ambient.jar";
            Assert.Equal("ambient.jar", artifact["path"]!.GetValue<string>());
            json = root.ToJsonString();
        }
        else
        {
            json = Encoding.UTF8.GetString(canonical).Replace("\"schemaVersion\":1", replacement, StringComparison.Ordinal);
        }
        Assert.Throws<JsonException>(() => KeycloakCanonicalJson.ReadRecipe(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("archive", 65537L, 65536L, 1L << 30, 1L << 30, 300)]
    [InlineData("expanded", 65536L, 65536L, (1L << 30) + 1, 1L << 30, 300)]
    [InlineData("output", 65536L, 65536L, 1L << 30, (1L << 30) + 1, 300)]
    [InlineData("material", 65536L, (1L << 30) + 1, 1L << 30, 1L << 30, 300)]
    [InlineData("deadline", 65536L, 1L << 30, 1L << 30, 1L << 30, 301)]
    public void ResourceCeilingsRejectWidening(string _, long entries, long material, long expanded, long output, int seconds) =>
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { ResourceLimits = new(material, entries, expanded, output, seconds) }), KeycloakFailureCode.Invalid);

    [Fact]
    // protects: all invalid controls remain typed and safe at the public validation boundary.
    public void ValidationRejectsDigestProfileMaterialAndDestinationControls()
    {
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { SourceProfiles = ["keycloak-v1"] }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { MaterialReferences = TestRecipes.Recipe().MaterialReferences.RemoveAt(0) }), KeycloakFailureCode.Conflict);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { RuntimeFrontendOciIdentities = [] }), KeycloakFailureCode.Required);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with
        {
            RuntimeFrontendOciIdentities = [TestRecipes.Oci("frontend", "frontend-bytes") with { ConfigDigest = null }]
        }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { Operations = [TestRecipes.ArtifactGroup("one", "/opt/keycloak/lib/server.jar"), TestRecipes.ArtifactGroup("two", "/opt/keycloak/lib/server.jar")] }), KeycloakFailureCode.Conflict);
        var duplicateTheme = new KeycloakThemeInput("theme-two", new string('a', 64), null, null)
        {
            LocalInputId = "theme-baseline",
            DestinationPath = "/opt/keycloak/themes/keycloak.v2/login/messages.properties"
        };
        var themeRecipe = TestRecipes.Recipe() with
        {
            ThemeInputs = [new KeycloakThemeInput("theme-one", new string('a', 64), null, null) { LocalInputId = "theme-input", DestinationPath = duplicateTheme.DestinationPath }, duplicateTheme]
        };
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(themeRecipe), KeycloakFailureCode.Conflict);
        var overlapDestination = "/opt/keycloak/themes/keycloak.v2/login/messages.properties";
        var overlapRecipe = TestRecipes.Recipe() with
        {
            Operations = [TestRecipes.ArtifactGroup("one", overlapDestination)],
            ThemeInputs = [new KeycloakThemeInput("theme-one", new string('a', 64), null, null) { LocalInputId = "theme-input", DestinationPath = overlapDestination }]
        };
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(overlapRecipe), KeycloakFailureCode.Conflict);
        var mismatch = new KeycloakOperation.ArtifactReplacementGroupOperation(ArtifactReplacementGroup.Create("mismatch", [new ArtifactReplacement(TestRecipes.Artifact("server", "1", "/opt/keycloak/lib/server.jar"), TestRecipes.Artifact("other", "2", "/opt/keycloak/lib/server.jar"))], [new ArtifactCoordinate("com.example", "other", "2", null, "jar")]));
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { Operations = [mismatch] }), KeycloakFailureCode.Conflict);
    }

    [Fact]
    public void PolicyAllowsInitializedEmptyUnusedSecurityFamilies()
    {
        var policy = TestRecipes.Policy(false);
        Assert.Same(policy, KeycloakRecipeValidation.Validate(policy));
        Assert.NotEmpty(KeycloakCanonicalJson.Write(policy));
    }

    [Theory]
    [InlineData("source profiles")]
    [InlineData("operations")]
    [InlineData("theme inputs")]
    [InlineData("captured inputs")]
    [InlineData("materials")]
    public void DefaultImmutableCollectionsAreRejected(string collection)
    {
        var recipe = TestRecipes.Recipe();
        var invalid = collection switch
        {
            "source profiles" => recipe with { SourceProfiles = default },
            "operations" => recipe with { Operations = default },
            "theme inputs" => recipe with { ThemeInputs = default },
            "captured inputs" => recipe with { CapturedHostInputs = default },
            _ => recipe with { MaterialReferences = default }
        };
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(invalid), KeycloakFailureCode.Required);
    }

    [Theory]
    [InlineData("unknown policy field", "\"version\":\"v1\",\"unknown\":1")]
    [InlineData("duplicate policy field", "\"version\":\"v1\",\"version\":\"v1\"")]
    public void PolicyReaderRejectsUnknownAndDuplicateFields(string _, string replacement)
    {
        var json = Encoding.UTF8.GetString(KeycloakCanonicalJson.Write(TestRecipes.Policy())).Replace("\"version\":\"v1\"", replacement, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => KeycloakCanonicalJson.ReadPolicy(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    // protects: policy validation rejects malformed authority without requiring unused security families.
    public void PolicyRejectsBadAuthorityAndDigestVectors()
    {
        var policy = TestRecipes.Policy();
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { PermittedHttpsOrigins = ["https://SNAPSHOT.example.test/source/"] }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { PermittedHttpsOrigins = ["https://snapshot.example.test/source/", "https://snapshot.example.test/source/"] }), KeycloakFailureCode.Conflict);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { PermittedPathMappings = ["../escape"] }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { PermittedCoordinateMappings = ["com.example\\escape"] }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { MaterialSha256ByIdentity = ImmutableDictionary<string, string>.Empty.Add("", new string('a', 64)) }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { MaterialSha256ByIdentity = ImmutableDictionary<string, string>.Empty.Add("index", "bad") }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { SigningAlgorithms = ["", "OpenPGP-SHA256"] }), KeycloakFailureCode.Conflict);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { WithdrawnKeys = ["old", "old"] }), KeycloakFailureCode.Conflict);
    }

    [Fact]
    public void RecipeRejectsNullDefaultsUnsupportedAndShapeControls()
    {
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate((KeycloakSecurityRecipe)null!), KeycloakFailureCode.Required);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { SchemaVersion = 2 }), KeycloakFailureCode.Unsupported);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe(KeycloakRecipeMode.Security, [])), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe(KeycloakRecipeMode.ThemeOnly, [TestRecipes.ArtifactGroup()])), KeycloakFailureCode.Conflict);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { SourceImage = TestRecipes.Recipe().SourceImage with { Platform = "windows/amd64" } }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { ResourceLimits = null! }), KeycloakFailureCode.Invalid);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { Operations = [TestRecipes.Runtime(), TestRecipes.Runtime("runtime-2")] }), KeycloakFailureCode.Unsupported);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { Operations = [TestRecipes.Archive("archive-1"), TestRecipes.Archive("archive-2")] }), KeycloakFailureCode.Unsupported);
    }

    [Theory]
    [InlineData("source image", KeycloakFailureCode.Required)]
    [InlineData("entrypoint", KeycloakFailureCode.Required)]
    [InlineData("mode", KeycloakFailureCode.Invalid)]
    [InlineData("config digest", KeycloakFailureCode.Required)]
    [InlineData("diff ids", KeycloakFailureCode.Required)]
    [InlineData("captured URI", KeycloakFailureCode.Invalid)]
    [InlineData("capture binding", KeycloakFailureCode.Invalid)]
    [InlineData("download URI", KeycloakFailureCode.Required)]
    public void RecipeShortCircuitGuardsReturnTheirStableCodes(string shape, KeycloakFailureCode expected)
    {
        var recipe = TestRecipes.Recipe();
        var invalid = shape switch
        {
            "source image" => recipe with { SourceImage = null! },
            "entrypoint" => recipe with { EntrypointPath = null! },
            "mode" => recipe with { Mode = (KeycloakRecipeMode)99 },
            "config digest" => recipe with { SourceImage = recipe.SourceImage with { ConfigDigest = "bad" } },
            "diff ids" => recipe with { SourceImage = recipe.SourceImage with { DiffIds = default } },
            "captured URI" => recipe with { MaterialReferences = recipe.MaterialReferences.SetItem(0, recipe.MaterialReferences[0] with { CapturedHostInput = true }) },
            "capture binding" => recipe with { MaterialReferences = recipe.MaterialReferences.SetItem(0, recipe.MaterialReferences[0] with { CapturedHostInput = true, ExactHttpsUri = string.Empty }) },
            _ => recipe with { MaterialReferences = recipe.MaterialReferences.SetItem(0, recipe.MaterialReferences[0] with { ExactHttpsUri = string.Empty }) }
        };
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(invalid), expected);
    }

    [Theory]
    [InlineData("runtime version", KeycloakFailureCode.Required)]
    [InlineData("runtime template", KeycloakFailureCode.Unsupported)]
    [InlineData("runtime source profile", KeycloakFailureCode.Unsupported)]
    [InlineData("runtime OCI", KeycloakFailureCode.Conflict)]
    [InlineData("repository snapshot", KeycloakFailureCode.Invalid)]
    [InlineData("required package", KeycloakFailureCode.Invalid)]
    [InlineData("removed package", KeycloakFailureCode.Invalid)]
    public void RuntimeProfileShapesReturnTheirStableCodes(string shape, KeycloakFailureCode expected)
    {
        var operation = (KeycloakOperation.RuntimeProfileOperation)TestRecipes.Runtime();
        var profile = operation.Profile;
        var invalidProfile = shape switch
        {
            "runtime version" => profile with { Version = string.Empty },
            "runtime template" => profile with { TemplateName = "unknown" },
            "runtime source profile" => profile with { SupportedSourceProfiles = ["keycloak-v1"] },
            "runtime OCI" => profile with { RuntimeOciIdentities = [TestRecipes.Oci("bad", "missing")] },
            "repository snapshot" => profile with { RepositorySnapshots = [profile.RepositorySnapshots[0] with { SigningFingerprint = string.Empty }] },
            "required package" => profile with { RequiredPackages = [profile.RequiredPackages[0] with { MaterialId = string.Empty }, profile.RequiredPackages[1], profile.RequiredPackages[2]] },
            _ => profile with { RemovedPackages = [profile.RemovedPackages[0] with { ExpectedVersion = string.Empty }, profile.RemovedPackages[1]] }
        };
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { Operations = [operation with { Profile = invalidProfile }] }), expected);
    }

    [Theory]
    [InlineData("required package name")]
    [InlineData("removed package name")]
    [InlineData("required package shell name")]
    [InlineData("required package version")]
    [InlineData("required package architecture")]
    [InlineData("removed package version")]
    [InlineData("removed package architecture")]
    public void RuntimeProfilePackageIdentityRemainsClosed(string shape)
    {
        var operation = (KeycloakOperation.RuntimeProfileOperation)TestRecipes.Runtime();
        var profile = shape switch
        {
            "required package name" => operation.Profile with { RequiredPackages = [operation.Profile.RequiredPackages[0] with { Name = "zlib" }, operation.Profile.RequiredPackages[1], operation.Profile.RequiredPackages[2]] },
            "removed package name" => operation.Profile with { RemovedPackages = [operation.Profile.RemovedPackages[0], operation.Profile.RemovedPackages[1] with { Name = "wget" }] },
            "required package shell name" => operation.Profile with { RequiredPackages = [operation.Profile.RequiredPackages[0] with { Name = "openssl;rm -rf" }, operation.Profile.RequiredPackages[1], operation.Profile.RequiredPackages[2]] },
            "required package version" => operation.Profile with { RequiredPackages = [operation.Profile.RequiredPackages[0] with { Version = "3;echo" }, operation.Profile.RequiredPackages[1], operation.Profile.RequiredPackages[2]] },
            "required package architecture" => operation.Profile with { RequiredPackages = [operation.Profile.RequiredPackages[0] with { Architecture = "arm64" }, operation.Profile.RequiredPackages[1], operation.Profile.RequiredPackages[2]] },
            "removed package version" => operation.Profile with { RemovedPackages = [operation.Profile.RemovedPackages[0] with { ExpectedVersion = "7;echo" }, operation.Profile.RemovedPackages[1]] },
            _ => operation.Profile with { RemovedPackages = [operation.Profile.RemovedPackages[0] with { Architecture = "arm64" }, operation.Profile.RemovedPackages[1]] }
        };
        var expected = shape is "required package name" or "removed package name" or "required package shell name" ? KeycloakFailureCode.Unsupported : KeycloakFailureCode.Invalid;
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { Operations = [operation with { Profile = profile }] }), expected);
    }

    [Fact]
    public void EntrypointPathEscapeIsRejected()
    {
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { EntrypointPath = "/opt/keycloak/bin/../shell" }), KeycloakFailureCode.Invalid);
    }

    [Fact]
    // protects: Docker 29 receives an offline build and never emits an attestation index.
    public void DockerBuildArgumentsDisableNetworkAndProvenance()
    {
        var root = CreateRevalidationFixture(out var manifestDigest);
        try
        {
            var result = new ContextMaterializationResult(root, manifestDigest, manifestDigest, 1, 1, null, "/opt/keycloak/bin/kc.sh", ContextPlatform.LinuxAmd64, ImmutableDictionary<string, string>.Empty, []);
            var arguments = new KeycloakBuildContext(result, "plan").CreateDockerBuildArguments("local/keycloak:test");
            Assert.Contains("--network=none", arguments);
            Assert.Contains("--provenance=false", arguments);
            Assert.DoesNotContain("--network", arguments.Where(x => x != "--network=none"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DockerBuildArgumentsMapCancellationToSafeBuildFailure()
    {
        var root = CreateRevalidationFixture(out var manifestDigest);
        try
        {
            var result = new ContextMaterializationResult(root, manifestDigest, manifestDigest, 1, 1, null, "/opt/keycloak/bin/kc.sh", ContextPlatform.LinuxAmd64, ImmutableDictionary<string, string>.Empty, []);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var failure = Assert.Throws<KeycloakSecurityException>(() => new KeycloakBuildContext(result, "plan").CreateDockerBuildArguments("local/keycloak:test", cancellation.Token));
            Assert.Equal(KeycloakFailureCode.Cancelled, failure.Code);
            Assert.Equal(KeycloakSafePhase.Build, failure.Phase);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateRevalidationFixture(out string manifestDigest)
    {
        var root = Path.Combine(KeycloakSecurity.PrivateTemporaryRoot(), "keycloak-build-args-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "sentinel.txt");
        File.WriteAllText(file, "sentinel", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, (UnixFileMode)420);
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("sentinel"))).ToLowerInvariant();
        var manifest = $"{{\"files\":[{{\"path\":\"sentinel.txt\",\"sha256\":\"{sha}\",\"length\":8,\"mode\":420}}],\"directories\":[]}}";
        var manifestPath = Path.Combine(root, "context-manifest.json");
        File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));
        manifestDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant();
        return root;
    }

    [Theory]
    [InlineData("group missing member", KeycloakFailureCode.Conflict)]
    [InlineData("group duplicate member", KeycloakFailureCode.Conflict)]
    [InlineData("artifact path", KeycloakFailureCode.Invalid)]
    [InlineData("coordinate", KeycloakFailureCode.Invalid)]
    [InlineData("archive version", KeycloakFailureCode.Unsupported)]
    [InlineData("archive modules", KeycloakFailureCode.Required)]
    public void ReplacementAndArchiveShapesReturnTheirStableCodes(string shape, KeycloakFailureCode expected)
    {
        var group = (KeycloakOperation.ArtifactReplacementGroupOperation)TestRecipes.ArtifactGroup();
        var replacement = group.Group.Replacements[0];
        KeycloakOperation invalidOperation;
        if (shape == "group missing member") invalidOperation = group with { Group = group.Group with { RequiredMembers = [new ArtifactCoordinate("com.example", "missing", "1", null, "jar")] } };
        else if (shape == "group duplicate member") invalidOperation = group with { Group = group.Group with { RequiredMembers = [replacement.Replacement.Coordinate, replacement.Replacement.Coordinate] } };
        else if (shape == "artifact path") invalidOperation = group with { Group = group.Group with { Replacements = [new ArtifactReplacement(replacement.Original with { Destination = "/opt/keycloak/../server.jar" }, replacement.Replacement)] } };
        else if (shape == "coordinate") invalidOperation = group with { Group = group.Group with { Replacements = [new ArtifactReplacement(replacement.Original with { Coordinate = replacement.Original.Coordinate with { Artifact = "bad artifact" } }, replacement.Replacement)] } };
        else if (shape == "archive version")
        {
            var archive = (KeycloakOperation.EmbeddedArchiveReplacementOperation)TestRecipes.Archive();
            invalidOperation = archive with { Replacement = archive.Replacement with { Version = "2" } };
        }
        else
        {
            var archive = (KeycloakOperation.EmbeddedArchiveReplacementOperation)TestRecipes.Archive();
            invalidOperation = archive with { Replacement = archive.Replacement with { ModuleSet = null } };
        }
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { Operations = [invalidOperation] }), expected);
    }

    [Fact]
    public void PolicyInitializationGuardsReturnStableCodes()
    {
        var policy = TestRecipes.Policy();
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate((KeycloakMaterialTrustPolicy)null!), KeycloakFailureCode.Required);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { Version = string.Empty }), KeycloakFailureCode.Required);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { PermittedHttpsOrigins = default }), KeycloakFailureCode.Required);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { PermittedPathMappings = [] }), KeycloakFailureCode.Required);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { PermittedCoordinateMappings = [] }), KeycloakFailureCode.Required);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { MaterialSha256ByIdentity = ImmutableDictionary<string, string>.Empty }), KeycloakFailureCode.Required);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { SigningFingerprints = default }), KeycloakFailureCode.Required);
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(policy with { HistoricalExactReleaseBytes = default }), KeycloakFailureCode.Required);
    }

    private static byte[] ReverseRootProperties(byte[] json)
    {
        using var document = JsonDocument.Parse(json); using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream))
        { writer.WriteStartObject(); foreach (var property in document.RootElement.EnumerateObject().Reverse()) { writer.WritePropertyName(property.Name); property.Value.WriteTo(writer); } writer.WriteEndObject(); }
        return stream.ToArray();
    }

    // testcard protects: public readers reject malformed nested wire values before creating identity.
    // fails_when: kind confusion, ambient paths, or mismatched operation IDs enter canonical bytes.
    // why_new: v30 misses compatibility Read and nested parser alternatives.
    // seam: public canonical readers and writers; no reflection or private serializer calls.
    // no_claim: parser rejection does not establish material or runtime provenance.
    [Fact]
    public void CompatibilityReadPreservesTheCompleteCanonicalRecipe()
    {
        var bytes = KeycloakCanonicalJson.Write(TestRecipes.Recipe());
        Assert.Equal(bytes, KeycloakCanonicalJson.Write(KeycloakCanonicalJson.Read(bytes)));
    }

    [Theory]
    [InlineData("unknown operation")]
    [InlineData("ambient original path")]
    [InlineData("artifact unknown field")]
    [InlineData("group id mismatch")]
    [InlineData("profile id mismatch")]
    [InlineData("archive id mismatch")]
    [InlineData("classifier wrong kind")]
    [InlineData("material boolean string")]
    [InlineData("material boolean null")]
    [InlineData("material digest number")]
    [InlineData("module hash number")]
    [InlineData("package index value boolean")]
    [InlineData("operations object")]
    [InlineData("source diff object")]
    [InlineData("required members object")]
    [InlineData("missing snapshot suite")]
    [InlineData("mode numeric string")]
    [InlineData("source profile number")]
    public void NestedWireKindAndIdentityMutationsAreRejected(string mutation)
    {
        var root = JsonNode.Parse(KeycloakCanonicalJson.Write(TestRecipes.Recipe()))!;
        var operations = root["operations"]!;
        var original = operations[0]!["group"]!["replacements"]![0]!["original"]!;
        switch (mutation)
        {
            case "unknown operation": operations[0]!["kind"] = "FutureOperation"; break;
            case "ambient original path": original["path"] = "/private/ambient.jar"; break;
            case "artifact unknown field": original["unknown"] = true; break;
            case "group id mismatch": operations[0]!["group"]!["id"] = "other"; break;
            case "profile id mismatch": operations[1]!["profile"]!["id"] = "other"; break;
            case "archive id mismatch": operations[2]!["replacement"]!["id"] = "other"; break;
            case "classifier wrong kind": original["coordinate"]!["classifier"] = 7; break;
            case "material boolean string": root["materialReferences"]![0]!["capturedHostInput"] = "false"; break;
            case "material boolean null": root["materialReferences"]![0]!["capturedHostInput"] = null; break;
            case "material digest number": root["materialReferences"]![0]!["sha256"] = 7; break;
            case "module hash number": operations[2]!["replacement"]!["moduleSet"]!["sourceModuleSha256ByIdentity"]!["source"] = 7; break;
            case "package index value boolean": operations[1]!["profile"]!["repositorySnapshots"]![0]!["packageIndexMaterialIds"]!["main"] = true; break;
            case "operations object": root["operations"] = new JsonObject(); break;
            case "source diff object": root["sourceImage"]!["diffIds"] = new JsonObject(); break;
            case "required members object": operations[0]!["group"]!["requiredMembers"] = new JsonObject(); break;
            case "missing snapshot suite": operations[1]!["profile"]!["repositorySnapshots"]![0]!.AsObject().Remove("suite"); break;
            case "mode numeric string": root["mode"] = "1"; break;
            case "source profile number": root["sourceProfiles"]![0] = 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.Throws<JsonException>(() => KeycloakCanonicalJson.ReadRecipe(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Theory]
    [InlineData("digest map array")]
    [InlineData("digest number")]
    [InlineData("origin null")]
    [InlineData("unknown nested property")]
    public void PolicyWireKindsDoNotBecomeStringIdentities(string mutation)
    {
        var root = JsonNode.Parse(KeycloakCanonicalJson.Write(TestRecipes.Policy()))!;
        switch (mutation)
        {
            case "digest map array": root["materialSha256ByIdentity"] = new JsonArray(); break;
            case "digest number": root["materialSha256ByIdentity"]!["index"] = 7; break;
            case "origin null": root["permittedHttpsOrigins"]![0] = null; break;
            case "unknown nested property": root["futurePolicy"] = new JsonObject(); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.Throws<JsonException>(() => KeycloakCanonicalJson.ReadPolicy(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public void OptionalNullWireFieldsCanonicalizeToTheirOmittedForm()
    {
        var expected = KeycloakCanonicalJson.Write(TestRecipes.Recipe());
        var root = JsonNode.Parse(expected)!;
        var artifact = root["operations"]![0]!["group"]!["replacements"]![0]!["original"]!;
        artifact["path"] = null;
        artifact["coordinate"]!["classifier"] = null;
        Assert.Equal(expected, KeycloakCanonicalJson.Write(KeycloakCanonicalJson.ReadRecipe(Encoding.UTF8.GetBytes(root.ToJsonString()))));
    }

    [Fact]
    public void ClassifierAndOptionalThemeAuthoritySurviveCanonicalRoundTrip()
    {
        var recipe = TestRecipes.Recipe();
        var operation = (KeycloakOperation.ArtifactReplacementGroupOperation)recipe.Operations[0];
        var pair = operation.Group.Replacements[0];
        var original = pair.Original with { Coordinate = pair.Original.Coordinate with { Classifier = "linux-x86_64" } };
        var replacement = pair.Replacement with { Coordinate = pair.Replacement.Coordinate with { Classifier = "linux-x86_64" } };
        var group = operation with { Group = operation.Group with { Replacements = [new(original, replacement)], RequiredMembers = [replacement.Coordinate] } };
        recipe = recipe with
        {
            Operations = recipe.Operations.SetItem(0, group),
            ThemeInputs = [new KeycloakThemeInput("theme", new string('a', 64), "https://snapshot.example.test/source/", "reviewed-host")
            { LocalInputId = "theme-input", DestinationPath = "/opt/keycloak/themes/neutral/login/theme.properties" }]
        };
        var bytes = KeycloakCanonicalJson.Write(recipe);
        var parsed = KeycloakCanonicalJson.ReadRecipe(bytes);
        var parsedGroup = (KeycloakOperation.ArtifactReplacementGroupOperation)parsed.Operations[0];
        Assert.Equal("linux-x86_64", parsedGroup.Group.RequiredMembers[0].Classifier);
        Assert.Equal("reviewed-host", parsed.ThemeInputs[0].CapturedHostInput);
        Assert.Equal(recipe.ThemeInputs[0].ExactUri, parsed.ThemeInputs[0].ExactUri);
        Assert.Equal(bytes, KeycloakCanonicalJson.Write(parsed));
    }

    [Fact]
    public void PolicyMapKeysUseOrdinalOrderAndChangedDigestChangesIdentity()
    {
        var map = ImmutableDictionary<string, string>.Empty
            .Add("z", new string('a', 64)).Add("a", new string('b', 64))
            .Add("Z", new string('c', 64)).Add("A", new string('d', 64));
        var policy = TestRecipes.Policy() with { MaterialSha256ByIdentity = map };
        var bytes = KeycloakCanonicalJson.Write(policy);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(["A", "Z", "a", "z"], document.RootElement.GetProperty("materialSha256ByIdentity").EnumerateObject().Select(x => x.Name).ToArray());
        var changed = KeycloakCanonicalJson.Write(policy with { MaterialSha256ByIdentity = map.SetItem("A", new string('e', 64)) });
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA256.HashData(changed)));
    }

    // protects: enum spellings and numeric JSON values cannot alias supported recipe modes.
    // fails_when: an unknown alphabetic name, wrong case, or ordinal enters recipe identity.
    // why_new: separates lexical enum rejection from a JSON kind mismatch.
    // seam: strict public recipe reader; no reflection or private serializer calls.
    // no_claim: reader rejection does not prove any image or material bytes.
    [Theory]
    [InlineData("unknown alphabetic")]
    [InlineData("mode case")]
    [InlineData("numeric JSON")]
    public void ModeWireShapesCannotAliasSupportedEnumValues(string shape)
    {
        var root = JsonNode.Parse(KeycloakCanonicalJson.Write(TestRecipes.Recipe()))!;
        switch (shape)
        {
            case "unknown alphabetic": root["mode"] = "FutureMode"; break;
            case "mode case": root["mode"] = "THEMEONLY"; break;
            case "numeric JSON": root["mode"] = 0; break;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }
        var failure = Assert.Throws<JsonException>(() => KeycloakCanonicalJson.ReadRecipe(Encoding.UTF8.GetBytes(root.ToJsonString())));
        Assert.Equal("Recipe JSON does not match the complete supported schema.", failure.Message);
    }

    // protects: optional text distinguishes omission/null from a real string without coercion.
    // fails_when: an omitted/null field becomes text or a present value is discarded.
    // why_new: directly verifies the intentionally visible JSON boundary helper's three outcomes.
    // seam: internal KeycloakJsonExtensions exposed to this test assembly; no reflection.
    // no_claim: this helper does not authorize optional input values.
    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"value\":null}", null)]
    [InlineData("{\"value\":\"reviewed\"}", "reviewed")]
    public void NullableJsonTextPreservesMissingNullAndStringSemantics(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, KeycloakJsonExtensions.GetNullableString(document.RootElement, "value"));
        Assert.Equal(json, document.RootElement.GetRawText());
    }

}
