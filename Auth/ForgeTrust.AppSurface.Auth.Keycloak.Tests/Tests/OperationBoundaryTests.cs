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
}
