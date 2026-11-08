using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

// testcard protects: public validation keeps every closed contract boundary typed and stable.
// fails_when: a null/default/duplicate/range mutation is accepted or reports the wrong code.
// why_new: these mutations cover early short-circuit operands without duplicating operation proof tests.
// seam: public KeycloakRecipeValidation and KeycloakCanonicalJson APIs; no reflection.
// no_claim: successful validation does not prove acquisition, Docker, or runtime conformance.
public sealed class ValidationBoundaryTests
{
    [Fact]
    public void NullRecipeReturnsRequired()
        => ExpectRecipeFailure(null!, KeycloakFailureCode.Required);

    [Theory]
    [InlineData("schema", KeycloakFailureCode.Unsupported)]
    [InlineData("source null", KeycloakFailureCode.Required)]
    [InlineData("source profiles default", KeycloakFailureCode.Required)]
    [InlineData("source profiles empty", KeycloakFailureCode.Required)]
    [InlineData("entrypoint null", KeycloakFailureCode.Required)]
    [InlineData("mode", KeycloakFailureCode.Invalid)]
    [InlineData("version", KeycloakFailureCode.Invalid)]
    [InlineData("source profile", KeycloakFailureCode.Invalid)]
    [InlineData("source index", KeycloakFailureCode.Invalid)]
    [InlineData("source platform", KeycloakFailureCode.Invalid)]
    [InlineData("source config", KeycloakFailureCode.Required)]
    [InlineData("source diff ids", KeycloakFailureCode.Required)]
    [InlineData("source diff digest", KeycloakFailureCode.Required)]
    [InlineData("source manifest digest", KeycloakFailureCode.Invalid)]
    [InlineData("source archive material", KeycloakFailureCode.Required)]
    [InlineData("frontend default", KeycloakFailureCode.Required)]
    [InlineData("frontend empty", KeycloakFailureCode.Required)]
    [InlineData("frontend id", KeycloakFailureCode.Unsupported)]
    [InlineData("frontend null", KeycloakFailureCode.Invalid)]
    [InlineData("frontend config", KeycloakFailureCode.Invalid)]
    [InlineData("frontend reference", KeycloakFailureCode.Invalid)]
    [InlineData("operations default", KeycloakFailureCode.Required)]
    [InlineData("operation null", KeycloakFailureCode.Conflict)]
    [InlineData("theme inputs default", KeycloakFailureCode.Required)]
    [InlineData("theme input null", KeycloakFailureCode.Conflict)]
    [InlineData("captured inputs default", KeycloakFailureCode.Required)]
    [InlineData("materials default", KeycloakFailureCode.Required)]
    [InlineData("material null", KeycloakFailureCode.Conflict)]
    [InlineData("captured URI", KeycloakFailureCode.Invalid)]
    [InlineData("material ceiling", KeycloakFailureCode.Invalid)]
    [InlineData("resource limits", KeycloakFailureCode.Invalid)]
    [InlineData("archive ceiling", KeycloakFailureCode.Invalid)]
    [InlineData("deadline ceiling", KeycloakFailureCode.Invalid)]
    [InlineData("theme digest", KeycloakFailureCode.Invalid)]
    [InlineData("theme binding", KeycloakFailureCode.Invalid)]
    [InlineData("theme destination", KeycloakFailureCode.Invalid)]
    [InlineData("baseline entry", KeycloakFailureCode.Invalid)]
    public void RecipeBoundaryMutationsReturnStableCodes(string mutation, KeycloakFailureCode expected)
    {
        var recipe = TestRecipes.Recipe();
        var invalid = mutation switch
        {
            "schema" => recipe with { SchemaVersion = 2 },
            "source null" => recipe with { SourceImage = null! },
            "source profiles default" => recipe with { SourceProfiles = default },
            "source profiles empty" => recipe with { SourceProfiles = [] },
            "entrypoint null" => recipe with { EntrypointPath = null! },
            "mode" => recipe with { Mode = (KeycloakRecipeMode)99 },
            "version" => recipe with { KeycloakVersion = "26.7.1" },
            "source profile" => recipe with { SourceProfiles = ["keycloak-v1"] },
            "source index" => recipe with { SourceImage = recipe.SourceImage with { Index = "bad" } },
            "source platform" => recipe with { SourceImage = recipe.SourceImage with { Platform = "windows/amd64" } },
            "source config" => recipe with { SourceImage = recipe.SourceImage with { ConfigDigest = null } },
            "source diff ids" => recipe with { SourceImage = recipe.SourceImage with { DiffIds = default } },
            "source diff digest" => recipe with { SourceImage = recipe.SourceImage with { DiffIds = ["bad"] } },
            "source manifest digest" => recipe with { SourceImage = recipe.SourceImage with { PlatformManifest = "bad" } },
            "source archive material" => recipe with { SourceImage = recipe.SourceImage with { ArchiveMaterialId = string.Empty } },
            "frontend default" => recipe with { RuntimeFrontendOciIdentities = default },
            "frontend empty" => recipe with { RuntimeFrontendOciIdentities = [] },
            "frontend id" => recipe with { RuntimeFrontendOciIdentities = [TestRecipes.Oci("runtime", "frontend-bytes")] },
            "frontend null" => recipe with { RuntimeFrontendOciIdentities = [null!] },
            "frontend config" => recipe with { RuntimeFrontendOciIdentities = [TestRecipes.Oci("frontend", "frontend-bytes") with { ConfigDigest = null }] },
            "frontend reference" => recipe with { RuntimeFrontendOciIdentities = [TestRecipes.Oci("frontend", "frontend-bytes") with { Reference = "registry/image bad" }] },
            "operations default" => recipe with { Operations = default },
            "operation null" => recipe with { Operations = [null!] },
            "theme inputs default" => recipe with { ThemeInputs = default },
            "theme input null" => recipe with { ThemeInputs = [null!] },
            "captured inputs default" => recipe with { CapturedHostInputs = default },
            "materials default" => recipe with { MaterialReferences = default },
            "material null" => recipe with { MaterialReferences = [null!] },
            "captured URI" => recipe with
            {
                CapturedHostInputs = [recipe.MaterialReferences[0].Id],
                MaterialReferences = new[] { recipe.MaterialReferences[0] with { CapturedHostInput = true } }.Concat(recipe.MaterialReferences.Skip(1)).ToImmutableArray()
            },
            "material ceiling" => recipe with { MaterialReferences = recipe.MaterialReferences.SetItem(0, recipe.MaterialReferences[0] with { MaxBytes = 0 }) },
            "archive ceiling" => recipe with { ResourceLimits = recipe.ResourceLimits with { MaxArchiveEntries = 65537 } },
            "deadline ceiling" => recipe with { ResourceLimits = recipe.ResourceLimits with { MaxOperationSeconds = 301 } },
            "theme digest" => recipe with { ThemeInputs = [new KeycloakThemeInput("theme", "bad", null, null) { LocalInputId = "theme-input", DestinationPath = "/opt/keycloak/themes/keycloak.v2/login/messages.properties" }] },
            "theme binding" => recipe with { ThemeInputs = [new KeycloakThemeInput("theme", new string('a', 64), null, null) { LocalInputId = "missing", DestinationPath = "/opt/keycloak/themes/keycloak.v2/login/messages.properties" }] },
            "theme destination" => recipe with { ThemeInputs = [new KeycloakThemeInput("theme", new string('a', 64), null, null) { LocalInputId = "theme-input", DestinationPath = "/tmp/theme.ftl" }] },
            "baseline entry" => TestRecipes.RecipeWithTemplateBaseline() with { TemplateBaseline = TestRecipes.RecipeWithTemplateBaseline().TemplateBaseline! with { Entry = "../login.ftl" } },
            _ => recipe with { ResourceLimits = recipe.ResourceLimits with { MaxOutputBytes = 0 } }
        };
        ExpectRecipeFailure(invalid, expected);
    }

