using System.Collections.Immutable;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

// testcard protects: runtime, artifact, and archive operations remain closed and typed at validation.
// fails_when: one operation field is malformed, missing, duplicated, or widened and the wrong code escapes.
// why_new: operation-family boundaries are distinct from recipe-root and canonical-reader coverage.
// seam: public KeycloakRecipeValidation with complete sibling closures preserved in every mutation.
// no_claim: these tests do not prove material capture, archive transformation, or Docker behavior.
public sealed class OperationBoundaryTests
{
    [Theory]
    [InlineData("group null", KeycloakFailureCode.Invalid)]
    [InlineData("replacements default", KeycloakFailureCode.Invalid)]
    [InlineData("replacements empty", KeycloakFailureCode.Invalid)]
    [InlineData("required members default", KeycloakFailureCode.Invalid)]
    [InlineData("required members empty", KeycloakFailureCode.Invalid)]
    [InlineData("artifact digest", KeycloakFailureCode.Invalid)]
    [InlineData("artifact path", KeycloakFailureCode.Invalid)]
    [InlineData("artifact coordinate", KeycloakFailureCode.Invalid)]
    [InlineData("original null", KeycloakFailureCode.Invalid)]
    [InlineData("replacement null", KeycloakFailureCode.Invalid)]
    [InlineData("original mismatch", KeycloakFailureCode.Conflict)]
    [InlineData("destination mismatch", KeycloakFailureCode.Conflict)]
    [InlineData("missing member", KeycloakFailureCode.Conflict)]
    [InlineData("duplicate member", KeycloakFailureCode.Conflict)]
    [InlineData("duplicate destination", KeycloakFailureCode.Conflict)]
    public void ArtifactGroupFieldMutationsReturnStableCodes(string mutation, KeycloakFailureCode expected)
    {
        var operation = (KeycloakOperation.ArtifactReplacementGroupOperation)TestRecipes.ArtifactGroup();
        var group = operation.Group;
        var replacement = group.Replacements[0];
        var invalidGroup = mutation switch
        {
            "group null" => null,
            "replacements default" => group with { Replacements = default },
            "replacements empty" => group with { Replacements = [] },
            "required members default" => group with { RequiredMembers = default },
            "required members empty" => group with { RequiredMembers = [] },
            "artifact digest" => group with { Replacements = [replacement with { Replacement = replacement.Replacement with { Sha256 = "bad" } }] },
            "artifact path" => group with { Replacements = [replacement with { Replacement = replacement.Replacement with { Destination = "/opt/keycloak/../escape" } }] },
            "artifact coordinate" => group with { Replacements = [replacement with { Replacement = replacement.Replacement with { Coordinate = replacement.Replacement.Coordinate with { Artifact = "bad artifact" } } }] },
            "original null" => group with { Replacements = [replacement with { Original = null! }] },
            "replacement null" => group with { Replacements = [replacement with { Replacement = null! }] },
            "original mismatch" => group with { Replacements = [replacement with { Replacement = replacement.Replacement with { Coordinate = replacement.Replacement.Coordinate with { Group = "other" } } }] },
            "destination mismatch" => group with { Replacements = [replacement with { Replacement = replacement.Replacement with { Destination = "/opt/keycloak/lib/other.jar" } }] },
            "missing member" => group with { RequiredMembers = [replacement.Replacement.Coordinate, new ArtifactCoordinate("com.example", "missing", "1", null, "jar")] },
            "duplicate destination" => group with
            {
                Replacements = [
                    replacement,
                    new ArtifactReplacement(
                        TestRecipes.Artifact("other", "1", replacement.Original.Destination),
                        TestRecipes.Artifact("other", "2", replacement.Original.Destination))],
                RequiredMembers = [replacement.Replacement.Coordinate, new ArtifactCoordinate("com.example", "other", "2", null, "jar")]
            },
            _ => group with { RequiredMembers = [replacement.Replacement.Coordinate, replacement.Replacement.Coordinate] }
        };
        var invalid = operation with { Group = invalidGroup! };
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { Operations = [invalid] }), expected);
    }

    [Theory]
    [InlineData("profile null", KeycloakFailureCode.Required)]
    [InlineData("version missing", KeycloakFailureCode.Required)]
    [InlineData("template missing", KeycloakFailureCode.Required)]
    [InlineData("source profiles default", KeycloakFailureCode.Required)]
    [InlineData("runtime OCI default", KeycloakFailureCode.Required)]
    [InlineData("snapshots default", KeycloakFailureCode.Required)]
    [InlineData("required packages default", KeycloakFailureCode.Required)]
    [InlineData("removed packages default", KeycloakFailureCode.Required)]
    [InlineData("observations default", KeycloakFailureCode.Required)]
    [InlineData("version unsupported", KeycloakFailureCode.Unsupported)]
    [InlineData("template unsupported", KeycloakFailureCode.Unsupported)]
    [InlineData("snapshot field", KeycloakFailureCode.Invalid)]
    [InlineData("snapshot null", KeycloakFailureCode.Invalid)]
    [InlineData("snapshot components", KeycloakFailureCode.Invalid)]
    [InlineData("snapshot package map", KeycloakFailureCode.Invalid)]
    [InlineData("runtime OCI field", KeycloakFailureCode.Invalid)]
    [InlineData("runtime OCI null", KeycloakFailureCode.Invalid)]
    [InlineData("runtime OCI duplicate", KeycloakFailureCode.Unsupported)]
    [InlineData("snapshot URI", KeycloakFailureCode.Invalid)]
    [InlineData("snapshot architecture", KeycloakFailureCode.Invalid)]
    [InlineData("required package name", KeycloakFailureCode.Unsupported)]
    [InlineData("removed package name", KeycloakFailureCode.Unsupported)]
    [InlineData("required package field", KeycloakFailureCode.Invalid)]
    [InlineData("required package material", KeycloakFailureCode.Invalid)]
    [InlineData("removed package field", KeycloakFailureCode.Invalid)]
    [InlineData("removed package architecture", KeycloakFailureCode.Invalid)]
    public void RuntimeProfileFieldMutationsReturnStableCodes(string mutation, KeycloakFailureCode expected)
    {
        var operation = (KeycloakOperation.RuntimeProfileOperation)TestRecipes.Runtime();
        var profile = operation.Profile;
        var invalid = mutation switch
        {
            "profile null" => null,
            "version missing" => profile with { Version = string.Empty },
            "template missing" => profile with { TemplateName = string.Empty },
            "source profiles default" => profile with { SupportedSourceProfiles = default },
            "runtime OCI default" => profile with { RuntimeOciIdentities = default },
            "snapshots default" => profile with { RepositorySnapshots = default },
            "required packages default" => profile with { RequiredPackages = default },
            "removed packages default" => profile with { RemovedPackages = default },
            "observations default" => profile with { ExpectedObservations = default },
            "version unsupported" => profile with { Version = "2" },
            "template unsupported" => profile with { TemplateName = "unknown" },
            "snapshot field" => profile with { RepositorySnapshots = [profile.RepositorySnapshots[0] with { SigningFingerprint = string.Empty }] },
            "snapshot null" => profile with { RepositorySnapshots = [null!] },
            "snapshot components" => profile with { RepositorySnapshots = [profile.RepositorySnapshots[0] with { Components = [] }] },
            "snapshot package map" => profile with { RepositorySnapshots = [profile.RepositorySnapshots[0] with { PackageIndexMaterialIds = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty }] },
            "runtime OCI field" => profile with { RuntimeOciIdentities = [profile.RuntimeOciIdentities[0] with { ConfigDigest = null }] },
            "runtime OCI null" => profile with { RuntimeOciIdentities = [null!] },
            "runtime OCI duplicate" => profile with { RuntimeOciIdentities = [profile.RuntimeOciIdentities[0], profile.RuntimeOciIdentities[0]] },
            "snapshot URI" => profile with { RepositorySnapshots = [profile.RepositorySnapshots[0] with { ExactHttpsUri = "https://snapshot.example.test/source/./" }] },
            "snapshot architecture" => profile with { RepositorySnapshots = [profile.RepositorySnapshots[0] with { Architectures = [] }] },
            "required package field" => profile with { RequiredPackages = [profile.RequiredPackages[0] with { Version = "3;echo" }, profile.RequiredPackages[1], profile.RequiredPackages[2]] },
            "required package material" => profile with { RequiredPackages = [profile.RequiredPackages[0] with { MaterialId = null }, profile.RequiredPackages[1], profile.RequiredPackages[2]] },
            "required package name" => profile with { RequiredPackages = [profile.RequiredPackages[0] with { Name = "zlib" }, profile.RequiredPackages[1], profile.RequiredPackages[2]] },
            "removed package field" => profile with { RemovedPackages = [profile.RemovedPackages[0] with { ExpectedVersion = "7;echo" }, profile.RemovedPackages[1]] },
            "removed package name" => profile with { RemovedPackages = [profile.RemovedPackages[0] with { Name = "wget" }, profile.RemovedPackages[1]] },
            _ => profile with { RemovedPackages = [profile.RemovedPackages[0] with { Architecture = "arm64" }, profile.RemovedPackages[1]] }
        };
        var invalidOperation = operation with { Profile = invalid! };
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { Operations = [invalidOperation] }), expected);
    }

    [Theory]
    [InlineData("replacement null", KeycloakFailureCode.Invalid)]
    [InlineData("member declarations empty", KeycloakFailureCode.Invalid)]
    [InlineData("module set null", KeycloakFailureCode.Required)]
    [InlineData("archive version", KeycloakFailureCode.Unsupported)]
    [InlineData("archive template", KeycloakFailureCode.Unsupported)]
    [InlineData("source artifact mismatch", KeycloakFailureCode.Conflict)]
    [InlineData("replacement artifact mismatch", KeycloakFailureCode.Conflict)]
    [InlineData("module destination mismatch", KeycloakFailureCode.Conflict)]
    [InlineData("source module map empty", KeycloakFailureCode.Conflict)]
    [InlineData("replacement module map empty", KeycloakFailureCode.Conflict)]
    [InlineData("module digest", KeycloakFailureCode.Invalid)]
    [InlineData("source artifact null", KeycloakFailureCode.Invalid)]
    [InlineData("module map count", KeycloakFailureCode.Conflict)]
    [InlineData("archive destination", KeycloakFailureCode.Invalid)]
    [InlineData("source coordinate", KeycloakFailureCode.Invalid)]
    public void ArchiveFieldMutationsReturnStableCodes(string mutation, KeycloakFailureCode expected)
    {
        var operation = (KeycloakOperation.EmbeddedArchiveReplacementOperation)TestRecipes.Archive();
        var archive = operation.Replacement;
        var module = archive.ModuleSet!;
        var invalid = mutation switch
        {
            "replacement null" => null,
            "member declarations empty" => archive with { ClassPrefixes = [], VersionedClasses = [], ServicePaths = [] },
            "module set null" => archive with { ModuleSet = null },
            "archive version" => archive with { Version = "2" },
            "archive template" => archive with { TemplateName = "unknown" },
            "source artifact mismatch" => archive with { ModuleSet = module with { SourceArtifact = module.SourceArtifact with { Coordinate = module.SourceArtifact.Coordinate with { Artifact = "other" } } } },
            "replacement artifact mismatch" => archive with { ModuleSet = module with { ReplacementArtifact = module.ReplacementArtifact with { Coordinate = module.ReplacementArtifact.Coordinate with { Artifact = "other" } } } },
            "module destination mismatch" => archive with { ModuleSet = module with { Destination = "/opt/keycloak/lib/other.jar" } },
            "source module map empty" => archive with { ModuleSet = module with { SourceModuleSha256ByIdentity = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty } },
            "replacement module map empty" => archive with { ModuleSet = module with { ReplacementModuleSha256ByIdentity = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty } },
            "source artifact null" => archive with { ModuleSet = module with { SourceArtifact = null! } },
            "module map count" => archive with { ModuleSet = module with { SourceModuleSha256ByIdentity = module.SourceModuleSha256ByIdentity.Add("extra", new string('3', 64)) } },
            "archive destination" => archive with { ModuleSet = module with { Destination = "/opt/keycloak/../app.jar" } },
            "source coordinate" => archive with { Source = archive.Source with { Version = "bad version" } },
            _ => archive with { ModuleSet = module with { SourceModuleSha256ByIdentity = module.SourceModuleSha256ByIdentity.SetItem("source", "bad") } }
        };
        var invalidOperation = operation with { Replacement = invalid! };
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(TestRecipes.Recipe() with { Operations = [invalidOperation] }), expected);
    }

    // testcard protects: callers cannot reorder closed handlers or serialize an unknown family.
    // fails_when: a public operation subtype or a later artifact group bypasses family validation.
    // why_new: v30 default-family and artifact-after-runtime paths are uncovered.
    // seam: public record inheritance and public validation/serialization; no reflection.
    // no_claim: these are declaration controls, not executable transformation proof.
    [Fact]
    public void CallerDefinedOperationIsUnsupportedBeforeCanonicalSerialization()
    {
        var recipe = TestRecipes.Recipe();
        recipe = recipe with { Operations = recipe.Operations.SetItem(0, new FutureOperation("future")) };
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe), KeycloakFailureCode.Unsupported);
        TestRecipes.ExpectFailure(() => KeycloakCanonicalJson.Write(recipe), KeycloakFailureCode.Unsupported);
    }

    private sealed record FutureOperation(string OperationId) : KeycloakOperation(OperationId);

    [Theory]
    [InlineData("artifact after runtime", KeycloakFailureCode.Conflict)]
    [InlineData("runtime after archive", KeycloakFailureCode.Unsupported)]
    [InlineData("no runtime", KeycloakFailureCode.Required)]
    public void FamilyOrderAndRequiredRuntimeAreCheckedIndependently(string mutation, KeycloakFailureCode code)
    {
        var recipe = TestRecipes.Recipe();
        System.Collections.Immutable.ImmutableArray<KeycloakOperation> operations = mutation switch
        {
            "artifact after runtime" => [TestRecipes.Runtime(), TestRecipes.ArtifactGroup()],
            "runtime after archive" => [TestRecipes.Archive(), TestRecipes.Runtime()],
            "no runtime" => [TestRecipes.ArtifactGroup()],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe with { Operations = operations }), code);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("version")]
    [InlineData("classifier")]
    [InlineData("extension")]
    [InlineData("destination empty")]
    [InlineData("destination root")]
    [InlineData("destination relative")]
    [InlineData("destination slash")]
    [InlineData("destination control")]
    public void ArtifactAtomsAndImagePathFormsRejectBeforeAlignment(string mutation)
    {
        var recipe = TestRecipes.Recipe();
        var operation = (KeycloakOperation.ArtifactReplacementGroupOperation)recipe.Operations[0];
        var pair = operation.Group.Replacements[0];
        var original = pair.Original;
        var changed = mutation switch
        {
            "group" => original with { Coordinate = original.Coordinate with { Group = "bad group" } },
            "version" => original with { Coordinate = original.Coordinate with { Version = "bad version" } },
            "classifier" => original with { Coordinate = original.Coordinate with { Classifier = "bad/classifier" } },
            "extension" => original with { Coordinate = original.Coordinate with { Extension = string.Empty } },
            "destination empty" => original with { Destination = string.Empty },
            "destination root" => original with { Destination = "/" },
            "destination relative" => original with { Destination = "opt/keycloak/lib/file.jar" },
            "destination slash" => original with { Destination = "/opt/keycloak/lib\\file.jar" },
            "destination control" => original with { Destination = "/opt/keycloak/lib/\u007ffile.jar" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        var group = operation with { Group = operation.Group with { Replacements = [pair with { Original = changed }] } };
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe with { Operations = recipe.Operations.SetItem(0, group) }), KeycloakFailureCode.Invalid);
    }

    [Theory]
    [InlineData("snapshot digest", KeycloakFailureCode.Invalid)]
    [InlineData("keyring id", KeycloakFailureCode.Invalid)]
    [InlineData("map null", KeycloakFailureCode.Invalid)]
    [InlineData("map key", KeycloakFailureCode.Invalid)]
    [InlineData("map value", KeycloakFailureCode.Invalid)]
    [InlineData("duplicate snapshot", KeycloakFailureCode.Conflict)]
    [InlineData("required digest", KeycloakFailureCode.Invalid)]
    [InlineData("required version null", KeycloakFailureCode.Invalid)]
    [InlineData("required version long", KeycloakFailureCode.Invalid)]
    [InlineData("removed version null", KeycloakFailureCode.Invalid)]
    [InlineData("removed version initial", KeycloakFailureCode.Invalid)]
    [InlineData("missing package material", KeycloakFailureCode.Conflict)]
    [InlineData("missing keyring material", KeycloakFailureCode.Conflict)]
    public void RuntimeMetadataMutationsPreserveTheThreeAndTwoPackageClosure(string mutation, KeycloakFailureCode code)
    {
        var recipe = TestRecipes.Recipe();
        var operation = (KeycloakOperation.RuntimeProfileOperation)recipe.Operations[1];
        var profile = operation.Profile;
        var snapshot = profile.RepositorySnapshots[0];
        var changed = mutation switch
        {
            "snapshot digest" => profile with { RepositorySnapshots = [snapshot with { Sha256 = "bad" }] },
            "keyring id" => profile with { RepositorySnapshots = [snapshot with { KeyringMaterialId = string.Empty }] },
            "map null" => profile with { RepositorySnapshots = [snapshot with { PackageIndexMaterialIds = null! }] },
            "map key" => profile with { RepositorySnapshots = [snapshot with { PackageIndexMaterialIds = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add(string.Empty, "package-index") }] },
            "map value" => profile with { RepositorySnapshots = [snapshot with { PackageIndexMaterialIds = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("main", string.Empty) }] },
            "duplicate snapshot" => profile with { RepositorySnapshots = [snapshot, snapshot] },
            "required digest" => profile with { RequiredPackages = profile.RequiredPackages.SetItem(0, profile.RequiredPackages[0] with { Sha256 = "bad" }) },
            "required version null" => profile with { RequiredPackages = profile.RequiredPackages.SetItem(0, profile.RequiredPackages[0] with { Version = null! }) },
            "required version long" => profile with { RequiredPackages = profile.RequiredPackages.SetItem(0, profile.RequiredPackages[0] with { Version = new string('1', 257) }) },
            "removed version null" => profile with { RemovedPackages = profile.RemovedPackages.SetItem(0, profile.RemovedPackages[0] with { ExpectedVersion = null! }) },
            "removed version initial" => profile with { RemovedPackages = profile.RemovedPackages.SetItem(0, profile.RemovedPackages[0] with { ExpectedVersion = "v7" }) },
            "missing package material" => profile with { RequiredPackages = profile.RequiredPackages.SetItem(0, profile.RequiredPackages[0] with { MaterialId = "undeclared" }) },
            "missing keyring material" => profile with { RepositorySnapshots = [snapshot with { KeyringMaterialId = "undeclared" }] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        Assert.Equal(["openssl", "openssl-provider-legacy", "libssl3t64"], changed.RequiredPackages.Select(x => x.Name).ToArray());
        Assert.Equal(["curl", "libcurl4t64"], changed.RemovedPackages.Select(x => x.Name).ToArray());
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe with { Operations = recipe.Operations.SetItem(1, operation with { Profile = changed }) }), code);
    }

    [Fact]
    public void Arm64RuntimeAndDebianVersionGrammarHaveAValidControl()
    {
        var recipe = TestRecipes.Recipe();
        var operation = (KeycloakOperation.RuntimeProfileOperation)recipe.Operations[1];
        var profile = operation.Profile with
        {
            RuntimeOciIdentities = [operation.Profile.RuntimeOciIdentities[0] with { Platform = "linux/arm64" }],
            RepositorySnapshots = [operation.Profile.RepositorySnapshots[0] with { Architectures = ["arm64"] }],
            RequiredPackages = operation.Profile.RequiredPackages.Select(x => x with { Architecture = "arm64", Version = "1:3.0+dfsg-1~test" }).ToImmutableArray(),
            RemovedPackages = operation.Profile.RemovedPackages.Select(x => x with { Architecture = "arm64", ExpectedVersion = "2:7.0-1~test" }).ToImmutableArray()
        };
        recipe = recipe with { SourceImage = recipe.SourceImage with { Platform = "linux/arm64" }, Operations = recipe.Operations.SetItem(1, operation with { Profile = profile }) };
        Assert.Same(recipe, KeycloakRecipeValidation.Validate(recipe));
        var parsed = KeycloakCanonicalJson.ReadRecipe(KeycloakCanonicalJson.Write(recipe));
        var runtime = (KeycloakOperation.RuntimeProfileOperation)parsed.Operations[1];
        Assert.All(runtime.Profile.RequiredPackages, x => Assert.Equal("arm64", x.Architecture));
        Assert.Equal("1:3.0+dfsg-1~test", runtime.Profile.RequiredPackages[0].Version);
        Assert.Equal("linux/arm64", parsed.SourceImage.Platform);
    }

    // protects: exact alignment includes classifier, extension, and both archive destinations.
    // fails_when: a version upgrade silently changes artifact kind or archive placement.
    // why_new: earlier mismatch controls changed group/artifact or the module destination only.
    // seam: public validation with the remaining valid operation families preserved.
    // no_claim: validation does not prove replacement artifact contents.
    [Theory]
    [InlineData("null coordinate", KeycloakFailureCode.Invalid)]
    [InlineData("classifier mismatch", KeycloakFailureCode.Conflict)]
    [InlineData("extension mismatch", KeycloakFailureCode.Conflict)]
    [InlineData("archive pair destination", KeycloakFailureCode.Conflict)]
    public void RemainingArtifactAlignmentAlternativesRejectWithStableCodes(string mutation, KeycloakFailureCode expected)
    {
        var recipe = TestRecipes.Recipe();
        var groupOperation = (KeycloakOperation.ArtifactReplacementGroupOperation)recipe.Operations[0];
        var pair = groupOperation.Group.Replacements[0];
        if (mutation == "archive pair destination")
        {
            var operation = (KeycloakOperation.EmbeddedArchiveReplacementOperation)recipe.Operations[2];
            var modules = operation.Replacement.ModuleSet!;
            var changed = operation with
            {
                Replacement = operation.Replacement with
                {
                    ModuleSet = modules with { ReplacementArtifact = modules.ReplacementArtifact with { Destination = "/opt/keycloak/lib/other-cli.jar" } }
                }
            };
            recipe = recipe with { Operations = recipe.Operations.SetItem(2, changed) };
        }
        else
        {
            var coordinate = mutation switch
            {
                "null coordinate" => null,
                "classifier mismatch" => pair.Original.Coordinate with { Classifier = "tests" },
                "extension mismatch" => pair.Original.Coordinate with { Extension = "zip" },
                _ => throw new ArgumentOutOfRangeException(nameof(mutation))
            };
            var changed = groupOperation with { Group = groupOperation.Group with { Replacements = [pair with { Original = pair.Original with { Coordinate = coordinate! } }] } };
            recipe = recipe with { Operations = recipe.Operations.SetItem(0, changed) };
        }
        TestRecipes.ExpectFailure(() => KeycloakRecipeValidation.Validate(recipe), expected);
    }


    // protects: empty package versions cannot satisfy the exact signed runtime closure.
    // fails_when: an empty required or removed version is accepted despite complete package names.
    // why_new: covers the zero-length version boundary independently of null/oversize versions.
    // seam: public validation with complete three-required/two-removed arrays preserved.
    // no_claim: these tuples alone are not proof of signed package contents.
    [Theory]
    [InlineData("required")]
    [InlineData("removed")]
    public void EmptyRuntimeVersionsRejectWithoutTruncatingTheSupportedClosure(string family)
    {
        var recipe = TestRecipes.Recipe();
        var operation = (KeycloakOperation.RuntimeProfileOperation)recipe.Operations[1];
        var profile = operation.Profile;
        profile = family switch
        {
            "required" => profile with { RequiredPackages = profile.RequiredPackages.SetItem(0, profile.RequiredPackages[0] with { Version = string.Empty }) },
            "removed" => profile with { RemovedPackages = profile.RemovedPackages.SetItem(0, profile.RemovedPackages[0] with { ExpectedVersion = string.Empty }) },
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
        Assert.Equal(3, profile.RequiredPackages.Length);
        Assert.Equal(2, profile.RemovedPackages.Length);
        var changed = recipe with { Operations = recipe.Operations.SetItem(1, operation with { Profile = profile }) };
        var failure = Assert.Throws<KeycloakSecurityException>(() => KeycloakRecipeValidation.Validate(changed));
        Assert.Equal(KeycloakFailureCode.Invalid, failure.Code);
        Assert.Equal(KeycloakSafePhase.Validation, failure.Phase);
        Assert.Equal(profile.Id, failure.OperationId);
    }

}
