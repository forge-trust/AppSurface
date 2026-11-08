using System.Collections.Immutable;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

internal static class TestRecipes
{
    /// <summary>
    /// Shared input bundle for inspection and capture tests. The path map is deliberately
    /// test-owned: paths are staging locations and never participate in recipe identity.
    /// </summary>
    internal sealed record SyntheticFixture(
        KeycloakSecurityRecipe Recipe,
        KeycloakMaterialTrustPolicy Policy,
        KeycloakCaptureInputs Bindings,
        KeycloakImageIdentity SourceArchiveIdentity,
        ImmutableDictionary<string, string> MaterialPaths)
    {
        internal string SourceArchivePath => MaterialPaths[SourceArchiveIdentity.ArchiveMaterialId!];
    }

    /// <summary>
    /// Creates a canonical recipe/policy/binding bundle and deterministic paths for a
    /// caller-owned synthetic material directory. Callers may replace the files or use
    /// the paths to build an independent OCI fixture without changing the recipe.
    /// </summary>
    internal static SyntheticFixture CreateSyntheticFixture(string materialRoot, KeycloakSecurityRecipe? recipe = null, KeycloakMaterialTrustPolicy? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(materialRoot);
        var selectedRecipe = recipe ?? Recipe();
        var paths = selectedRecipe.MaterialReferences.ToImmutableDictionary(
            material => material.Id,
            material => Path.Combine(materialRoot, material.Id),
            StringComparer.Ordinal);
        var reviewedRecipeId = KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(selectedRecipe));
        return new(
            selectedRecipe,
            policy ?? Policy(selectedRecipe.Mode == KeycloakRecipeMode.Security),
            KeycloakCaptureInputs.Create(reviewedRecipeId, []),
            selectedRecipe.SourceImage,
            paths);
    }

    internal static KeycloakSecurityRecipe Recipe(KeycloakRecipeMode mode = KeycloakRecipeMode.Security, ImmutableArray<KeycloakOperation>? operations = null)
    {
        var digest = "sha256:" + new string('a', 64);
        var image = KeycloakImageIdentity.Create(digest, digest, "linux/amd64") with
        {
            IndexMaterialId = "index",
            ArchiveMaterialId = "archive-bytes",
            ConfigDigest = digest,
            DiffIds = [digest]
        };
        return KeycloakSecurityRecipe.Create(1, image, [Oci("frontend", "frontend-bytes")], operations ?? [ArtifactGroup(), Runtime(), Archive()], mode, [], []) with
        {
            KeycloakVersion = "26.7.2",
            EntrypointPath = "/opt/keycloak/bin/kc.sh",
            SourceProfiles = ["keycloak-26.7.2"],
            MaterialReferences = [
                Material("index"), Material("archive-bytes"), Material("frontend-bytes"), Material("frontend-index"),
                Material("runtime-bytes"), Material("runtime-index"), Material("keyring"), Material("package-index"), Material("openssl"), Material("openssl-provider-legacy"), Material("libssl3t64")
                , Material("theme-input"), Material("theme-baseline")
            ],
            ResourceLimits = new(1L << 30, 65536, 1L << 30, 1L << 30, 300)
        };
    }

    internal static KeycloakSecurityRecipe RecipeWithTemplateBaseline() => Recipe() with
    {
        ThemeInputs = [new KeycloakThemeInput("login-template", new string('d', 64), null, null) { LocalInputId = "theme-input", DestinationPath = "/opt/keycloak/themes/keycloak.v2/login/login.ftl" }],
        TemplateBaseline = new KeycloakTemplateBaseline("/opt/keycloak/keycloak.jar", "a89cbd82" + new string('a', 56), "theme/keycloak.v2/login/login.ftl", "c9ec5c28" + new string('c', 56), "theme-baseline")
    };

    internal static KeycloakMaterialTrustPolicy Policy(bool securityKeys = true) =>
        KeycloakMaterialTrustPolicy.Create("v1", "owner-887", ["https://snapshot.example.test/source/"], ["snapshot.example.test/source/"], ["com.example"], [new KeyValuePair<string, string>("index", new string('a', 64))], securityKeys ? ["fingerprint"] : [], securityKeys ? ["OpenPGP-SHA256"] : [], [], []);

    internal static KeycloakOperation ArtifactGroup(string id = "artifacts", string destination = "/opt/keycloak/lib/server.jar", string replacementVersion = "2")
    {
        var original = Artifact("server", "1", destination);
        var replacement = Artifact("server", replacementVersion, destination);
        return new KeycloakOperation.ArtifactReplacementGroupOperation(ArtifactReplacementGroup.Create(id, [new ArtifactReplacement(original, replacement)], [replacement.Coordinate]));
    }

    internal static KeycloakOperation Runtime(string id = "runtime") => new KeycloakOperation.RuntimeProfileOperation(
        RuntimeProfile.Create(id, "1", "temurin-ubuntu", ["keycloak-26.7.2"]) with
        {
            RuntimeOciIdentities = [Oci("runtime-profile-oci", "runtime-bytes") with { IndexMaterialId = "runtime-index" }],
            RepositorySnapshots = [new SignedRepositorySnapshot("ubuntu", "https://snapshot.example.test/source/", new string('b', 64), "fingerprint", "jammy", ["main"], ["amd64"]) { KeyringMaterialId = "keyring", PackageIndexMaterialIds = ImmutableDictionary<string, string>.Empty.Add("main", "package-index") }],
            RequiredPackages = [
                new RuntimePackageSpec("openssl", "3", new string('c', 64), "amd64") { MaterialId = "openssl" },
                new RuntimePackageSpec("openssl-provider-legacy", "3", new string('d', 64), "amd64") { MaterialId = "openssl-provider-legacy" },
                new RuntimePackageSpec("libssl3t64", "3", new string('e', 64), "amd64") { MaterialId = "libssl3t64" }
            ],
            RemovedPackages = [new KeycloakRemovedPackageSpec("curl", "amd64", "7"), new KeycloakRemovedPackageSpec("libcurl4t64", "amd64", "8")],
            ExpectedObservations = ["nonroot"]
        });

    internal static KeycloakOperation Archive(string id = "archive", string destination = "/opt/keycloak/lib/app.jar")
    {
        var source = new ArtifactCoordinate("com.example", "jackson-core", "1", null, "jar");
        var replacement = source with { Version = "2" };
        var sourceArtifact = new ArtifactReference(source, new string('e', 64), string.Empty, destination);
        var replacementArtifact = new ArtifactReference(replacement, new string('f', 64), string.Empty, destination);
        var moduleSet = new EmbeddedArchiveModuleSet(sourceArtifact, replacementArtifact, destination, ImmutableDictionary<string, string>.Empty.Add("source", new string('1', 64)), ImmutableDictionary<string, string>.Empty.Add("replacement", new string('2', 64)));
        return new KeycloakOperation.EmbeddedArchiveReplacementOperation(new EmbeddedArchiveReplacement(id, "1", "jackson", source, replacement, ["com/fasterxml/jackson/core/"], ["com/fasterxml/jackson/core/Main.class"], ["META-INF/services/example"]) { ModuleSet = moduleSet });
    }

    internal static OciIdentity Oci(string id, string archiveMaterial) => new OciIdentity("runtime", "sha256:" + new string('9', 64)) with { Id = id, Platform = "linux/amd64", ArchiveMaterialId = archiveMaterial, PlatformManifestDigest = "sha256:" + new string('8', 64), ConfigDigest = "sha256:" + new string('7', 64), IndexMaterialId = "frontend-index", DiffIds = ["sha256:" + new string('6', 64)] };
    internal static KeycloakMaterialReference Material(string id) => new(id, "https://snapshot.example.test/source/", new string('a', 64), 1024);
    internal static ArtifactReference Artifact(string artifact, string version, string destination) => new(new ArtifactCoordinate("com.example", artifact, version, null, "jar"), new string('a', 64), string.Empty, destination);
    internal static void ExpectFailure(Action action, KeycloakFailureCode? code = null)
    {
        var exception = Assert.Throws<KeycloakSecurityException>(action);
        if (code is not null) Assert.Equal(code, exception.Code);
    }
}
