using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
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

    private static void ExpectRecipeFailure(KeycloakSecurityRecipe recipe, KeycloakFailureCode expected)
    {
        var exception = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(recipe));
        Assert.Equal(expected, exception.Code);
    }
}
