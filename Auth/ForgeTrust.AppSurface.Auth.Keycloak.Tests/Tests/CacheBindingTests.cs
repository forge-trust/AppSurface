using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

// protects: cached non-host material remains subject to the same exact URI authorization as a download.
// fails_when: a local cache path could bypass the approved origin/path checks before signed-byte verification.
// no_claim: this exercises capture provenance ordering and does not execute Docker.
public sealed class CacheBindingTests
{
    [Fact]
    public async Task Valid_non_host_cache_capture_succeeds_with_approved_origin()
    {
        using var fixture = SecurityRecipeFixture.Create();
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings,
            Path.Combine(fixture.Root, "valid-cache"));

        Assert.Equal(KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(fixture.Recipe)), plan.RecipeDigest);
        Assert.Equal(fixture.Recipe.SourceImage.Platform, plan.Platform);
    }

    [Fact]
    public async Task Non_host_cache_with_wrong_origin_fails_before_oci_verification()
    {
        using var fixture = SecurityRecipeFixture.Create();
        var remote = fixture.Recipe.MaterialReferences.First(x => !x.CapturedHostInput);
        var wrongOrigin = remote with { ExactHttpsUri = "https://unapproved.example.test/path/" };
        var recipe = fixture.Recipe with
        {
            MaterialReferences = fixture.Recipe.MaterialReferences.Replace(remote, wrongOrigin)
        };
        var bindings = fixture.Bindings with
        {
            ReviewedRecipeId = KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(recipe))
        };
        var destination = Path.Combine(fixture.Root, "wrong-origin-cache");

        var error = await Assert.ThrowsAsync<KeycloakSecurityException>(() =>
            KeycloakSecurity.CaptureAsync(recipe, fixture.Policy, bindings, destination));

        Assert.Equal(KeycloakSafePhase.Capture, error.Phase);
        Assert.Equal(KeycloakFailureCode.DigestMismatch, error.Code);
        Assert.False(Directory.Exists(destination));
    }
}
