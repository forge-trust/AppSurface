using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

/// <summary>Protects OCI index authorization and verified graph publication; fails on digest, platform, closure, or duplicate JSON violations; claims only exact graph members in the owned output.</summary>
public sealed class OciLayoutTests
{
    [Fact]
    public void StrictJson_and_upstream_index_require_digest_platform_and_media_type()
    {
        using var root = new TempDirectory(); var selected = "sha256:" + new string('a', 64); var index = Encoding.UTF8.GetBytes($"{{\"schemaVersion\":2,\"manifests\":[{{\"digest\":\"{selected}\",\"platform\":{{\"os\":\"linux\",\"architecture\":\"amd64\"}},\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\"}}]}}"); var path = Path.Combine(root.Path, "index.json"); File.WriteAllBytes(path, index);
        OciLayout.VerifyUpstreamIndex(path, Digest(index), selected, "linux/amd64"); Assert.Throws<InvalidDataException>(() => OciLayout.StrictJson(Encoding.UTF8.GetBytes("{\"x\":1,\"x\":2}")));
        Assert.Throws<InvalidDataException>(() => OciLayout.VerifyUpstreamIndex(path, new string('0', 71), selected, "linux/amd64"));
    }

    [Theory]
    [InlineData("schemaVersion", "1")]
    [InlineData("mediaType", "application/vnd.oci.image.index.v1+json")]
    [InlineData("platform", "linux/arm64")]
    public void Upstream_index_rejects_wrong_schema_media_or_platform(string field, string value)
    {
        using var root = new TempDirectory();
        var selected = "sha256:" + new string('a', 64);
        var index = $"{{\"schemaVersion\":2,\"manifests\":[{{\"digest\":\"{selected}\",\"platform\":{{\"os\":\"linux\",\"architecture\":\"amd64\"}},\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\"}}]}}";
        var bytes = field switch
        {
            "schemaVersion" => Encoding.UTF8.GetBytes(index.Replace("\"schemaVersion\":2", "\"schemaVersion\":1", StringComparison.Ordinal)),
            "mediaType" => Encoding.UTF8.GetBytes(index.Replace("application/vnd.oci.image.manifest.v1+json", value, StringComparison.Ordinal)),
            _ => Encoding.UTF8.GetBytes(index.Replace("\"architecture\":\"amd64\"", "\"architecture\":\"arm64\"", StringComparison.Ordinal))
        };
        var path = Path.Combine(root.Path, field + ".json"); File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => OciLayout.VerifyUpstreamIndex(path, Digest(bytes), selected, "linux/amd64"));
    }

    [Fact]
    public void StrictJson_rejects_malformed_and_duplicate_properties()
    {
        Assert.ThrowsAny<JsonException>(() => OciLayout.StrictJson(Encoding.UTF8.GetBytes("{\"x\":")));
        Assert.Throws<InvalidDataException>(() => OciLayout.StrictJson(Encoding.UTF8.GetBytes("{\"x\":1,\"x\":2}")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Copies_only_verified_graph_members_and_rejects_digest_mismatch(bool v7)
    {
        using var root = new TempDirectory(); var config = Encoding.UTF8.GetBytes("{}"); var layer = Encoding.UTF8.GetBytes("layer"); var configDigest = Digest(config); var layerDigest = Digest(layer); var manifest = Encoding.UTF8.GetBytes($"{{\"config\":{{\"digest\":\"{configDigest}\",\"size\":{config.Length}}},\"layers\":[{{\"digest\":\"{layerDigest}\",\"size\":{layer.Length}}}]}}"); var manifestDigest = Digest(manifest); var archive = Path.Combine(root.Path, "archive.tar");
        using (var file = File.Create(archive)) using (var writer = new TarWriter(file, leaveOpen: true)) { GraphEntry(writer, "blobs/sha256/" + manifestDigest[7..], manifest, v7); GraphEntry(writer, "blobs/sha256/" + configDigest[7..], config, v7); GraphEntry(writer, "blobs/sha256/" + layerDigest[7..], layer, v7); }
        using (var append = File.Open(archive, FileMode.Append, FileAccess.Write, FileShare.Read)) append.Write(new byte[1024]);
        var output = Path.Combine(root.Path, "out"); OciLayout.CopyVerifiedGraph(archive, manifestDigest, configDigest, output, default); Assert.Equal(layer, File.ReadAllBytes(Path.Combine(output, "blobs", "sha256", layerDigest[7..])));
        Assert.Throws<InvalidDataException>(() => OciLayout.CopyVerifiedGraph(archive, "sha256:" + new string('b', 64), configDigest, Path.Combine(root.Path, "bad"), default));
    }

    [Fact]
    public void Rejects_duplicate_manifest_graph_identity()
    {
        using var root = new TempDirectory();
        var config = Encoding.UTF8.GetBytes("{}");
        var configDigest = Digest(config);
        var manifest = Encoding.UTF8.GetBytes($"{{\"config\":{{\"digest\":\"{configDigest}\",\"size\":{config.Length}}},\"layers\":[{{\"digest\":\"{configDigest}\",\"size\":{config.Length}}}]}}");
        var manifestDigest = Digest(manifest);
        var archive = WriteGraphArchive(root.Path, manifestDigest, manifest, configDigest, config);

        AssertGraphRejected(root.Path, archive, manifestDigest, configDigest, "Repeated layer identity unsupported.");
    }

    [Fact]
    public void Rejects_ambiguous_repeated_manifest_archive_member()
    {
        using var root = new TempDirectory();
        var config = Encoding.UTF8.GetBytes("{}");
        var configDigest = Digest(config);
        var manifest = Encoding.UTF8.GetBytes($"{{\"config\":{{\"digest\":\"{configDigest}\",\"size\":{config.Length}}},\"layers\":[]}}");
        var manifestDigest = Digest(manifest);
        var archive = WriteGraphArchive(root.Path, manifestDigest, manifest, configDigest, config, duplicateManifest: true);

        AssertGraphRejected(root.Path, archive, manifestDigest, configDigest, "Manifest is oversized or ambiguous.");
    }

    [Theory]
    [InlineData(false, false, "Graph member is missing, repeated, or oversized.")]
    [InlineData(true, false, "Graph member is missing, repeated, or oversized.")]
    [InlineData(false, true, "Graph member digest differs.")]
    [InlineData(false, false, "Graph closure is incomplete.")]
    public void Rejects_invalid_graph_members_and_incomplete_closure(bool duplicateConfig, bool corruptConfig, string expectedMessage)
    {
        using var root = new TempDirectory();
        var config = Encoding.UTF8.GetBytes("{}");
        var layer = Encoding.UTF8.GetBytes("layer");
        var configDigest = Digest(config);
        var layerDigest = Digest(layer);
        var includeLayer = expectedMessage != "Graph closure is incomplete.";
        var claimedConfigSize = expectedMessage == "Graph member is missing, repeated, or oversized." && !duplicateConfig ? config.Length + 1 : config.Length;
        var manifest = Encoding.UTF8.GetBytes($"{{\"config\":{{\"digest\":\"{configDigest}\",\"size\":{claimedConfigSize}}},\"layers\":[{{\"digest\":\"{layerDigest}\",\"size\":{layer.Length}}}]}}");
        var manifestDigest = Digest(manifest);
        var archivedConfig = corruptConfig ? Encoding.UTF8.GetBytes("xx") : config;
        var archive = WriteGraphArchive(root.Path, manifestDigest, manifest, configDigest, archivedConfig, duplicateConfig, includeLayer ? layerDigest : null, layer);

        AssertGraphRejected(root.Path, archive, manifestDigest, configDigest, expectedMessage);
    }

    [Fact]
    public void Rejects_noncanonical_uppercase_graph_digest()
    {
        using var root = new TempDirectory();
        var config = Encoding.UTF8.GetBytes("{}");
        var canonicalDigest = Digest(config);
        var uppercaseDigest = "sha256:" + canonicalDigest[7..].ToUpperInvariant();
        var manifest = Encoding.UTF8.GetBytes($"{{\"config\":{{\"digest\":\"{uppercaseDigest}\",\"size\":{config.Length}}},\"layers\":[]}}");
        var manifestDigest = Digest(manifest);
        var archive = WriteGraphArchive(root.Path, manifestDigest, manifest, uppercaseDigest, config);

        AssertGraphRejected(root.Path, archive, manifestDigest, uppercaseDigest, "Noncanonical graph digest.");
    }

    [Fact]
    public void ManifestMemberCannotBeALinkEvenWithTheSelectedDigestName()
    {
        using var root = new TempDirectory();
        var manifestDigest = "sha256:" + new string('a', 64);
        var archive = Path.Combine(root.Path, "linked-manifest.tar");
        using (var file = File.Create(archive))
        using (var writer = new TarWriter(file))
            writer.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "blobs/sha256/" + manifestDigest[7..]) { LinkName = "elsewhere" });
        AssertGraphRejected(root.Path, archive, manifestDigest, "sha256:" + new string('b', 64), "Manifest is oversized or ambiguous.");
    }

    private static string WriteGraphArchive(string directory, string manifestDigest, byte[] manifest, string configDigest, byte[] config, bool duplicateConfig = false, string? layerDigest = null, byte[]? layer = null, bool duplicateManifest = false)
    {
        var archive = Path.Combine(directory, "graph-" + Guid.NewGuid().ToString("N") + ".tar");
        using (var file = File.Create(archive))
        {
            using (var writer = new TarWriter(file, leaveOpen: true))
            {
                Entry(writer, "blobs/sha256/" + manifestDigest[7..], manifest);
                if (duplicateManifest)
                    Entry(writer, "blobs/sha256/" + manifestDigest[7..], manifest);
                Entry(writer, "blobs/sha256/" + configDigest[7..], config);
                if (duplicateConfig)
                    Entry(writer, "blobs/sha256/" + configDigest[7..], config);
                if (layerDigest is not null)
                    Entry(writer, "blobs/sha256/" + layerDigest[7..], layer!);
            }
            file.Write(new byte[1024]);
        }
        return archive;
    }

    private static void AssertGraphRejected(string directory, string archive, string manifestDigest, string configDigest, string expectedMessage)
    {
        var output = Path.Combine(directory, "rejected-" + Guid.NewGuid().ToString("N"));
        var exception = Assert.Throws<InvalidDataException>(() => OciLayout.CopyVerifiedGraph(archive, manifestDigest, configDigest, output, default));
        Assert.Equal(expectedMessage, exception.Message);
        Assert.False(Directory.Exists(output));
    }

    private static void GraphEntry(TarWriter writer, string name, byte[] data, bool v7)
    {
        TarEntry entry = v7 ? new V7TarEntry(TarEntryType.V7RegularFile, name) : new UstarTarEntry(TarEntryType.RegularFile, name);
        entry.DataStream = new MemoryStream(data);
        writer.WriteEntry(entry);
    }

    private static void Entry(TarWriter writer, string name, byte[] data) => writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed class TempDirectory : IDisposable { internal string Path { get; } = System.IO.Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : System.IO.Path.GetTempPath(), "oci-layout-tests-" + Guid.NewGuid().ToString("N")); internal TempDirectory() { Directory.CreateDirectory(Path); } public void Dispose() { try { Directory.Delete(Path, true); } catch { } } }
}
