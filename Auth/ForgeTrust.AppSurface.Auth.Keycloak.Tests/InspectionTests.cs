using ForgeTrust.AppSurface.Auth.Keycloak;
using ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// protects: complete captured theme inventory is compared to actual exported bytes before evidence.
// fails_when: a changed identity, file, ownership or extra node can mint passing evidence.
// no_claim: this independently generated image and scripted transport do not prove Docker runtime behavior.
public sealed class InspectionTests
{
    [Fact]
    public async Task PublicInspect_invalidImage_returnsTypedFailureAndRejectedCorrelation()
    {
        using var fixture = TinyImageFixture.Create();
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings, Path.Combine(fixture.Root, "public-snapshot"));
        var events = new List<KeycloakStageEvent>();
        using var subscription = KeycloakStageEvents.Subscribe(events.Add);

        var failure = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.InspectAsync(plan, fixture.InspectionImage with { ConfigDigest = "SHA256:" + new string('A', 64) }));

        Assert.Equal(KeycloakFailureCode.InspectionFailed, failure.Code);
        var rejected = events.Last(x => x.Phase == KeycloakStagePhase.Inspection && x.Outcome == KeycloakStageOutcome.Rejected);
        Assert.Equal(failure.CorrelationId.Value, rejected.CorrelationId);
    }

    [Fact]
    public async Task Captured_theme_plan_inspects_exact_export_and_writes_bound_evidence()
    {
        using var fixture = TinyImageFixture.Create();
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings, Path.Combine(fixture.Root, "snapshot"));
        var evidence = await KeycloakSecurity.InspectCoreAsync(plan, fixture.InspectionImage, default, Command(fixture));
        Assert.Equal(plan.Digest, evidence.PlanDigest); Assert.Equal(fixture.InspectionImage, evidence.Image); Assert.Equal(KeycloakRecipeMode.ThemeOnly, evidence.Mode);
        Assert.Equal(plan.RecipeDigest, evidence.RecipeDigest); Assert.Equal(plan.PolicyDigest, evidence.PolicyDigest);
        Assert.Contains(evidence.InputImages, x => x.Role == "source" && x.ManifestDigest == fixture.Image.PlatformManifest);
        Assert.Contains(evidence.InputImages, x => x.Role == "frontend" && x.ManifestDigest == fixture.Image.PlatformManifest);
        Assert.Empty(evidence.Profiles); Assert.Null(evidence.TemplateBaselineSha256);
        Assert.Equal("docker-29-oci-v1", evidence.InspectorVersion); Assert.Equal("managed-zip-v1", evidence.TransformerVersion);
        Assert.Equal("THEME-ONLY VERIFIED; security customization not evaluated", evidence.Outcome);
        Assert.Contains(evidence.Checks, x => x.Id == "file:opt/keycloak/bin/kc.sh");
        Assert.Contains(evidence.Checks, x => x.Id == "scope:theme-only" && x.Observed == "security customization not evaluated");
        Assert.Equal(Path.Combine(fixture.Root, "evidence.json"), await evidence.WriteAsync(Path.Combine(fixture.Root, "evidence.json")));
        var resolved = await DockerInspection.ResolveLocalAsync("local:fixture", default, Command(fixture));
        Assert.Equal(fixture.InspectionImage.ManifestDigest, resolved.ManifestDigest);
        Assert.Equal(fixture.InspectionImage.ConfigDigest, resolved.ConfigDigest);
        Assert.Equal(fixture.InspectionImage.Platform, resolved.Platform);
        Assert.Equal(fixture.InspectionImage.DiffIds.ToArray(), resolved.DiffIds.ToArray());
    }

    [Fact]
    public async Task ThemeOnlyInspectionPreservesNonemptyOutsideFilesAndLinksExactly()
    {
        using var fixture = TinyImageFixture.Create(includeOutside: true);
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings, Path.Combine(fixture.Root, "outside-snapshot"));
        var evidence = await KeycloakSecurity.InspectCoreAsync(plan, fixture.InspectionImage, default, Command(fixture));
        Assert.Equal(KeycloakRecipeMode.ThemeOnly, evidence.Mode);
        Assert.Contains(evidence.Checks, x => x.Id == "scope:theme-only");
    }

    [Fact]
    public async Task Unsupported_version_changed_image_and_wrong_config_fail_before_evidence()
    {
        using var fixture = TinyImageFixture.Create();
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings, Path.Combine(fixture.Root, "snapshot"));
        var wrong = fixture.InspectionImage with { ConfigDigest = "sha256:" + new string('0', 64) };
        var mismatch = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.InspectCoreAsync(plan, wrong, default, Command(fixture)));
        Assert.Equal(KeycloakFailureCode.InspectionFailed, mismatch.Code);
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerInspection.ExportAsync(fixture.InspectionImage, Path.Combine(fixture.Root, "export.tar"), new HashSet<string>(), default, Command(fixture, "28.0")));
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerInspection.ResolveLocalAsync("local:fixture", default, Command(fixture, "28.0")));
        plan.Dispose();
        var disposed = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.InspectAsync(plan, fixture.InspectionImage));
        Assert.Equal(KeycloakFailureCode.CaptureInvalid, disposed.Code);
        var missing = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.InspectAsync(null!, fixture.InspectionImage));
        Assert.Equal(KeycloakFailureCode.Invalid, missing.Code);
    }

    [Fact]
    public async Task Wrong_platform_cancellation_and_capture_tamper_never_reach_Docker()
    {
        using var fixture = TinyImageFixture.Create();
        var snapshot = Path.Combine(fixture.Root, "snapshot");
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings, snapshot);
        var called = false;
        Task<ProcessObservation> Unexpected(IReadOnlyList<string> args, CancellationToken token) { called = true; throw new InvalidOperationException(); }
        await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.InspectCoreAsync(plan, fixture.InspectionImage with { Platform = "linux/arm64" }, default, Unexpected));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.InspectCoreAsync(plan, fixture.InspectionImage, cancellation.Token, Unexpected));
        Assert.Equal(KeycloakFailureCode.Cancelled, cancelled.Code);
        var capturedArchive = Path.Combine(snapshot, fixture.Image.ArchiveMaterialId!);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(capturedArchive, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(capturedArchive, "changed");
        await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.InspectCoreAsync(plan, fixture.InspectionImage, default, Unexpected));
        Assert.False(called);
    }

    [Theory]
    [InlineData(InspectionImageMutation.ExtraOutsideApplicationTree)]
    [InlineData(InspectionImageMutation.ExtraInsideApplicationTree)]
    [InlineData(InspectionImageMutation.ChangedKeycloakFile)]
    [InlineData(InspectionImageMutation.ChangedDirectoryMode)]
    [InlineData(InspectionImageMutation.ChangedDirectoryUid)]
    [InlineData(InspectionImageMutation.ChangedDirectoryGid)]
    [InlineData(InspectionImageMutation.MissingDirectory)]
    [InlineData(InspectionImageMutation.ExtraLink)]
    [InlineData(InspectionImageMutation.ChangedConfigUser)]
    [InlineData(InspectionImageMutation.ChangedConfigEntrypoint)]
    [InlineData(InspectionImageMutation.ChangedConfigEnvironment)]
    [InlineData(InspectionImageMutation.ChangedOutsideFileHash)]
    [InlineData(InspectionImageMutation.ChangedOutsideFileMode)]
    [InlineData(InspectionImageMutation.ChangedOutsideFileUid)]
    [InlineData(InspectionImageMutation.ChangedOutsideFileGid)]
    [InlineData(InspectionImageMutation.ChangedOutsideFileType)]
    [InlineData(InspectionImageMutation.ChangedOutsideLinkTarget)]
    public async Task Mutated_oci_graphs_fail_real_inventory_comparison_before_evidence(InspectionImageMutation mutation)
    {
        using var fixture = TinyImageFixture.Create(includeOutside: true);
        using var image = InspectionImageMutationArchive.Create(fixture, mutation);
        using var plan = await KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings, Path.Combine(fixture.Root, "snapshot"));

        var observed = new OciReader().Read(image.ArchivePath,
            new OciExpectedImage(null, image.Image.ManifestDigest, image.Image.Platform, image.Image.ConfigDigest!, image.Image.DiffIds), default);
        Assert.Equal(image.Image.ManifestDigest, observed.ManifestDigest);
        Assert.Equal(image.Image.ConfigDigest, observed.ConfigDigest);
        Assert.Equal(image.Image.DiffIds.ToArray(), observed.DiffIds.ToArray());

        var error = await Assert.ThrowsAsync<KeycloakSecurityException>(() =>
            KeycloakSecurity.InspectCoreAsync(plan, image.Image, default, Command(fixture, image)));

        Assert.Equal(KeycloakFailureCode.InspectionFailed, error.Code);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "evidence.json")));
    }

    private static DockerInspectionCommand Command(TinyImageFixture fixture, string version = "29.7.2") => (args, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (args[0] == "image" && args[1] == "save") File.Copy(fixture.ArchivePath, args[3]);
        return Task.FromResult(args[0] == "version" ? DockerInspectionTests.Observation(version)
            : DockerInspectionTests.Reply(args, fixture.InspectionImage, fixture.InspectionImage.ManifestDigest));
    };

    private static DockerInspectionCommand Command(TinyImageFixture fixture, InspectionImageMutationArchive image) => (args, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (args[0] == "image" && args[1] == "save") File.Copy(image.ArchivePath, args[3]);
        return Task.FromResult(args[0] == "version" ? DockerInspectionTests.Observation("29.7.2")
            : DockerInspectionTests.Reply(args, image.Image, image.Image.ManifestDigest));
    };
}
