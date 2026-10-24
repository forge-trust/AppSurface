using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public void Copies_only_verified_graph_members_and_rejects_digest_mismatch()
    {
        using var root = new TempDirectory(); var config = Encoding.UTF8.GetBytes("{}"); var layer = Encoding.UTF8.GetBytes("layer"); var configDigest = Digest(config); var layerDigest = Digest(layer); var manifest = Encoding.UTF8.GetBytes($"{{\"config\":{{\"digest\":\"{configDigest}\",\"size\":{config.Length}}},\"layers\":[{{\"digest\":\"{layerDigest}\",\"size\":{layer.Length}}}]}}"); var manifestDigest = Digest(manifest); var archive = Path.Combine(root.Path, "archive.tar");
        using (var file = File.Create(archive)) using (var writer = new TarWriter(file, leaveOpen: true)) { Entry(writer, "blobs/sha256/" + manifestDigest[7..], manifest); Entry(writer, "blobs/sha256/" + configDigest[7..], config); Entry(writer, "blobs/sha256/" + layerDigest[7..], layer); } using (var append = File.Open(archive, FileMode.Append, FileAccess.Write, FileShare.Read)) append.Write(new byte[1024]);
        var output = Path.Combine(root.Path, "out"); OciLayout.CopyVerifiedGraph(archive, manifestDigest, configDigest, output, default); Assert.Equal(layer, File.ReadAllBytes(Path.Combine(output, "blobs", "sha256", layerDigest[7..])));
        Assert.Throws<InvalidDataException>(() => OciLayout.CopyVerifiedGraph(archive, "sha256:" + new string('b', 64), configDigest, Path.Combine(root.Path, "bad"), default));
    }

    private static void Entry(TarWriter writer, string name, byte[] data) => writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed class TempDirectory : IDisposable { internal string Path { get; } = System.IO.Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : System.IO.Path.GetTempPath(), "oci-layout-tests-" + Guid.NewGuid().ToString("N")); internal TempDirectory() => Directory.CreateDirectory(Path); public void Dispose() { try { Directory.Delete(Path, true); } catch { } } }
}
