using System.Collections.Immutable;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Exact final image identity selected by the host for inspection.</summary>
/// <param name="ManifestDigest">The immutable selected platform manifest digest.</param>
/// <param name="ConfigDigest">The exact OCI config digest.</param>
/// <param name="Platform">The explicit Linux image platform.</param>
/// <param name="DiffIds">The ordered uncompressed rootfs layer identities.</param>
public sealed record KeycloakInspectionImage(string ManifestDigest, string ConfigDigest, string Platform, ImmutableArray<string> DiffIds);

internal delegate Task<ProcessObservation> DockerInspectionCommand(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

internal sealed record RuntimeProbeObservation(string Image, string Config, string Platform, bool Passed, string AdapterVersion);

/// <summary>The supported Docker 29 immutable export and read-only runtime handoff.</summary>
internal static class DockerInspection
{
    internal static async Task<ObservedOciImage> ExportAsync(KeycloakInspectionImage image, string absentArchive, IReadOnlySet<string> payloads, CancellationToken token, DockerInspectionCommand? command = null)
    {
        var run = command ?? Run;
        Validate(image);
        var version = await run(["version", "--format", "{{.Server.Version}}"], token).ConfigureAwait(false);
        KeycloakSecurity.Require(version.Output.Trim().StartsWith("29.", StringComparison.Ordinal), "Unsupported Docker server version.");
        var identity = await ImageIdentity(image.ManifestDigest, token, run).ConfigureAwait(false);
        Verify(identity, image);
        _ = await run(["image", "save", "--output", absentArchive, image.ManifestDigest], token).ConfigureAwait(false);
        var observed = new OciReader().Read(absentArchive, new(null, image.ManifestDigest, image.Platform, image.ConfigDigest, image.DiffIds,
            PayloadPaths: payloads), token);
        var after = await ImageIdentity(image.ManifestDigest, token, run).ConfigureAwait(false);
        Verify(after, image);
        return observed;
    }

    internal static async Task<RuntimeProbeObservation> ProbeAsync(KeycloakInspectionImage image, string version, CancellationToken token, DockerInspectionCommand? command = null)
    {
        var run = command ?? Run;
        Validate(image);
        KeycloakSecurity.Require(version == "26.7.2", "Unsupported runtime profile version.");
        var before = await ImageIdentity(image.ManifestDigest, token, run).ConfigureAwait(false); Verify(before, image);
        var name = "appsurface-keycloak-probe-" + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            // Fixed framework-owned probes only. No host paths, credentials, callback, or recipe shell.
            const string script = "test \"$(id -u)\" = 1000 && test -s /etc/ssl/certs/ca-certificates.crt && test -s /opt/java/openjdk/lib/security/cacerts && test ! -e /usr/bin/pebble && ! command -v curl && java -version && /opt/keycloak/bin/kc.sh --version && /opt/keycloak/bin/kcadm.sh --help >/dev/null";
            _ = await run(["create", "--name", name, "--platform", image.Platform, "--network", "none", "--read-only", "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges", "--memory", "3g", "--cpus", "4", "--tmpfs", "/tmp:rw,nosuid,nodev,size=128m",
                "--entrypoint", "/bin/sh", image.ManifestDigest, "-ec", script], token).ConfigureAwait(false);
            created = true;
            var container = await run(["inspect", "--format", "{{.Image}}", name], token).ConfigureAwait(false);
            KeycloakSecurity.Require(container.Output.Trim() == before.EngineId, "Probe container image differs from inspected content.");
            var after = await ImageIdentity(image.ManifestDigest, token, run).ConfigureAwait(false); Verify(after, image);
            var observation = await run(["start", "--attach", name], token).ConfigureAwait(false);
            var status = await run(["inspect", "--format", "{{.State.ExitCode}}", name], token).ConfigureAwait(false);
            KeycloakSecurity.Require(status.Output.Trim() == "0" && (observation.Output + observation.Error).Contains("Keycloak " + version, StringComparison.Ordinal), "Runtime probe failed.");
            return new(image.ManifestDigest, image.ConfigDigest, image.Platform, true, "docker-29-v1");
        }
        finally
        {
            if (created)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { _ = await run(["rm", "--force", name], cleanup.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or BoundedProcessException or OperationCanceledException) { }
            }
        }
    }

    internal static async Task<KeycloakInspectionImage> ResolveLocalAsync(string reference, CancellationToken token, DockerInspectionCommand? command = null)
    {
        var run = command ?? Run;
        KeycloakSecurity.Require(reference is { Length: > 0 and <= 256 } && !reference!.StartsWith('-')
            && reference!.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '@'), "Invalid local image reference.");
        var selected = await ImageIdentity(reference!, token, run).ConfigureAwait(false);
        // Docker 29 containerd stores expose the platform manifest ID. Classic config-only
        // stores cannot supply this mapping and must use an explicit supported immutable identity.
        var version = await run(["version", "--format", "{{.Server.Version}}"], token).ConfigureAwait(false);
        KeycloakSecurity.Require(version.Output.Trim().StartsWith("29.", StringComparison.Ordinal), "Unsupported Docker server version.");
        var scratch = Path.Combine(KeycloakSecurity.PrivateTemporaryRoot(), "appsurface-image-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var archive = Path.Combine(scratch, "image.tar");
            _ = await run(["image", "save", "--output", archive, selected.EngineId], token).ConfigureAwait(false);
            using var file = File.OpenRead(archive);
            KeycloakSecurity.Require(file.Length <= 1024L * 1024 * 1024, "Image archive exceeds profile.");
            using var preflight = new TarBudgetReadStream(file, 1024L * 1024 * 1024, 1024L * 1024 * 1024);
            using var tar = new System.Formats.Tar.TarReader(preflight, true);
            byte[]? manifest = null;
            System.Formats.Tar.TarEntry? entry;
            while ((entry = tar.GetNextEntry()) is not null)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Name != "blobs/sha256/" + selected.EngineId[7..]) continue;
                KeycloakSecurity.Require(manifest is null && entry.Length is > 0 and <= 1024 * 1024, "Selected manifest is ambiguous or oversized.");
                manifest = new byte[(int)entry.Length]; entry.DataStream!.ReadExactly(manifest);
            }
            KeycloakSecurity.Require(manifest is not null && "sha256:" + KeycloakSecurity.Hash(manifest) == selected.EngineId, "Platform manifest is unavailable from this Docker store.");
            preflight.Finish();
            using var json = OciLayout.StrictJson(manifest!);
            if (!json.RootElement.TryGetProperty("config", out _))
                throw new NotSupportedException("The local reference must resolve to a platform manifest. Build with the fixed context arguments, including disabled provenance, or select an immutable platform manifest.");
            var config = json.RootElement.GetProperty("config").GetProperty("digest").GetString()!;
            var result = new KeycloakInspectionImage(selected.EngineId, config, selected.Platform, selected.DiffIds);
            _ = new OciReader().Read(archive, new(null, result.ManifestDigest, result.Platform, result.ConfigDigest, result.DiffIds), token);
            Verify(await ImageIdentity(selected.EngineId, token, run).ConfigureAwait(false), result);
            return result;
        }
        finally { KeycloakSecurity.CleanOwned(scratch); }
    }

    private sealed record EngineIdentity(string EngineId, string Platform, ImmutableArray<string> DiffIds);
    private static async Task<EngineIdentity> ImageIdentity(string digest, CancellationToken token, DockerInspectionCommand run)
    {
        // Only identity fields are collected; ambient image environment values never enter logs.
        var result = await run(["image", "inspect", "--format", "{\"id\":{{json .Id}},\"os\":{{json .Os}},\"architecture\":{{json .Architecture}},\"layers\":{{json .RootFS.Layers}}}", digest], token).ConfigureAwait(false);
        using var json = OciLayout.StrictJson(System.Text.Encoding.UTF8.GetBytes(result.Output));
        var root = json.RootElement;
        return new(root.GetProperty("id").GetString()!, root.GetProperty("os").GetString() + "/" + root.GetProperty("architecture").GetString(),
            root.GetProperty("layers").EnumerateArray().Select(x => x.GetString()!).ToImmutableArray());
    }
    private static void Verify(EngineIdentity observed, KeycloakInspectionImage expected)
    {
        // Docker 29 containerd exposes the manifest as .Id; classic stores expose the
        // config. Both mappings require independent export config and ordered diffID proof.
        KeycloakSecurity.Require((observed.EngineId == expected.ManifestDigest || observed.EngineId == expected.ConfigDigest)
            && observed.Platform == expected.Platform && observed.DiffIds.SequenceEqual(expected.DiffIds), "Docker identity/config/rootfs mapping differs.");
    }
    internal static void Validate(KeycloakInspectionImage image)
    {
        KeycloakSecurity.Require(image is not null && image.Platform is "linux/arm64" or "linux/amd64"
            && IsDigest(image.ManifestDigest) && IsDigest(image.ConfigDigest) && !image.DiffIds.IsDefaultOrEmpty && image.DiffIds.All(IsDigest), "Invalid immutable inspection identity.");
    }
    private static bool IsDigest(string x) => x is { Length: 71 } && x.StartsWith("sha256:", StringComparison.Ordinal)
        && x[7..].All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    private static async Task<ProcessObservation> Run(IReadOnlyList<string> arguments, CancellationToken token)
    {
        var result = await BoundedProcess.RunAsync("docker", arguments, null, TimeSpan.FromSeconds(300), 1024 * 1024, [], token).ConfigureAwait(false);
        KeycloakSecurity.Require(result.ExitCode == 0, "Docker inspection adapter returned a nonzero exit.");
        return result;
    }
}

public static partial class KeycloakSecurity
{
    /// <summary>Resolves a host-local Docker 29 image reference to a verified immutable platform identity.</summary>
    /// <param name="hostImageReference">The host's local tag or immutable image reference.</param>
    /// <param name="cancellationToken">Cancels the single bounded resolution/export.</param>
    /// <returns>The actual exported and verified manifest, config, platform and ordered rootfs identities.</returns>
    /// <exception cref="KeycloakSecurityException">The local image store or identity mapping is unsupported or invalid.</exception>
    public static async Task<KeycloakInspectionImage> ResolveLocalImageAsync(string hostImageReference, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(300));
            return await DockerInspection.ResolveLocalAsync(hostImageReference, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { throw KeycloakDiagnostics.Map(ex, KeycloakSafePhase.Inspection, "local-image", KeycloakEffectState.None); }
    }
}
