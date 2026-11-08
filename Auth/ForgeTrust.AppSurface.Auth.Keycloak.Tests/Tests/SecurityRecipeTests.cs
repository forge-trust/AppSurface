using ForgeTrust.AppSurface.Auth.Keycloak;
using ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// Test card: one runtime-only security recipe through capture, offline materialization,
// and scripted immutable-image inspection. The OCI, signed index, and DEB bytes are real
// bounded fixture inputs; this does not claim Docker or Keycloak execution.
public sealed class SecurityRecipeTests
{
    [Fact]
    public async Task RuntimeRecipe_CapturesAndMaterializesRealSignedInputs()
    {
        using var fixture = SecurityRecipeFixture.Create();
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings,
            Path.Combine(fixture.Root, "snapshot"));
        var context = await KeycloakSecurity.MaterializeAsync(plan, Path.Combine(fixture.Root, "context"));

        Assert.Equal(plan.Digest, context.PlanDigest);
        Assert.True(File.Exists(Path.Combine(context.DirectoryPath, "inputs", "oci", "runtime", "index.json")));
        Assert.True(File.Exists(Path.Combine(context.DirectoryPath, "debs", "openssl_3.0.0_amd64.deb")));
        Assert.True(File.Exists(Path.Combine(context.DirectoryPath, "debs", "openssl-provider-legacy_3.0.0_amd64.deb")));
        Assert.True(File.Exists(Path.Combine(context.DirectoryPath, "debs", "libssl3t64_3.0.0_amd64.deb")));
        Assert.Equal("build", context.CreateDockerBuildArguments("fixture:security")[0]);
    }

    [Fact]
    public async Task RuntimeRecipe_InspectsSeparateFinalOciArchiveWithScriptedDocker29Adapter()
    {
        using var fixture = SecurityRecipeFixture.Create();
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings,
            Path.Combine(fixture.Root, "snapshot"));
        var evidence = await KeycloakSecurity.InspectCoreAsync(plan, fixture.FinalImage, default, Command(fixture));

        Assert.Equal(KeycloakRecipeMode.Security, evidence.Mode);
        Assert.Equal(fixture.FinalImage, evidence.Image);
        Assert.Contains(evidence.Profiles, x => x.OperationId == "runtime" && x.Family == "runtime:temurin-ubuntu" && x.Version == "1");
        Assert.Contains(evidence.Checks, x => x.Id == "removed:curl" && x.Observed == "absent");
        Assert.Contains(evidence.Checks, x => x.Id == "removed:libcurl4t64" && x.Observed == "absent");
    }

    [Fact]
    public async Task RuntimeRecipe_RejectsReleaseBindingHashBeforeSecurityAcceptance()
    {
        using var fixture = SecurityRecipeFixture.Create();
        var binding = fixture.Bindings.LocalInputs.Single(x => x.MaterialId == "release");
        var changed = fixture.Bindings with { LocalInputs = fixture.Bindings.LocalInputs.Replace(binding, binding with { Sha256 = new string('0', 64) }) };
        var error = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, changed, Path.Combine(fixture.Root, "bad-release")));
        Assert.Equal(KeycloakFailureCode.DigestMismatch, error.Code);
        Assert.Equal(KeycloakSafePhase.Capture, error.Phase);
    }

    [Fact]
    public async Task RuntimeRecipe_RejectsPackageIndexAndPackageHashBindingChanges()
    {
        using var fixture = SecurityRecipeFixture.Create();
        foreach (var materialId in new[] { "package-index", "openssl" })
        {
            var binding = fixture.Bindings.LocalInputs.Single(x => x.MaterialId == materialId);
            var changed = fixture.Bindings with { LocalInputs = fixture.Bindings.LocalInputs.Replace(binding, binding with { Sha256 = new string('0', 64) }) };
            var error = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, changed, Path.Combine(fixture.Root, "bad-" + materialId)));
            Assert.Equal(KeycloakFailureCode.DigestMismatch, error.Code);
            Assert.Equal(KeycloakSafePhase.Capture, error.Phase);
        }
    }

    [Fact]
    public async Task RuntimeRecipe_RejectsExpectedPackageVersionAbsentFromSignedIndex()
    {
        using var fixture = SecurityRecipeFixture.Create();
        var runtime = fixture.Recipe.Operations.OfType<KeycloakOperation.RuntimeProfileOperation>().Single();
        var changedProfile = runtime.Profile with { RequiredPackages = runtime.Profile.RequiredPackages.SetItem(0, runtime.Profile.RequiredPackages[0] with { Version = "3.0.1" }) };
        var changedRecipe = fixture.Recipe with { Operations = [new KeycloakOperation.RuntimeProfileOperation(changedProfile)] };
        var changedBindings = fixture.Bindings with { ReviewedRecipeId = KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(changedRecipe)) };
        var error = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(changedRecipe, fixture.Policy, changedBindings,
            Path.Combine(fixture.Root, "bad-version")));
        Assert.Equal(KeycloakFailureCode.DigestMismatch, error.Code);
        Assert.Equal(KeycloakSafePhase.Capture, error.Phase);
    }

    [Fact]
    public async Task CompleteRecipeCapturesAllFamiliesAndComparesIndependentRepairedBytes()
    {
        using var fixture = SecurityRecipeFixture.Create(complete: true);
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings, Path.Combine(fixture.Root, "snapshot"));
        var context = await KeycloakSecurity.MaterializeAsync(plan, Path.Combine(fixture.Root, "context"));
        Assert.Equal(fixture.Recipe.Operations.OfType<KeycloakOperation.EmbeddedArchiveReplacementOperation>().Single().Replacement.ModuleSet!.ReplacementArtifact.Sha256, context.RepairedCliSha256);
        Assert.Equal(await File.ReadAllBytesAsync(fixture.MaterialPaths["cli-repaired"]), await File.ReadAllBytesAsync(Path.Combine(context.DirectoryPath, "rootfs", "opt", "keycloak", "bin", "client", "keycloak-admin-cli-26.7.2.jar")));
        Assert.Equal(await File.ReadAllBytesAsync(fixture.MaterialPaths["maven-replacement"]), await File.ReadAllBytesAsync(Path.Combine(context.DirectoryPath, "rootfs", "opt", "keycloak", "lib", "server.jar")));
        Assert.Equal("#!/bin/sh\nexec /opt/keycloak/bin/kc.sh \"$@\"\n", await File.ReadAllTextAsync(Path.Combine(context.DirectoryPath, "rootfs", "opt", "keycloak", "bin", "skoolit-entrypoint.sh")));
        var actual = new OciReader().Read(fixture.FinalArchivePath, new(null, fixture.FinalImage.ManifestDigest, fixture.FinalImage.Platform, fixture.FinalImage.ConfigDigest, fixture.FinalImage.DiffIds));
        var actualFiles = actual.Entries.ToDictionary(x => x.Path);
        foreach (var path in Directory.GetFiles(Path.Combine(context.DirectoryPath, "rootfs"), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Path.Combine(context.DirectoryPath, "rootfs"), path).Replace('\\', '/');
            Assert.True(actualFiles.TryGetValue(relative, out var entry), "Missing " + relative);
            Assert.Equal("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), entry!.Sha256);
            if (!OperatingSystem.IsWindows()) Assert.Equal((int)File.GetUnixFileMode(path), entry.Mode);
        }
        var evidence = await KeycloakSecurity.InspectCoreAsync(plan, fixture.FinalImage, default, Command(fixture));
        Assert.Equal(3, evidence.Profiles.Length);
        Assert.NotNull(evidence.TemplateBaselineSha256);
        Assert.Contains(evidence.Checks, x => x.Id == "file:opt/keycloak/themes/keycloak.v2/login/login.ftl" && x.Expected == "sha256:" + fixture.Recipe.ThemeInputs[0].Sha256);
    }

    [Theory]
    [InlineData("original-artifact")]
    [InlineData("replacement-coordinate")]
    [InlineData("template-archive")]
    [InlineData("template-material")]
    [InlineData("template-member")]
    [InlineData("template-payload")]
    [InlineData("cli-original")]
    [InlineData("source-module")]
    [InlineData("embedded-version")]
    [InlineData("archive-coordinate")]
    [InlineData("withdrawn-signer")]
    [InlineData("signer-policy")]
    [InlineData("signing-algorithm")]
    [InlineData("release-origin")]
    [InlineData("signed-package-hash")]
    public async Task CompleteRecipeRejectsIndependentProvenanceDriftBeforePublishing(string mutation)
    {
        using var fixture = SecurityRecipeFixture.Create(complete: true);
        var recipe = fixture.Recipe; var policy = fixture.Policy;
        var group = (KeycloakOperation.ArtifactReplacementGroupOperation)recipe.Operations[0];
        var runtime = (KeycloakOperation.RuntimeProfileOperation)recipe.Operations[1];
        var archive = (KeycloakOperation.EmbeddedArchiveReplacementOperation)recipe.Operations[2];
        var modules = archive.Replacement.ModuleSet!;
        switch (mutation)
        {
            case "original-artifact": group = group with { Group = group.Group with { Replacements = [group.Group.Replacements[0] with { Original = group.Group.Replacements[0].Original with { Sha256 = new string('0', 64) } }] } }; break;
            case "replacement-coordinate": policy = policy with { PermittedCoordinateMappings = policy.PermittedCoordinateMappings.Remove("com.example:security-adapter:2.0.0::jar") }; break;
            case "template-archive": recipe = recipe with { TemplateBaseline = recipe.TemplateBaseline! with { ArchiveSha256 = new string('0', 64) } }; break;
            case "template-material": recipe = recipe with { TemplateBaseline = recipe.TemplateBaseline! with { MaterialId = "theme-input" } }; break;
            case "template-member": recipe = recipe with { TemplateBaseline = recipe.TemplateBaseline! with { Entry = "theme/missing.ftl" } }; break;
            case "template-payload": recipe = recipe with { TemplateBaseline = recipe.TemplateBaseline! with { Sha256 = recipe.ThemeInputs[0].Sha256, MaterialId = "theme-input" } }; break;
            case "cli-original": archive = archive with { Replacement = archive.Replacement with { ModuleSet = modules with { SourceArtifact = modules.SourceArtifact with { Sha256 = new string('0', 64) } } } }; break;
            case "source-module": archive = archive with { Replacement = archive.Replacement with { ModuleSet = modules with { SourceModuleSha256ByIdentity = modules.SourceModuleSha256ByIdentity.SetItem("com.fasterxml.jackson.core:jackson-core:2.21.5::jar", new string('0', 64)) } } }; break;
            case "embedded-version": archive = archive with { Replacement = archive.Replacement with { ModuleSet = modules with { SourceModuleSha256ByIdentity = modules.SourceModuleSha256ByIdentity.Remove("com.fasterxml.jackson.core:jackson-core:2.21.5::jar").Add("com.fasterxml.jackson.core:jackson-core:2.21.4::jar", modules.SourceModuleSha256ByIdentity["com.fasterxml.jackson.core:jackson-core:2.21.5::jar"]) } } }; break;
            case "archive-coordinate": policy = policy with { PermittedCoordinateMappings = policy.PermittedCoordinateMappings.Remove("com.fasterxml.jackson.core:jackson-core:2.21.7::jar") }; break;
            case "withdrawn-signer": policy = policy with { WithdrawnKeys = policy.SigningFingerprints }; break;
            case "signer-policy": policy = policy with { SigningFingerprints = [] }; break;
            case "signing-algorithm": policy = policy with { SigningAlgorithms = [] }; break;
            case "release-origin": runtime = runtime with { Profile = runtime.Profile with { RepositorySnapshots = [runtime.Profile.RepositorySnapshots[0] with { ExactHttpsUri = "https://snapshot.example.test/other/" }] } }; break;
            default: runtime = runtime with { Profile = runtime.Profile with { RequiredPackages = runtime.Profile.RequiredPackages.SetItem(0, runtime.Profile.RequiredPackages[0] with { Sha256 = new string('0', 64) }) } }; break;
        }
        recipe = recipe with { Operations = [group, runtime, archive] };
        var bindings = fixture.Bindings with { ReviewedRecipeId = KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(recipe)) };
        var destination = Path.Combine(fixture.Root, "rejected");
        var error = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(recipe, policy, bindings, destination));
        Assert.Equal(KeycloakFailureCode.DigestMismatch, error.Code);
        Assert.Equal(KeycloakSafePhase.Capture, error.Phase);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("missing-artifact")]
    [InlineData("missing-template")]
    [InlineData("missing-cli")]
    public async Task CaptureRejectsMissingSelectedSourceMembers(string mutation)
    {
        using var fixture = SecurityRecipeFixture.Create(complete: true);
        var recipe = fixture.Recipe;
        if (mutation == "missing-template") recipe = recipe with { TemplateBaseline = recipe.TemplateBaseline! with { ArchivePath = "/opt/keycloak/missing.jar" } };
        else if (mutation == "missing-artifact")
        {
            var operation = (KeycloakOperation.ArtifactReplacementGroupOperation)recipe.Operations[0];
            var pair = operation.Group.Replacements[0];
            operation = operation with { Group = operation.Group with { Replacements = [pair with { Original = pair.Original with { Destination = "/opt/keycloak/lib/missing.jar" }, Replacement = pair.Replacement with { Destination = "/opt/keycloak/lib/missing.jar" } }] } };
            recipe = recipe with { Operations = recipe.Operations.SetItem(0, operation) };
        }
        else
        {
            var operation = (KeycloakOperation.EmbeddedArchiveReplacementOperation)recipe.Operations[2];
            var module = operation.Replacement.ModuleSet!;
            const string absent = "/opt/keycloak/bin/client/missing.jar";
            operation = operation with { Replacement = operation.Replacement with { ModuleSet = module with { Destination = absent, SourceArtifact = module.SourceArtifact with { Destination = absent }, ReplacementArtifact = module.ReplacementArtifact with { Destination = absent } } } };
            recipe = recipe with { Operations = recipe.Operations.SetItem(2, operation) };
        }
        var bindings = fixture.Bindings with { ReviewedRecipeId = KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(recipe)) };
        var destination = Path.Combine(fixture.Root, "missing-member");
        Assert.Equal(KeycloakFailureCode.DigestMismatch, (await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(recipe, fixture.Policy, bindings, destination))).Code);
        Assert.False(Directory.Exists(destination));
    }

    private static DockerInspectionCommand Command(SecurityRecipeFixture fixture) => (args, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (args[0] == "image" && args[1] == "save") File.Copy(fixture.FinalArchivePath, args[3]);
        return Task.FromResult(args[0] == "version"
            ? DockerInspectionTests.Observation("29.7.2")
            : DockerInspectionTests.Reply(args, fixture.FinalImage, fixture.FinalImage.ManifestDigest));
    };
}
