using System.Collections.Immutable;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

// testcard protects: public capture rejects malformed declarations before touching host inputs.
// fails_when: null declarations or cancellation escape as raw implementation exceptions.
// why_new: capture is the public boundary before materialization and Docker.
// seam: public CaptureAsync with intentionally invalid inputs; no network or Docker is used.
// no_claim: this does not prove Docker execution or the public inspection wrapper.
public sealed class CaptureTests
{
    [Fact]
    public async Task TinyThemeOnlyImageCapturesMaterializesAndKeepsAcceptedBytesPrivate()
    {
        using var fixture = TinyImageFixture.Create();
        var destination = Path.Combine(fixture.Root, "captured");
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings, destination);

        File.WriteAllText(fixture.ArchivePath, "mutated-after-capture");
        var contextPath = Path.Combine(fixture.Root, "context");
        var context = await KeycloakSecurity.MaterializeAsync(plan, contextPath);
        Assert.Equal("#!/bin/sh\n", await File.ReadAllTextAsync(Path.Combine(context.DirectoryPath, "rootfs", "opt", "keycloak", "bin", "kc.sh")));
        Assert.Equal("tiny-theme\n", await File.ReadAllTextAsync(Path.Combine(context.DirectoryPath, "rootfs", "opt", "keycloak", "themes", "keycloak.v2", "login", "login.ftl")));
        Assert.Equal(["build", "--network=none", "--provenance=false"], context.CreateDockerBuildArguments("tiny:theme").Take(3));
    }

    [Fact]
    public async Task TinyImageRejectsChangedBindingDigestWithoutPublishingSnapshot()
    {
        using var fixture = TinyImageFixture.Create();
        var destination = Path.Combine(fixture.Root, "bad-capture");
        var changed = fixture.Bindings with
        {
            LocalInputs = fixture.Bindings.LocalInputs.SetItem(0, fixture.Bindings.LocalInputs[0] with { Sha256 = new string('0', 64) })
        };

        var exception = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, changed, destination));
        Assert.Equal(KeycloakSafePhase.Capture, exception.Phase);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task CaptureRejectsBindingCeilingBelowActualFileLengthWithoutPublishingSnapshot()
    {
        using var fixture = TinyImageFixture.Create();
        var destination = Path.Combine(fixture.Root, "small-binding");
        var binding = fixture.Bindings.LocalInputs[0] with { MaxBytes = 1 };
        var changed = fixture.Bindings with { LocalInputs = fixture.Bindings.LocalInputs.SetItem(0, binding) };

        var exception = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, changed, destination));
        Assert.Equal(KeycloakSafePhase.Capture, exception.Phase);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task CaptureRejectsNullRecipeAsTypedRequiredFailure()
    {
        var exception = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(
            null!, TestRecipes.Policy(), null!, Path.Combine(Path.GetTempPath(), "keycloak-capture-null")));

        Assert.Equal(KeycloakFailureCode.Required, exception.Code);
        Assert.Equal(KeycloakSafePhase.Validation, exception.Phase);
        Assert.Equal(KeycloakRetryMetadata.None, exception.RetryMetadata);
    }

    [Fact]
    public async Task CaptureRejectsNullPolicyBeforeAcquisition()
    {
        var recipe = TestRecipes.Recipe();
        var exception = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(
            recipe, null!, null!, Path.Combine(Path.GetTempPath(), "keycloak-capture-policy")));

        Assert.Equal(KeycloakFailureCode.Required, exception.Code);
        Assert.Equal(KeycloakSafePhase.Validation, exception.Phase);
    }

    [Fact]
    public async Task CaptureCancellationIsTypedAndDoesNotPublishDestination()
    {
        using var fixture = TinyImageFixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var destination = Path.Combine(fixture.Root, "cancelled");

        var exception = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(
            fixture.Recipe, fixture.Policy, fixture.Bindings, destination, cancellation.Token));

        Assert.Equal(KeycloakFailureCode.Cancelled, exception.Code);
        Assert.Equal(KeycloakSafePhase.Capture, exception.Phase);
        Assert.False(Directory.Exists(destination));
    }

    // protects: returned identities describe the accepted bytes and remain readable after disposal.
    // fails_when: a handle swaps policy/platform identity or disposal deletes caller-owned evidence.
    // why_new: capture success alone did not assert every exposed captured-plan identity.
    // seam: public capture plus the intentionally visible accepted-policy record; no Docker/network.
    // no_claim: identity metadata does not establish runtime conformance.
    [Fact]
    public async Task CapturedPlanExposesAcceptedIdentitiesWithoutOwningSnapshotLifetime()
    {
        using var fixture = TinyImageFixture.Create();
        var destination = Path.Combine(fixture.Root, "identity-snapshot");
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings, destination);
        var manifest = await File.ReadAllBytesAsync(Path.Combine(destination, "snapshot.json"));
        var snapshotDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(manifest));
        Assert.Equal(snapshotDigest, plan.Digest);
        Assert.Equal(KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(fixture.Recipe)), plan.RecipeDigest);
        Assert.Equal(KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(fixture.Policy)), plan.PolicyDigest);
        Assert.Equal(KeycloakCanonicalJson.Write(fixture.Policy), KeycloakCanonicalJson.Write(plan.Policy));
        Assert.Equal("linux/amd64", plan.Platform);
        Assert.Equal(KeycloakRecipeMode.ThemeOnly, plan.Mode);
        plan.Dispose();
        plan.Dispose();
        Assert.Equal(snapshotDigest, plan.Digest);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(Path.Combine(destination, "snapshot.json")));
    }

    [Theory]
    [InlineData("missing host binding")]
    [InlineData("recipe material ceiling")]
    [InlineData("source config identity")]
    public async Task CaptureRejectsIndependentAcquisitionAndGraphBoundariesAtomically(string mutation)
    {
        using var fixture = TinyImageFixture.Create();
        var recipe = fixture.Recipe;
        var bindings = fixture.Bindings;
        switch (mutation)
        {
            case "missing host binding": bindings = bindings with { LocalInputs = bindings.LocalInputs.RemoveAt(0) }; break;
            case "recipe material ceiling": recipe = recipe with { ResourceLimits = recipe.ResourceLimits with { MaxMaterialBytes = 1 } }; break;
            case "source config identity": recipe = recipe with { SourceImage = recipe.SourceImage with { ConfigDigest = "sha256:" + new string('0', 64) } }; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        bindings = bindings with { ReviewedRecipeId = KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(recipe)) };
        var destination = Path.Combine(fixture.Root, "rejected-boundary");
        var failure = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(recipe, fixture.Policy, bindings, destination));
        Assert.Equal(KeycloakFailureCode.DigestMismatch, failure.Code);
        Assert.Equal(KeycloakSafePhase.Capture, failure.Phase);
        Assert.Equal(KeycloakEffectState.None, failure.Effect);
        Assert.False(Path.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, ".capture-inputs-*"));
    }

    [Fact]
    public async Task CaptureRejects257IndividuallyValidHostMaterialsBeforePublishing()
    {
        using var fixture = TinyImageFixture.Create();
        var original = fixture.Recipe.MaterialReferences[0];
        var extras = Enumerable.Range(0, 257 - fixture.Recipe.MaterialReferences.Length)
            .Select(index => original with { Id = "extra-" + index }).ToImmutableArray();
        var recipe = fixture.Recipe with
        {
            MaterialReferences = fixture.Recipe.MaterialReferences.AddRange(extras),
            CapturedHostInputs = fixture.Recipe.CapturedHostInputs.AddRange(extras.Select(x => x.Id))
        };
        var policy = fixture.Policy with { MaterialSha256ByIdentity = fixture.Policy.MaterialSha256ByIdentity.AddRange(extras.Select(x => new KeyValuePair<string, string>(x.Id, x.Sha256))) };
        var originalBinding = fixture.Bindings.LocalInputs.Single(x => x.MaterialId == original.Id);
        var bindings = fixture.Bindings with
        {
            ReviewedRecipeId = KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(recipe)),
            LocalInputs = fixture.Bindings.LocalInputs.AddRange(extras.Select(x => originalBinding with { MaterialId = x.Id }))
        };
        Assert.Equal(257, recipe.MaterialReferences.Length);
        var destination = Path.Combine(fixture.Root, "too-many-materials");
        var failure = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(recipe, policy, bindings, destination));
        Assert.Equal(KeycloakFailureCode.DigestMismatch, failure.Code);
        Assert.Equal(KeycloakSafePhase.Capture, failure.Phase);
        Assert.False(Path.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, ".capture-inputs-*"));
    }


    // protects: a cached downloadable input still requires an exact approved path namespace.
    // fails_when: a sibling prefix becomes authority or an exact approved resource is rejected.
    // why_new: v44 leaves the namespace predicate's rejecting alternative unverified.
    // seam: public capture with real tiny OCI inputs and explicit local bindings; no transport.
    // no_claim: this is cached-material authorization, not remote download availability.
    [Theory]
    [InlineData("https://materials.example.test/approved", true)]
    [InlineData("https://materials.example.test/approved-sibling/input", false)]
    public async Task CachedMaterialRequiresAnExactApprovedNamespaceBoundary(string materialUri, bool approved)
    {
        using var fixture = TinyImageFixture.Create();
        var material = fixture.Recipe.MaterialReferences[0] with { CapturedHostInput = false, ExactHttpsUri = materialUri };
        var recipe = fixture.Recipe with { MaterialReferences = fixture.Recipe.MaterialReferences.SetItem(0, material) };
        var policy = fixture.Policy with
        {
            PermittedHttpsOrigins = ["https://materials.example.test/"],
            PermittedPathMappings = ["https://materials.example.test/approved"]
        };
        var bindings = fixture.Bindings with { ReviewedRecipeId = KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(recipe)) };
        Assert.Contains(bindings.LocalInputs, binding => binding.MaterialId == material.Id);
        var destination = Path.Combine(fixture.Root, "namespace-snapshot");
        if (approved)
        {
            using var plan = await KeycloakSecurity.CaptureAsync(recipe, policy, bindings, destination);
            Assert.Equal(bindings.ReviewedRecipeId, plan.RecipeDigest);
            Assert.Equal(KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(policy)), plan.PolicyDigest);
            Assert.Equal(await File.ReadAllBytesAsync(fixture.MaterialPaths[material.Id]), await File.ReadAllBytesAsync(Path.Combine(destination, material.Id)));
        }
        else
        {
            var failure = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.CaptureAsync(recipe, policy, bindings, destination));
            Assert.Equal(KeycloakFailureCode.DigestMismatch, failure.Code);
            Assert.Equal(KeycloakSafePhase.Capture, failure.Phase);
            Assert.Equal(KeycloakEffectState.None, failure.Effect);
            Assert.False(Path.Exists(destination));
        }
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, ".capture-inputs-*"));
    }

}
