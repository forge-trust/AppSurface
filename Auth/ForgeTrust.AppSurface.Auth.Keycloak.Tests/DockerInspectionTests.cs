using System.Collections.Immutable;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;
using ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// protects: immutable Docker handoff, bounded read-only probes and cleanup.
// fails_when: a changed identity, nonzero probe or unsupported mapping yields passing evidence.
// no_claim: scripted transport verifies protocol behavior; retained real-image proofs verify Docker.
public sealed class DockerInspectionTests
{
    private static KeycloakInspectionImage Image => new("sha256:" + new string('a', 64), "sha256:" + new string('b', 64), "linux/amd64", ["sha256:" + new string('c', 64)]);

    [Theory]
    [InlineData("manifest")]
    [InlineData("config")]
    public async Task Probe_uses_exact_readonly_container_and_always_cleans_up(string mapping)
    {
        var image = Image; var calls = new List<string[]>();
        Task<ProcessObservation> Run(IReadOnlyList<string> args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); calls.Add(args.ToArray());
            return Task.FromResult(Reply(args, image, mapping == "config" ? image.ConfigDigest : image.ManifestDigest));
        }
        var result = await DockerInspection.ProbeAsync(image, "26.7.2", default, Run);
        Assert.True(result.Passed); Assert.Equal(image.ManifestDigest, result.Image); Assert.Equal(image.ConfigDigest, result.Config);
        var create = Assert.Single(calls, x => x[0] == "create");
        Assert.Contains("--read-only", create); Assert.Contains("--cap-drop", create); Assert.Contains("no-new-privileges", create);
        Assert.Equal("none", create[Array.IndexOf(create, "--network") + 1]);
        Assert.Contains(image.ManifestDigest, create); Assert.DoesNotContain(create, x => x is "--volume" or "-v" or "--privileged");
        Assert.Equal("rm", calls[^1][0]); Assert.Equal("--force", calls[^1][1]);
    }

    [Theory]
    [InlineData("identity-before")]
    [InlineData("identity-after")]
    [InlineData("container-image")]
    [InlineData("exit-code")]
    [InlineData("wrong-version")]
    [InlineData("start-failure")]
    public async Task Probe_rejects_mismatches_and_does_not_leak_owned_container(string failure)
    {
        var image = Image; var calls = new List<string[]>(); var identities = 0;
        Task<ProcessObservation> Run(IReadOnlyList<string> args, CancellationToken token)
        {
            calls.Add(args.ToArray());
            if (args[0] == "image" && args[1] == "inspect")
            {
                identities++;
                if ((failure == "identity-before" && identities == 1) || (failure == "identity-after" && identities == 2))
                    return Task.FromResult(Reply(args, image with { Platform = "linux/arm64" }, image.ManifestDigest));
            }
            if (args[0] == "inspect" && args[2] == "{{.Image}}" && failure == "container-image") return Task.FromResult(Observation("different"));
            if (args[0] == "inspect" && args[2] == "{{.State.ExitCode}}" && failure == "exit-code") return Task.FromResult(Observation("1"));
            if (args[0] == "start" && failure == "wrong-version") return Task.FromResult(Observation("Keycloak 26.6.0"));
            if (args[0] == "start" && failure == "start-failure") throw new IOException("private process text");
            return Task.FromResult(Reply(args, image, image.ManifestDigest));
        }
        if (failure == "start-failure") await Assert.ThrowsAsync<IOException>(() => DockerInspection.ProbeAsync(image, "26.7.2", default, Run));
        else await Assert.ThrowsAsync<InvalidDataException>(() => DockerInspection.ProbeAsync(image, "26.7.2", default, Run));
        Assert.Equal(failure != "identity-before", calls.Any(x => x[0] == "rm"));
    }

    [Fact]
    public async Task Cleanup_failure_preserves_primary_probe_failure()
    {
        var image = Image;
        Task<ProcessObservation> Run(IReadOnlyList<string> args, CancellationToken token)
        {
            if (args[0] == "rm") throw new IOException("cleanup");
            if (args[0] == "start") throw new InvalidDataException("primary");
            return Task.FromResult(Reply(args, image, image.ManifestDigest));
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DockerInspection.ProbeAsync(image, "26.7.2", default, Run));
        Assert.Equal("primary", error.Message);
        var mapped = KeycloakDiagnostics.Map(error, KeycloakSafePhase.Inspection, "runtime:probe", KeycloakEffectState.None);
        Assert.Equal(KeycloakFailureCode.InspectionFailed, mapped.Code);
        Assert.Equal(KeycloakEffectState.Unknown, mapped.Effect);
        Assert.Equal(KeycloakRecoveryClass.InspectBeforeRetry, mapped.Recovery);
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerInspection.ProbeAsync(image, "26.6", default, Run));
    }

    // protects: a lost create acknowledgement still removes the exact generated container name.
    // fails_when: cancellation, timeout or I/O failure bypasses cleanup or reuses the cancelled token.
    // seam: the existing internal DockerInspectionCommand; no real Docker or private reflection.
    [Theory]
    [InlineData("io")]
    [InlineData("cancelled")]
    [InlineData("deadline")]
    public async Task Unacknowledged_create_still_removes_owned_name_and_preserves_primary(string shape)
    {
        using var cancellation = new CancellationTokenSource();
        Exception primary = shape switch
        {
            "io" => new IOException("private create reply"),
            "cancelled" => new OperationCanceledException(cancellation.Token),
            _ => new BoundedProcessException(ProcessStopReason.Deadline)
        };
        string? attempted = null;
        string? removed = null;
        Task<ProcessObservation> Run(IReadOnlyList<string> args, CancellationToken token)
        {
            if (args[0] == "create")
            {
                attempted = args[2];
                cancellation.Cancel();
                throw primary;
            }
            if (args[0] == "rm")
            {
                Assert.True(token.CanBeCanceled);
                Assert.False(token.IsCancellationRequested);
                Assert.NotEqual(cancellation.Token, token);
                removed = args[2];
            }
            return Task.FromResult(Reply(args, Image, Image.ManifestDigest));
        }

        var failure = await Record.ExceptionAsync(() => DockerInspection.ProbeAsync(Image, "26.7.2", cancellation.Token, Run));

        Assert.Same(primary, failure);
        Assert.StartsWith("appsurface-keycloak-probe-", attempted, StringComparison.Ordinal);
        Assert.Equal(attempted, removed);
        var mapped = KeycloakDiagnostics.Map(primary, KeycloakSafePhase.Inspection, "runtime:probe", KeycloakEffectState.None);
        Assert.Equal(KeycloakEffectState.None, mapped.Effect);
    }

    // protects: failed cleanup cannot mint a passing probe observation or authorize blind retry.
    [Theory]
    [InlineData("io")]
    [InlineData("denied")]
    [InlineData("cancelled")]
    [InlineData("deadline")]
    [InlineData("nonzero")]
    public async Task Passing_probe_with_unconfirmed_cleanup_is_rejected_with_unknown_effect(string shape)
    {
        var removals = 0;
        Task<ProcessObservation> Run(IReadOnlyList<string> args, CancellationToken token)
        {
            if (args[0] != "rm") return Task.FromResult(Reply(args, Image, Image.ManifestDigest));
            removals++;
            if (shape == "nonzero") return Task.FromResult(new ProcessObservation(1, "private output", "private error", TimeSpan.Zero));
            throw shape switch
            {
                "io" => new IOException("private cleanup"),
                "denied" => new UnauthorizedAccessException("private cleanup"),
                "cancelled" => new OperationCanceledException(token),
                _ => new BoundedProcessException(ProcessStopReason.Deadline)
            };
        }

        var failure = await Assert.ThrowsAsync<KeycloakSecurityException>(() => DockerInspection.ProbeAsync(Image, "26.7.2", default, Run));

        Assert.Equal(1, removals);
        Assert.Equal(KeycloakFailureCode.ProbeFailed, failure.Code);
        Assert.Equal(KeycloakSafePhase.Inspection, failure.Phase);
        Assert.Equal(KeycloakEffectState.Unknown, failure.Effect);
        Assert.Equal(KeycloakRecoveryClass.InspectBeforeRetry, failure.Recovery);
        Assert.True(failure.RetryMetadata.RequiresInspectionFirst);
        Assert.DoesNotContain("private", failure.ToString(), StringComparison.Ordinal);
    }

    // protects: an uncertain create plus failed removal retains the original failure and safe recovery state.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_failure_and_unconfirmed_cleanup_retain_primary_with_unknown_effect(bool known)
    {
        Exception primary = known
            ? KeycloakDiagnostics.Failure(KeycloakFailureCode.Cancelled, KeycloakSafePhase.Inspection,
                "runtime:probe", KeycloakEffectState.None, "private action")
            : new IOException("private primary");
        Task<ProcessObservation> Run(IReadOnlyList<string> args, CancellationToken token)
        {
            if (args[0] == "create") throw primary;
            if (args[0] == "rm") throw new IOException("private cleanup");
            return Task.FromResult(Reply(args, Image, Image.ManifestDigest));
        }

        var failure = await Record.ExceptionAsync(() => DockerInspection.ProbeAsync(Image, "26.7.2", default, Run));

        Assert.Same(primary, failure);
        var mapped = KeycloakDiagnostics.Map(primary, KeycloakSafePhase.Inspection,
            "runtime:probe", KeycloakEffectState.None, "cleanup-correlation");
        Assert.Equal(known ? KeycloakFailureCode.Cancelled : KeycloakFailureCode.CaptureInvalid, mapped.Code);
        Assert.Equal(KeycloakEffectState.Unknown, mapped.Effect);
        Assert.Equal(KeycloakRecoveryClass.InspectBeforeRetry, mapped.Recovery);
        Assert.Equal("cleanup-correlation", mapped.CorrelationId.Value);
        Assert.Null(mapped.InnerException);
        Assert.DoesNotContain("private", mapped.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-argument")]
    [InlineData("bad\nreference")]
    public async Task Invalid_resolver_reference_never_invokes_Docker(string reference)
    {
        var invoked = false;
        Task<ProcessObservation> Run(IReadOnlyList<string> args, CancellationToken token) { invoked = true; throw new InvalidOperationException(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerInspection.ResolveLocalAsync(reference, default, Run));
        Assert.False(invoked);
        var events = new List<KeycloakStageEvent>();
        using var subscription = KeycloakStageEvents.Subscribe(events.Add);
        var failure = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.ResolveLocalImageAsync(reference));
        Assert.Equal(KeycloakFailureCode.InspectionFailed, failure.Code); Assert.Equal(KeycloakSafePhase.Inspection, failure.Phase);
        Assert.Contains(events, x => x.Phase == KeycloakStagePhase.Inspection && x.Outcome == KeycloakStageOutcome.Rejected && x.CorrelationId == failure.CorrelationId.Value);
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("manifest")]
    [InlineData("config")]
    [InlineData("layers")]
    [InlineData("null-image")]
    [InlineData("default-platform")]
    [InlineData("uppercase-config")]
    [InlineData("nonhex-config")]
    [InlineData("null-diffid")]
    public void Invalid_identity_is_rejected(string field)
    {
        var image = field switch
        {
            "null-image" => null!,
            "platform" => Image with { Platform = "windows/amd64" },
            "default-platform" => Image with { Platform = "linux" },
            "manifest" => Image with { ManifestDigest = "sha256:bad" },
            "config" or "uppercase-config" => Image with { ConfigDigest = "SHA256:" + new string('B', 64) },
            "nonhex-config" => Image with { ConfigDigest = "sha256:" + new string('g', 64) },
            "null-diffid" => Image with { DiffIds = [null!] },
            _ => Image with { DiffIds = [] }
        };
        Assert.Throws<InvalidDataException>(() => DockerInspection.Validate(image));
    }

    [Fact]
    public async Task Resolve_local_core_returns_fixture_identity_and_correlated_success()
    {
        using var fixture = TinyImageFixture.Create();
        var events = new List<KeycloakStageEvent>();
        using var subscription = KeycloakStageEvents.Subscribe(events.Add);
        Task<ProcessObservation> Command(IReadOnlyList<string> args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (args[0] == "image" && args[1] == "save")
                File.Copy(fixture.ArchivePath, args[3]);
            return Task.FromResult(args[0] == "version" ? Observation("29.7.2") : Reply(args, fixture.InspectionImage, fixture.InspectionImage.ManifestDigest));
        }

        var result = await KeycloakSecurity.ResolveLocalImageCoreAsync("local:fixture", default, Command);

        Assert.Equal(fixture.InspectionImage.ManifestDigest, result.ManifestDigest);
        Assert.Equal(fixture.InspectionImage.ConfigDigest, result.ConfigDigest);
        Assert.Equal(fixture.InspectionImage.Platform, result.Platform);
        Assert.Equal(fixture.InspectionImage.DiffIds.ToArray(), result.DiffIds.ToArray());
        var own = events.Where(x => x.OperationId == "local-image").ToArray();
        Assert.Equal(2, own.Length);
        Assert.Equal(KeycloakStageOutcome.Started, own[0].Outcome);
        Assert.Equal(KeycloakStageOutcome.Succeeded, own[1].Outcome);
        Assert.Equal(own[0].CorrelationId, own[1].CorrelationId);
        Assert.Equal(fixture.InspectionImage.DiffIds.Length, own[1].CheckCount);
    }

    [Fact]
    public async Task Resolve_local_core_preserves_cancellation_and_does_not_start_export()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var saved = false;
        Task<ProcessObservation> Command(IReadOnlyList<string> args, CancellationToken token)
        {
            saved |= args[0] == "image" && args[1] == "save";
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Observation(""));
        }

        var failure = await Assert.ThrowsAsync<KeycloakSecurityException>(() =>
            KeycloakSecurity.ResolveLocalImageCoreAsync("local:fixture", cancellation.Token, Command));

        Assert.Equal(KeycloakFailureCode.Cancelled, failure.Code);
        Assert.False(saved);
    }

    [Fact]
    public async Task Export_rejects_unsupported_server_and_identity_changed_after_export()
    {
        using var fixture = TinyImageFixture.Create();
        var image = fixture.InspectionImage;
        var unsupported = await Assert.ThrowsAsync<InvalidDataException>(() => DockerInspection.ExportAsync(
            image, Path.Combine(Path.GetTempPath(), "docker-export-unsupported-" + Guid.NewGuid().ToString("N")),
            new HashSet<string>(), default, (args, token) => Task.FromResult(args[0] == "version" ? Observation("28.0") : Reply(args, image, image.ManifestDigest))));
        Assert.Contains("Unsupported Docker server version", unsupported.Message, StringComparison.Ordinal);

        var inspections = 0;
        Task<ProcessObservation> ChangedAfterExport(IReadOnlyList<string> args, CancellationToken token)
        {
            if (args[0] == "image" && args[1] == "inspect")
            {
                inspections++;
                return Task.FromResult(Reply(args, inspections == 2 ? image with { Platform = "linux/arm64" } : image, image.ManifestDigest));
            }
            if (args[0] == "image" && args[1] == "save") File.Copy(fixture.ArchivePath, args[3]);
            return Task.FromResult(Reply(args, image, image.ManifestDigest));
        }

        var archive = Path.Combine(Path.GetTempPath(), "docker-export-changed-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerInspection.ExportAsync(image, archive, new HashSet<string>(), default, ChangedAfterExport));
        Assert.False(File.Exists(archive));
    }

    [Fact]
    public async Task Export_rejects_malformed_binary_archive_without_accepting_identity()
    {
        using var fixture = TinyImageFixture.Create();
        var archive = Path.Combine(fixture.Root, "malformed-export.tar");
        Task<ProcessObservation> Run(IReadOnlyList<string> args, CancellationToken token)
        {
            if (args[0] == "image" && args[1] == "save") File.WriteAllBytes(args[3], [0xff, 0x00, 0x7f]);
            return Task.FromResult(args[0] == "version" ? Observation("29.7.2") : Reply(args, fixture.InspectionImage, fixture.InspectionImage.ManifestDigest));
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => DockerInspection.ExportAsync(
            fixture.InspectionImage, archive, new HashSet<string>(), default, Run));
        Assert.False(File.Exists(archive));
    }

    [Fact]
    public async Task Resolve_rejects_malformed_inspect_metadata_before_export()
    {
        var invoked = false;
        Task<ProcessObservation> Run(IReadOnlyList<string> args, CancellationToken token)
        {
            invoked = true;
            return Task.FromResult(Observation("{\"id\":null,"));
        }

        await Assert.ThrowsAnyAsync<JsonException>(() => DockerInspection.ResolveLocalAsync("local:malformed", default, Run));
        Assert.True(invoked);
    }

    [Fact]
    public async Task Export_rejects_existing_archive_without_deleting_or_invoking_delegate()
    {
        using var fixture = TinyImageFixture.Create();
        var archive = Path.Combine(fixture.Root, "existing.tar");
        await File.WriteAllBytesAsync(archive, [4, 5, 6]);
        var invoked = false;
        Task<ProcessObservation> Run(IReadOnlyList<string> args, CancellationToken token) { invoked = true; throw new InvalidOperationException(); }

        await Assert.ThrowsAsync<InvalidDataException>(() => DockerInspection.ExportAsync(fixture.InspectionImage, archive, new HashSet<string>(), default, Run));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(archive));
        Assert.False(invoked);
    }

    [Fact]
    public async Task Export_save_failure_preserves_delegate_written_archive()
    {
        using var fixture = TinyImageFixture.Create();
        var archive = Path.Combine(fixture.Root, "partial.tar");
        Task<ProcessObservation> Run(IReadOnlyList<string> args, CancellationToken token)
        {
            if (args[0] == "image" && args[1] == "save")
            {
                File.WriteAllBytes(args[3], [9, 8, 7]);
                throw new IOException("save failed");
            }
            return Task.FromResult(args[0] == "version" ? Observation("29.7.2") : Reply(args, fixture.InspectionImage, fixture.InspectionImage.ManifestDigest));
        }

        await Assert.ThrowsAsync<IOException>(() => DockerInspection.ExportAsync(fixture.InspectionImage, archive, new HashSet<string>(), default, Run));
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(archive));
    }

    internal static ProcessObservation Reply(IReadOnlyList<string> args, KeycloakInspectionImage image, string engineId)
    {
        if (args[0] == "image" && args[1] == "inspect") return Observation(JsonSerializer.Serialize(new { id = engineId, os = image.Platform.Split('/')[0], architecture = image.Platform.Split('/')[1], layers = image.DiffIds }));
        if (args[0] == "inspect") return Observation(args[2] == "{{.Image}}" ? engineId : "0");
        if (args[0] == "start") return Observation("Keycloak 26.7.2");
        if (args[0] == "version") return Observation("29.7.2");
        return Observation("");
    }
    internal static ProcessObservation Observation(string output) => new(0, output, "", TimeSpan.Zero);
}