    [Theory]
    [InlineData("duplicate operation", KeycloakFailureCode.Conflict)]
    [InlineData("duplicate material", KeycloakFailureCode.Conflict)]
    [InlineData("duplicate theme", KeycloakFailureCode.Conflict)]
    [InlineData("duplicate runtime OCI", KeycloakFailureCode.Unsupported)]
    public void DuplicateIdentitiesReturnStableCodes(string mutation, KeycloakFailureCode expected)
    {
        var recipe = TestRecipes.Recipe();
        var invalid = mutation switch
        {
            "duplicate operation" => recipe with { Operations = [TestRecipes.ArtifactGroup("same"), TestRecipes.ArtifactGroup("same", "/opt/keycloak/lib/other.jar")] },
            "duplicate material" => recipe with { MaterialReferences = recipe.MaterialReferences.SetItem(1, recipe.MaterialReferences[1] with { Id = recipe.MaterialReferences[0].Id }) },
            "duplicate theme" => recipe with
            {
                ThemeInputs = [
                    new KeycloakThemeInput("theme-one", new string('a', 64), null, null) { LocalInputId = "theme-input", DestinationPath = "/opt/keycloak/themes/keycloak.v2/login/messages.properties" },
                    new KeycloakThemeInput("theme-two", new string('a', 64), null, null) { LocalInputId = "theme-baseline", DestinationPath = "/opt/keycloak/themes/keycloak.v2/login/messages.properties" }]
            },
            _ => recipe with { Operations = [TestRecipes.Runtime(), TestRecipes.Runtime("runtime-2")] }
        };
        ExpectRecipeFailure(invalid, expected);
    }

