using ForgeTrust.AppSurface.Auth.Keycloak;
using System.Collections.Immutable;

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
}
