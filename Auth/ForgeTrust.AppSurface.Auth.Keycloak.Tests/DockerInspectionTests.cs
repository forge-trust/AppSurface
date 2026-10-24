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
        await Assert.ThrowsAsync<InvalidDataException>(() => DockerInspection.ProbeAsync(image, "26.6", default, Run));
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
        var failure = await Assert.ThrowsAsync<KeycloakSecurityException>(() => KeycloakSecurity.ResolveLocalImageAsync(reference));
        Assert.Equal(KeycloakFailureCode.InspectionFailed, failure.Code); Assert.Equal(KeycloakSafePhase.Inspection, failure.Phase);
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("manifest")]
    [InlineData("config")]
    [InlineData("layers")]
    public void Invalid_identity_is_rejected(string field)
    {
        var image = field switch { "platform" => Image with { Platform = "windows/amd64" }, "manifest" => Image with { ManifestDigest = "sha256:bad" }, "config" => Image with { ConfigDigest = "SHA256:" + new string('B', 64) }, _ => Image with { DiffIds = [] } };
        Assert.Throws<InvalidDataException>(() => DockerInspection.Validate(image));
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

        await Assert.ThrowsAsync<InvalidDataException>(() => DockerInspection.ExportAsync(
            image, Path.Combine(Path.GetTempPath(), "docker-export-changed-" + Guid.NewGuid().ToString("N")),
            new HashSet<string>(), default, ChangedAfterExport));
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