    [Theory]
    [InlineData("null policy", KeycloakFailureCode.Required)]
    [InlineData("version", KeycloakFailureCode.Required)]
    [InlineData("origins default", KeycloakFailureCode.Required)]
    [InlineData("duplicate origin", KeycloakFailureCode.Conflict)]
    [InlineData("digest map", KeycloakFailureCode.Invalid)]
    [InlineData("withdrawn default", KeycloakFailureCode.Required)]
    [InlineData("historical default", KeycloakFailureCode.Required)]
    [InlineData("origin alias", KeycloakFailureCode.Invalid)]
    [InlineData("path escape", KeycloakFailureCode.Invalid)]
    [InlineData("duplicate signer", KeycloakFailureCode.Conflict)]
    [InlineData("empty digest identity", KeycloakFailureCode.Invalid)]
    public void PolicyBoundaryMutationsReturnStableCodes(string mutation, KeycloakFailureCode expected)
    {
        var policy = TestRecipes.Policy();
        if (mutation == "null policy") { var nullPolicyException = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate((KeycloakMaterialTrustPolicy)null!)); Assert.Equal(expected, nullPolicyException.Code); return; }
        var invalid = mutation switch
        {
            "version" => policy with { Version = string.Empty },
            "origins default" => policy with { PermittedHttpsOrigins = default },
            "duplicate origin" => policy with { PermittedHttpsOrigins = [policy.PermittedHttpsOrigins[0], policy.PermittedHttpsOrigins[0]] },
            "withdrawn default" => policy with { WithdrawnKeys = default },
            "historical default" => policy with { HistoricalExactReleaseBytes = default },
            "origin alias" => policy with { PermittedHttpsOrigins = ["https://snapshot.example.test/source/./"] },
            "path escape" => policy with { PermittedPathMappings = ["snapshot.example.test/../escape"] },
            "duplicate signer" => policy with { SigningFingerprints = ["fingerprint", "fingerprint"] },
            "empty digest identity" => policy with { MaterialSha256ByIdentity = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add(string.Empty, new string('a', 64)) },
            _ => policy with { MaterialSha256ByIdentity = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("id", "bad") }
        };
        var policyException = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(invalid));
        Assert.Equal(expected, policyException.Code);
    }

    [Theory]
    [InlineData("null", KeycloakFailureCode.Required)]
    [InlineData("reviewed id", KeycloakFailureCode.Required)]
    [InlineData("bindings default", KeycloakFailureCode.Required)]
    [InlineData("bindings empty", KeycloakFailureCode.Required)]
    [InlineData("duplicate binding", KeycloakFailureCode.Invalid)]
    [InlineData("binding null", KeycloakFailureCode.Invalid)]
    [InlineData("binding path", KeycloakFailureCode.Invalid)]
    [InlineData("binding digest", KeycloakFailureCode.Invalid)]
    [InlineData("binding ceiling", KeycloakFailureCode.Invalid)]
    public void CaptureBindingBoundaryMutationsReturnStableCodes(string mutation, KeycloakFailureCode expected)
    {
        var valid = KeycloakCaptureInputs.Create(new string('a', 64), [new KeycloakLocalInputBinding("material", "/tmp/material", new string('a', 64), 1)]);
        if (mutation == "null") { var nullInputsException = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate((KeycloakCaptureInputs)null!)); Assert.Equal(expected, nullInputsException.Code); return; }
        var invalid = mutation switch
        {
            "reviewed id" => valid with { ReviewedRecipeId = string.Empty },
            "bindings default" => valid with { LocalInputs = default },
            "bindings empty" => valid with { LocalInputs = [] },
            "binding null" => valid with { LocalInputs = [null!] },
            "binding path" => valid with { LocalInputs = [valid.LocalInputs[0] with { LocalPath = " " }] },
            "binding digest" => valid with { LocalInputs = [valid.LocalInputs[0] with { Sha256 = "bad" }] },
            "binding ceiling" => valid with { LocalInputs = [valid.LocalInputs[0] with { MaxBytes = 0 }] },
            _ => valid with { LocalInputs = [valid.LocalInputs[0], valid.LocalInputs[0]] }
        };
        var inputsException = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(invalid));
        Assert.Equal(expected, inputsException.Code);
    }

    [Theory]
    [InlineData("wrong kind")]
    [InlineData("unknown enum")]
    [InlineData("enum case")]
    [InlineData("trailing")]
    [InlineData("unknown nested")]
    [InlineData("duplicate nested")]
    public void StrictRecipeReaderRejectsMalformedKindsEnumsTrailingAndNestedFields(string mutation)
    {
        var json = Encoding.UTF8.GetString(KeycloakCanonicalJson.Write(TestRecipes.Recipe()));
        var malformed = mutation switch
        {
            "wrong kind" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\"", StringComparison.Ordinal),
            "unknown enum" => json.Replace("\"mode\":\"Security\"", "\"mode\":\"security-extra\"", StringComparison.Ordinal),
            "enum case" => json.Replace("\"mode\":\"Security\"", "\"mode\":\"security\"", StringComparison.Ordinal),
            "trailing" => json + "{}",
            "unknown nested" => json.Replace("\"sourceImage\":{", "\"sourceImage\":{\"unknown\":1,", StringComparison.Ordinal),
            _ => json.Replace("\"sourceImage\":{\"index\":", "\"sourceImage\":{\"index\":\"duplicate\",\"index\":", StringComparison.Ordinal)
        };
        Assert.Throws<JsonException>(() => KeycloakCanonicalJson.ReadRecipe(Encoding.UTF8.GetBytes(malformed)));
    }


    // protects: each remaining v30 root/resource/reference operand rejects independently.
    // fails_when: a lower bound, missing graph association or safe baseline shape is bypassed.
    // why_new: preserves the valid sibling operations while targeting uncovered field alternatives.
    // seam: public Validate; no I/O or reflection. no_claim: does not verify the OCI bytes.
    [Theory]
    [InlineData("source index material", KeycloakFailureCode.Required)]
    [InlineData("materials empty", KeycloakFailureCode.Required)]
    [InlineData("two frontends", KeycloakFailureCode.Unsupported)]
    [InlineData("material resource zero", KeycloakFailureCode.Invalid)]
    [InlineData("entries zero", KeycloakFailureCode.Invalid)]
    [InlineData("expanded zero", KeycloakFailureCode.Invalid)]
    [InlineData("deadline zero", KeycloakFailureCode.Invalid)]
    [InlineData("material oversize", KeycloakFailureCode.Invalid)]
    [InlineData("theme binding empty", KeycloakFailureCode.Invalid)]
    [InlineData("theme destination null", KeycloakFailureCode.Invalid)]
    [InlineData("missing source index", KeycloakFailureCode.Conflict)]
    [InlineData("missing frontend archive", KeycloakFailureCode.Conflict)]
    [InlineData("baseline empty", KeycloakFailureCode.Invalid)]
    [InlineData("baseline dot", KeycloakFailureCode.Invalid)]
    [InlineData("baseline empty segment", KeycloakFailureCode.Invalid)]
    [InlineData("operation control", KeycloakFailureCode.Conflict)]
    public void RemainingRootAndAssociationGuardsRejectTheIntendedField(string mutation, KeycloakFailureCode expected)
    {
        var recipe = TestRecipes.Recipe();
        var baselineRecipe = TestRecipes.RecipeWithTemplateBaseline();
        var theme = baselineRecipe.ThemeInputs[0];
        var changed = mutation switch
        {
            "source index material" => recipe with { SourceImage = recipe.SourceImage with { IndexMaterialId = string.Empty } },
            "materials empty" => recipe with { MaterialReferences = [] },
            "two frontends" => recipe with { RuntimeFrontendOciIdentities = [recipe.RuntimeFrontendOciIdentities[0], recipe.RuntimeFrontendOciIdentities[0] with { Id = "second" }] },
            "material resource zero" => recipe with { ResourceLimits = recipe.ResourceLimits with { MaxMaterialBytes = 0 } },
            "entries zero" => recipe with { ResourceLimits = recipe.ResourceLimits with { MaxArchiveEntries = 0 } },
            "expanded zero" => recipe with { ResourceLimits = recipe.ResourceLimits with { MaxArchiveExpandedBytes = 0 } },
            "deadline zero" => recipe with { ResourceLimits = recipe.ResourceLimits with { MaxOperationSeconds = 0 } },
            "material oversize" => recipe with { MaterialReferences = recipe.MaterialReferences.SetItem(0, recipe.MaterialReferences[0] with { MaxBytes = (1L << 30) + 1 }) },
            "theme binding empty" => baselineRecipe with { ThemeInputs = [theme with { LocalInputId = string.Empty }] },
            "theme destination null" => baselineRecipe with { ThemeInputs = [theme with { DestinationPath = null }] },
            "missing source index" => recipe with { SourceImage = recipe.SourceImage with { IndexMaterialId = "undeclared-index" } },
            "missing frontend archive" => recipe with { RuntimeFrontendOciIdentities = [recipe.RuntimeFrontendOciIdentities[0] with { ArchiveMaterialId = "undeclared-archive" }] },
            "baseline empty" => baselineRecipe with { TemplateBaseline = baselineRecipe.TemplateBaseline! with { Entry = string.Empty } },
            "baseline dot" => baselineRecipe with { TemplateBaseline = baselineRecipe.TemplateBaseline! with { Entry = "theme/./login.ftl" } },
            "baseline empty segment" => baselineRecipe with { TemplateBaseline = baselineRecipe.TemplateBaseline! with { Entry = "theme//login.ftl" } },
            "operation control" => recipe with { Operations = recipe.Operations.SetItem(0, TestRecipes.ArtifactGroup("group\ncontrol")) },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        Assert.Same(recipe, KeycloakRecipeValidation.Validate(recipe));
        ExpectRecipeFailure(changed, expected);
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("digest")]
    [InlineData("archive id")]
    [InlineData("manifest null")]
    [InlineData("manifest malformed")]
    [InlineData("config malformed")]
    [InlineData("diff default")]
    [InlineData("diff malformed")]
    [InlineData("index id")]
    public void FrontendGraphFieldsCannotBeOmittedOrMalformed(string mutation)
    {
        var recipe = TestRecipes.Recipe();
        var frontend = recipe.RuntimeFrontendOciIdentities[0];
        var changed = mutation switch
        {
            "platform" => frontend with { Platform = "linux/riscv64" },
            "digest" => frontend with { Digest = "sha256:bad" },
            "archive id" => frontend with { ArchiveMaterialId = string.Empty },
            "manifest null" => frontend with { PlatformManifestDigest = null },
            "manifest malformed" => frontend with { PlatformManifestDigest = "sha256:bad" },
            "config malformed" => frontend with { ConfigDigest = "sha256:bad" },
            "diff default" => frontend with { DiffIds = default },
            "diff malformed" => frontend with { DiffIds = ["sha256:bad"] },
            "index id" => frontend with { IndexMaterialId = string.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        ExpectRecipeFailure(recipe with { RuntimeFrontendOciIdentities = [changed] }, KeycloakFailureCode.Invalid);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("origins empty")]
    [InlineData("coordinate default")]
    [InlineData("map null")]
    [InlineData("algorithms default")]
    public void RemainingPolicyInitializationAlternativesAreRequired(string mutation)
    {
        var policy = TestRecipes.Policy();
        var changed = mutation switch
        {
            "approval" => policy with { OwnerApprovalReference = string.Empty },
            "origins empty" => policy with { PermittedHttpsOrigins = [] },
            "coordinate default" => policy with { PermittedCoordinateMappings = default },
            "map null" => policy with { MaterialSha256ByIdentity = null! },
            "algorithms default" => policy with { SigningAlgorithms = default },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(changed));
        Assert.Equal(KeycloakFailureCode.Required, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
    }

    [Theory]
    [InlineData("https://example.test:443/material")]
    [InlineData("https://user@example.test/material")]
    [InlineData("https://127.0.0.1/material")]
    [InlineData("https://[::1]/material")]
    [InlineData("https://example.test/a?query=1")]
    [InlineData("https://example.test/a#fragment")]
    [InlineData("https://example.test/a\\b")]
    [InlineData("https://example.test/a%2fb")]
    [InlineData("https://example.test/a/../b")]
    [InlineData("https://example.test/\u007f")]
    public void NoncanonicalAuthorityAliasesAreRejectedBeforeIdentity(string uri)
        => Assert.Throws<ArgumentException>(() => KeycloakCanonicalization.CanonicalizeHttpsUri(uri));

    [Fact]
    public void CanonicalNondefaultPortAndPathArePreservedExactly()
    {
        const string uri = "https://example.test:8443/material/a.jar";
        Assert.Equal(uri, KeycloakCanonicalization.CanonicalizeHttpsUri(uri));
        var recipe = TestRecipes.Recipe();
        recipe = recipe with { MaterialReferences = recipe.MaterialReferences.SetItem(0, recipe.MaterialReferences[0] with { ExactHttpsUri = uri }) };
        Assert.Same(recipe, KeycloakRecipeValidation.Validate(recipe));
    }

    [Theory]
    [InlineData(KeycloakEffectState.None, false, false)]
    [InlineData(KeycloakEffectState.None, true, true)]
    [InlineData(KeycloakEffectState.Staged, true, true)]
    public void ExplicitTransientRetryRequiresAnEligibleCleanEffect(KeycloakEffectState effect, bool transient, bool expected)
    {
        var retry = KeycloakRecipeValidation.RetryFor(effect, transient, verifiedCleanedStaging: true);
        Assert.False(retry.AutomaticRetry);
        Assert.Equal(expected, retry.ExplicitFreshAttemptAllowed);
        Assert.Equal(expected ? 1 : 0, retry.MaximumAdditionalAttempts);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void OperationIdentityMustBePresentBeforeAnyEffects(string? id)
    {
        var recipe = TestRecipes.Recipe();
        var invalid = recipe with { Operations = recipe.Operations.SetItem(0, TestRecipes.ArtifactGroup(id!)) };
        ExpectRecipeFailure(invalid, KeycloakFailureCode.Conflict);
    }

    private static void ExpectRecipeFailure(KeycloakSecurityRecipe recipe, KeycloakFailureCode expected)
    {
        var exception = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(recipe));
        Assert.Equal(expected, exception.Code);
    }

    [Theory]
    [InlineData("empty binding identity")]
    [InlineData("empty frontend identity")]
    public void EmptyReferenceIdentitiesAreTypedInvalidBeforeResolution(string mutation)
    {
        if (mutation == "empty binding identity")
        {
            var inputs = KeycloakCaptureInputs.Create(new string('a', 64),
                [new KeycloakLocalInputBinding(string.Empty, "/tmp/owned-material", new string('b', 64), 1)]);
            var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(inputs));
            Assert.Equal(KeycloakFailureCode.Invalid, failure.Code);
            Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
        }
        else
        {
            Assert.Equal("empty frontend identity", mutation);
            var recipe = TestRecipes.Recipe();
            ExpectRecipeFailure(recipe with { RuntimeFrontendOciIdentities = [recipe.RuntimeFrontendOciIdentities[0] with { Id = string.Empty }] }, KeycloakFailureCode.Invalid);
        }
    }


    // protects: malformed authority and image path spellings fail at the typed boundary.
    // fails_when: blank/relative/non-HTTPS input, dot segments, or empty path segments are accepted.
    // why_new: targets remaining URI parsing and path-segment alternatives from v35.
    // seam: public canonicalization and validation APIs; no I/O or reflection.
    // no_claim: these are declaration checks, not acquisition or filesystem proof.
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("relative-material")]
    [InlineData("http://example.test/material")]
    public void MissingOrNonHttpsAuthorityCannotBecomeCanonicalIdentity(string? uri)
    {
        var failure = Assert.Throws<ArgumentException>(() => KeycloakCanonicalization.CanonicalizeHttpsUri(uri!));
        Assert.Equal("value", failure.ParamName);
    }

    [Fact]
    public void InvalidMaterialDigestIsRejectedBeforeReferenceResolution()
    {
        var recipe = TestRecipes.Recipe();
        var material = recipe.MaterialReferences[0];
        var changed = recipe with { MaterialReferences = recipe.MaterialReferences.SetItem(0, material with { Sha256 = "not-a-sha256" }) };
        Assert.Equal(material.Id, changed.SourceImage.IndexMaterialId);
        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(changed));
        Assert.Equal(KeycloakFailureCode.Invalid, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
        Assert.Equal("material", failure.OperationId);
    }

    [Fact]
    public void DuplicateTopLevelFrontendIdentityReturnsItsConflictDiagnostic()
    {
        var recipe = TestRecipes.Recipe();
        var frontend = recipe.RuntimeFrontendOciIdentities[0];
        var invalid = recipe with { RuntimeFrontendOciIdentities = [frontend, frontend] };

        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(invalid));
        Assert.Equal(KeycloakFailureCode.Conflict, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
        Assert.Equal("runtimeFrontendOciIdentities", failure.OperationId);
    }

    [Fact]
    public void ArchiveReplacementWithoutModuleSetReturnsRequiredDiagnostic()
    {
        var recipe = TestRecipes.Recipe();
        var operation = (KeycloakOperation.EmbeddedArchiveReplacementOperation)recipe.Operations[2];
        var invalidOperation = operation with { Replacement = operation.Replacement with { ModuleSet = null } };
        var invalid = recipe with { Operations = recipe.Operations.SetItem(2, invalidOperation) };

        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(invalid));
        Assert.Equal(KeycloakFailureCode.Required, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
        Assert.Equal("archive", failure.OperationId);
    }

    [Theory]
    [InlineData("replacements default")]
    [InlineData("replacements empty")]
    [InlineData("required members default")]
    [InlineData("required members empty")]
    public void ArtifactReplacementGroupMustInitializeBothMembershipCollections(string mutation)
    {
        var recipe = TestRecipes.Recipe();
        var operation = (KeycloakOperation.ArtifactReplacementGroupOperation)recipe.Operations[0];
        var group = mutation switch
        {
            "replacements default" => operation.Group with { Replacements = default },
            "replacements empty" => operation.Group with { Replacements = [] },
            "required members default" => operation.Group with { RequiredMembers = default },
            "required members empty" => operation.Group with { RequiredMembers = [] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        var invalid = recipe with { Operations = recipe.Operations.SetItem(0, operation with { Group = group }) };

        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(invalid));
        Assert.Equal(KeycloakFailureCode.Invalid, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
        Assert.Equal("artifacts", failure.OperationId);
    }

    [Theory]
    [InlineData("empty identity", KeycloakFailureCode.Conflict, "materialReferences")]
    [InlineData("oversized", KeycloakFailureCode.Invalid, "material")]
    public void MaterialMetadataMustHaveAnIdentityAndStayWithinItsCeiling(string mutation, KeycloakFailureCode expectedCode, string expectedOperation)
    {
        var recipe = TestRecipes.Recipe();
        var material = recipe.MaterialReferences[0];
        var invalidMaterial = mutation switch
        {
            "empty identity" => material with { Id = string.Empty },
            "oversized" => material with { MaxBytes = (1L << 30) + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(
            recipe with { MaterialReferences = recipe.MaterialReferences.SetItem(0, invalidMaterial) }));
        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
        Assert.Equal(expectedOperation, failure.OperationId);
    }

    [Fact]
    public void MissingTemplateBaselineMaterialIsRejectedAsInvalid()
    {
        var recipe = TestRecipes.RecipeWithTemplateBaseline();
        var invalid = recipe with { TemplateBaseline = recipe.TemplateBaseline! with { MaterialId = "undeclared-baseline" } };

        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(invalid));
        Assert.Equal(KeycloakFailureCode.Invalid, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
        Assert.Equal("templateBaseline", failure.OperationId);
    }

    [Fact]
    public void UndeclaredPackageIndexMaterialReturnsReferenceConflict()
    {
        var recipe = TestRecipes.Recipe();
        var operation = (KeycloakOperation.RuntimeProfileOperation)recipe.Operations[1];
        var snapshot = operation.Profile.RepositorySnapshots[0];
        var changedSnapshot = snapshot with
        {
            PackageIndexMaterialIds = snapshot.PackageIndexMaterialIds!.SetItem("main", "undeclared-package-index")
        };
        var changedProfile = operation.Profile with { RepositorySnapshots = [changedSnapshot] };
        var changedOperation = operation with { Profile = changedProfile };
        var invalid = recipe with { Operations = recipe.Operations.SetItem(1, changedOperation) };

        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(invalid));
        Assert.Equal(KeycloakFailureCode.Conflict, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
        Assert.Equal("materialReferences", failure.OperationId);
    }

    [Fact]
    public void DuplicateRepositorySnapshotIdsReturnTheirConflictDiagnostic()
    {
        var recipe = TestRecipes.Recipe();
        var operation = (KeycloakOperation.RuntimeProfileOperation)recipe.Operations[1];
        var snapshot = operation.Profile.RepositorySnapshots[0];
        var profile = operation.Profile with { RepositorySnapshots = [snapshot, snapshot] };
        var invalid = recipe with { Operations = recipe.Operations.SetItem(1, operation with { Profile = profile }) };

        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(invalid));
        Assert.Equal(KeycloakFailureCode.Conflict, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
        Assert.Equal("runtime.repositorySnapshots", failure.OperationId);
    }

    [Fact]
    public void RequiredPackageArchitectureMustMatchTheRuntimeOci()
    {
        var recipe = TestRecipes.Recipe();
        var operation = (KeycloakOperation.RuntimeProfileOperation)recipe.Operations[1];
        var package = operation.Profile.RequiredPackages[0] with { Architecture = "arm64" };
        var profile = operation.Profile with { RequiredPackages = operation.Profile.RequiredPackages.SetItem(0, package) };
        var invalid = recipe with { Operations = recipe.Operations.SetItem(1, operation with { Profile = profile }) };

        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(invalid));
        Assert.Equal(KeycloakFailureCode.Invalid, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
        Assert.Equal("runtime", failure.OperationId);
    }

    [Fact]
    public void RemovedPackageMustDeclareAValidArchitecture()
    {
        var recipe = TestRecipes.Recipe();
        var operation = (KeycloakOperation.RuntimeProfileOperation)recipe.Operations[1];
        var package = operation.Profile.RemovedPackages[0] with { Architecture = "arm64" };
        var profile = operation.Profile with { RemovedPackages = operation.Profile.RemovedPackages.SetItem(0, package) };
        var invalid = recipe with { Operations = recipe.Operations.SetItem(1, operation with { Profile = profile }) };

        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(invalid));
        Assert.Equal(KeycloakFailureCode.Invalid, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
        Assert.Equal("runtime", failure.OperationId);
    }

    [Fact]
    public void CanonicalUriWithNoAuthorityIsRejectedWithValueParameter()
    {
        var failure = Assert.Throws<ArgumentException>(() => KeycloakCanonicalization.CanonicalizeHttpsUri("https:///material"));
        Assert.Equal("value", failure.ParamName);
    }

    [Theory]
    [InlineData("/opt/keycloak/bin/./kc.sh")]
    [InlineData("/opt/keycloak/bin//kc.sh")]
    public void EntrypointCannotAliasAnotherCanonicalImagePath(string entrypoint)
    {
        var recipe = TestRecipes.Recipe() with { EntrypointPath = entrypoint };
        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(recipe));
        Assert.Equal(KeycloakFailureCode.Invalid, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
    }

}
