using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

/// <summary>Protects OCI graph identity, layer ordering, whiteouts, descriptor sizes, JSON shape, and resource budgets; fails on ambiguous or mismatched image material; claims exact merged paths or typed rejection.</summary>
public sealed class OciReaderTests
{
    [Fact]
    public void Merges_parent_replacement_and_whiteouts_with_exact_diff_id()
    {
        using var fixture = OciFixture.Create();
        var observed = new OciReader().Read(fixture.Archive, fixture.Expected);
        Assert.Contains(observed.Entries, x => x.Path == "etc/current" && x.Type == "file");
        Assert.DoesNotContain(observed.Entries, x => x.Path == "etc/old");
        Assert.DoesNotContain(observed.Entries, x => x.Path == "etc/tree/child");
        Assert.DoesNotContain(observed.Entries, x => x.Path == "etc/tree/other");
    }

    [Fact]
    public void A_whiteout_removing_a_selected_path_fails_without_publishing_output()
    {
        using var fixture = OciFixture.Create();
        var output = Path.Combine(fixture.Root, "selected");
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected,
            new OciReadSelection(new HashSet<string>(["etc/old"], StringComparer.Ordinal), output)));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Selection_rejects_a_linked_or_non_directory_ancestor()
    {
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("payload")));
        var observed = new Dictionary<string, ObservedFile>
        {
            ["etc"] = new("etc", "symlink", "", 0, 511, 0, 0, "target", 0),
            ["etc/file"] = new("etc/file", "file", hash, 7, 420, 0, 0, null, 0)
        };
        Assert.Throws<InvalidDataException>((Action)(() => OciReader.ValidateExpectedFiles(observed,
            new Dictionary<string, OciExpectedFile> { ["etc/file"] = new("etc/file", hash) })));
    }

    [Fact]
    public void Rejects_duplicate_json_diff_id_and_tar_budget_without_large_payloads()
    {
        using var fixture = OciFixture.Create(duplicateIndex: true);
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected));
        using var limited = OciFixture.Create(layerEntryBytes: 2);
        Assert.Throws<InvalidDataException>(() => new OciReader(new OciReaderLimits(MaxLayerEntryBytes: 2)).Read(limited.Archive, limited.Expected));
        using var cancelled = OciFixture.Create(); using var c = new CancellationTokenSource(); c.Cancel();
        Assert.Throws<OperationCanceledException>(() => new OciReader().Read(cancelled.Archive, cancelled.Expected, c.Token));
    }

    [Theory]
    [InlineData("missing-layer", false, false)]
    [InlineData("bad-size", false, true)]
    [InlineData("bad-config-type", true, false)]
    public void Rejects_nested_config_type_missing_layer_and_descriptor_size(string caseName, bool badConfigType, bool badSize)
    {
        using var fixture = OciFixture.Create(badConfigType: badConfigType, omitSecondLayer: caseName == "missing-layer", badSize: badSize);
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected));
    }

    private sealed class OciFixture : IDisposable
    {
        internal string Archive { get; private init; } = ""; internal OciExpectedImage Expected { get; private init; } = null!; internal string Root { get; init; } = "";
        internal static OciFixture Create(bool duplicateIndex = false, long layerEntryBytes = 1024 * 1024, bool badConfigType = false, bool omitSecondLayer = false, bool badSize = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "oci-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var lower = Tar([("etc/old", "old"), ("etc/tree/child", "child"), ("etc/tree/other", "other")]);
            var upper = Tar([("etc/.wh.old", ""), ("etc/.wh..wh..opq", ""), ("etc/current", "new")]);
            if (layerEntryBytes < 1024) upper = Tar([("etc/current", "long")]);
            var layers = new[] { Gzip(lower), Gzip(upper) }; var diffIds = layers.Select(x => "sha256:" + Hex(SHA256.HashData(UnGzip(x)))).ToArray(); var layerDigests = layers.Select(x => "sha256:" + Hex(SHA256.HashData(x))).ToArray();
            var config = Encoding.UTF8.GetBytes("{\"os\":\"linux\",\"architecture\":\"amd64\",\"config\":{\"User\":\"\",\"Entrypoint\":[]},\"rootfs\":{\"type\":\"layers\",\"diff_ids\":[\"" + diffIds[0] + "\",\"" + diffIds[1] + "\"]}}"); var configDigest = "sha256:" + Hex(SHA256.HashData(config));
            var configMediaType = badConfigType ? "application/vnd.oci.image.config.v1+json+unknown" : "application/vnd.oci.image.config.v1+json";
            var manifest = Encoding.UTF8.GetBytes($"{{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\",\"config\":{{\"mediaType\":\"{configMediaType}\",\"digest\":\"{configDigest}\",\"size\":{config.Length}}},\"layers\":[{{\"mediaType\":\"application/vnd.oci.image.layer.v1.tar+gzip\",\"digest\":\"{layerDigests[0]}\",\"size\":{layers[0].Length}}},{{\"mediaType\":\"application/vnd.oci.image.layer.v1.tar+gzip\",\"digest\":\"{layerDigests[1]}\",\"size\":{(badSize ? layers[1].Length + 1 : layers[1].Length)}}}]}}");
            var manifestDigest = "sha256:" + Hex(SHA256.HashData(manifest)); var indexText = duplicateIndex ? "{\"schemaVersion\":2,\"schemaVersion\":2,\"manifests\":[]}" : $"{{\"schemaVersion\":2,\"manifests\":[{{\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\",\"digest\":\"{manifestDigest}\",\"size\":{manifest.Length},\"platform\":{{\"os\":\"linux\",\"architecture\":\"amd64\"}}}}]}}";
            var index = Encoding.UTF8.GetBytes(indexText); var archive = Path.Combine(root, "archive.tar"); using (var ms = File.Create(archive)) { using (var tw = new TarWriter(ms, true)) { Entry(tw, "oci-layout", Encoding.UTF8.GetBytes("{\"imageLayoutVersion\":\"1.0.0\"}")); Entry(tw, "index.json", index); Entry(tw, "blobs/sha256/" + manifestDigest[7..], manifest); Entry(tw, "blobs/sha256/" + configDigest[7..], config); for (var i = 0; i < layers.Length; i++) if (!omitSecondLayer || i == 0) Entry(tw, "blobs/sha256/" + layerDigests[i][7..], layers[i]); } ms.Write(new byte[1024]); }
            return new OciFixture { Root = root, Archive = archive, Expected = new(null, manifestDigest, "linux/amd64", configDigest, diffIds) };
        }
        private static void Entry(TarWriter tw, string name, byte[] data) => tw.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
        private static byte[] Tar((string Name, string Value)[] files) { using var ms = new MemoryStream(); using (var tw = new TarWriter(ms, true)) foreach (var f in files) Entry(tw, f.Name, Encoding.UTF8.GetBytes(f.Value)); ms.Write(new byte[1024]); return ms.ToArray(); }
        private static byte[] Gzip(byte[] b) { using var ms = new MemoryStream(); using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, true)) gz.Write(b); return ms.ToArray(); }
        private static byte[] UnGzip(byte[] b) { using var input = new MemoryStream(b); using var gz = new GZipStream(input, CompressionMode.Decompress); using var output = new MemoryStream(); gz.CopyTo(output); return output.ToArray(); }
        private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
