using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Auth.Keycloak;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// protects: OCI parsing rejects malformed graph metadata and bounded work, while selected payloads remain exact.
// fails_when: malformed JSON, descriptors, media, layers, or selection limits are accepted as an image.
// no_claim: this uses independent synthetic OCI bytes and does not prove Docker behavior.
public sealed class OciReaderBoundaryTests
{
    [Fact]
    public void Valid_graph_reads_and_selected_payload_is_materialized()
    {
        using var fixture = BoundaryOciFixture.Create();
        var selected = Path.Combine(fixture.Root, "selected");
        var observed = new OciReader().Read(fixture.Archive, fixture.Expected,
            new OciReadSelection(new HashSet<string>([BoundaryOciFixture.PayloadPath], StringComparer.Ordinal), selected));
        var payloadPath = TestPathUtils.PathUnder(selected, BoundaryOciFixture.PayloadPath);

        Assert.Contains(observed.Entries, x => x.Path == BoundaryOciFixture.PayloadPath && x.Type == "file");
        Assert.Equal("payload\n", File.ReadAllText(payloadPath));
    }

    [Theory]
    [InlineData(BoundaryOciMutation.InvalidJson)]
    [InlineData(BoundaryOciMutation.InvalidSchema)]
    [InlineData(BoundaryOciMutation.InvalidManifestMediaType)]
    [InlineData(BoundaryOciMutation.InvalidDescriptorSize)]
    public void Malformed_graph_metadata_is_rejected(BoundaryOciMutation mutation)
    {
        using var fixture = BoundaryOciFixture.Create(mutation);
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected));
    }

    [Fact]
    public void Layer_count_budget_is_rejected()
    {
        using var fixture = BoundaryOciFixture.Create(BoundaryOciMutation.TwoLayers);
        Assert.Throws<InvalidDataException>(() => new OciReader(new OciReaderLimits(MaxLayers: 1)).Read(fixture.Archive, fixture.Expected));
    }

    [Fact]
    public void Selected_path_and_payload_budgets_are_rejected()
    {
        using var fixture = BoundaryOciFixture.Create();
        var selected = Path.Combine(fixture.Root, "selected");
        Assert.Throws<InvalidDataException>(() => new OciReader(new OciReaderLimits(MaxPaths: 1)).Read(fixture.Archive, fixture.Expected,
            new OciReadSelection(new HashSet<string>([BoundaryOciFixture.PayloadPath, "etc"], StringComparer.Ordinal), selected)));
        Assert.Throws<InvalidDataException>(() => new OciReader(new OciReaderLimits(MaxSelectedOutputBytes: 2)).Read(fixture.Archive, fixture.Expected,
            new OciReadSelection(new HashSet<string>([BoundaryOciFixture.PayloadPath], StringComparer.Ordinal), Path.Combine(fixture.Root, "selected-small"))));
    }

    [Fact]
    public void Selected_unknown_target_and_config_expectations_are_rejected()
    {
        using var fixture = BoundaryOciFixture.Create();
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected with { ConfigDigest = "sha256:" + new string('0', 64) }));
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected,
            new OciReadSelection(new HashSet<string>(["etc/missing"], StringComparer.Ordinal), Path.Combine(fixture.Root, "missing-target"))));
    }

    [Fact]
    public void Config_and_selected_file_expectations_are_verified()
    {
        using var fixture = BoundaryOciFixture.Create();
        var expected = fixture.Expected with
        {
            ConfigUser = "0:0",
            ConfigEntrypoint = ["/bin/true"],
            ConfigEnvironment = new Dictionary<string, string> { ["BOUNDARY"] = "true" },
            SelectedFiles = new Dictionary<string, OciExpectedFile>
            {
                [BoundaryOciFixture.PayloadPath] = new(BoundaryOciFixture.PayloadPath, Digest(Encoding.UTF8.GetBytes("payload\n")), Mode: 420, Uid: 0, Gid: 0)
            }
        };

        var observed = new OciReader().Read(fixture.Archive, expected);

        Assert.Equal("0:0", observed.ConfigUser);
        Assert.Equal(new[] { "/bin/true" }, observed.ConfigEntrypoint.ToArray());
        Assert.Equal("true", observed.ConfigEnvironment["BOUNDARY"]);
        Assert.Contains(observed.Entries, x => x.Path == BoundaryOciFixture.PayloadPath && x.Mode == 420 && x.Uid == 0 && x.Gid == 0);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("ancestor")]
    [InlineData("link")]
    [InlineData("unsafe-path")]
    public void Expected_file_contract_rejects_hostile_observations(string caseName)
    {
        var observed = new Dictionary<string, ObservedFile>(StringComparer.Ordinal)
        {
            ["etc"] = new("etc", "directory", "", 0, 493, 0, 0, null, 0),
            ["etc/payload"] = new("etc/payload", "file", Digest(Encoding.UTF8.GetBytes("payload\n")), 8, 420, 0, 0, null, 0),
            ["etc/link"] = new("etc/link", "symlink", "", 0, 511, 0, 0, "payload", 0)
        };
        var expected = caseName switch
        {
            "hash" => new OciExpectedFile("etc/payload", Digest(Encoding.UTF8.GetBytes("changed\n"))),
            "ancestor" => new OciExpectedFile("etc/missing/payload", Digest(Encoding.UTF8.GetBytes("payload\n"))),
            "link" => new OciExpectedFile("etc/link", "sha256:" + new string('0', 64), Type: "symlink"),
            _ => new OciExpectedFile("../payload", Digest(Encoding.UTF8.GetBytes("payload\n")))
        };

        Assert.Throws<InvalidDataException>(() => OciReader.ValidateExpectedFiles(observed, new Dictionary<string, OciExpectedFile> { [expected.Path] = expected }));
    }

    [Fact]
    public void Selection_requires_absent_output_and_at_least_one_path()
    {
        using var fixture = BoundaryOciFixture.Create();
        var existing = Path.Combine(fixture.Root, "existing"); Directory.CreateDirectory(existing);
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected,
            new OciReadSelection(new HashSet<string>([BoundaryOciFixture.PayloadPath], StringComparer.Ordinal), existing)));
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected,
            new OciReadSelection(new HashSet<string>(StringComparer.Ordinal), Path.Combine(fixture.Root, "empty-selection"))));
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("diff-ids")]
    [InlineData("entrypoint")]
    [InlineData("environment")]
    public void Config_identity_expectations_reject_mismatches(string field)
    {
        using var fixture = BoundaryOciFixture.Create();
        var expected = field switch
        {
            "platform" => fixture.Expected with { Platform = "linux/arm64" },
            "diff-ids" => fixture.Expected with { DiffIds = ["sha256:" + new string('0', 64)] },
            "entrypoint" => fixture.Expected with { ConfigEntrypoint = ["/bin/false"] },
            _ => fixture.Expected with { ConfigEnvironment = new Dictionary<string, string> { ["BOUNDARY"] = "false" } }
        };

        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, expected));
    }

    private static string Digest(byte[] value) => "sha256:" + Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    [Theory]
    [InlineData("archive")]
    [InlineData("compressed")]
    [InlineData("expanded-layer")]
    [InlineData("expanded-total")]
    [InlineData("json")]
    [InlineData("layers")]
    [InlineData("layer-members")]
    [InlineData("total-members")]
    [InlineData("paths")]
    [InlineData("path-length")]
    [InlineData("payload")]
    [InlineData("payload-aggregate")]
    [InlineData("selected-output")]
    [InlineData("layer-entry")]
    [InlineData("layer-entry-over-expanded")]
    [InlineData("aggregate-under-payload")]
    public void Invalid_limit_profiles_are_rejected(string profile)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OciReader(InvalidLimits(profile)));
    }

    private static OciReaderLimits InvalidLimits(string profile) => profile switch
    {
        "archive" => new(MaxArchiveBytes: 0),
        "compressed" => new(MaxCompressedBlobBytes: 0),
        "expanded-layer" => new(MaxExpandedLayerBytes: 0),
        "expanded-total" => new(MaxExpandedTotalBytes: 0),
        "json" => new(MaxJsonBytes: 0),
        "layers" => new(MaxLayers: 0),
        "layer-members" => new(MaxLayerMembers: 0),
        "total-members" => new(MaxTotalMembers: 0),
        "paths" => new(MaxPaths: 0),
        "path-length" => new(MaxPathLength: 0),
        "payload" => new(MaxPayloadBytes: 0),
        "payload-aggregate" => new(MaxPayloadAggregateBytes: 0),
        "selected-output" => new(MaxSelectedOutputBytes: 0),
        "layer-entry" => new(MaxLayerEntryBytes: 0),
        "layer-entry-over-expanded" => new(MaxExpandedLayerBytes: 1, MaxLayerEntryBytes: 2),
        "aggregate-under-payload" => new(MaxPayloadBytes: 2, MaxPayloadAggregateBytes: 1),
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    public enum BoundaryOciMutation
    {
        Valid,
        InvalidJson,
        InvalidSchema,
        InvalidManifestMediaType,
        InvalidDescriptorSize,
        TwoLayers
    }

    private sealed class BoundaryOciFixture : IDisposable
    {
        private BoundaryOciFixture(string root, string archive, OciExpectedImage expected)
            => (Root, Archive, Expected) = (root, archive, expected);

        internal string Root { get; }
        internal string Archive { get; }
        internal OciExpectedImage Expected { get; }
        internal const string PayloadPath = "etc/payload";

        internal static BoundaryOciFixture Create(BoundaryOciMutation mutation = BoundaryOciMutation.Valid)
        {
            var root = Path.Combine(Path.GetTempPath(), "oci-boundary-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var layer = Layer();
                var compressed = Gzip(layer);
                var layerDigest = Digest(compressed);
                var diffId = Digest(layer);
                var layerCount = mutation == BoundaryOciMutation.TwoLayers ? 2 : 1;
                var config = Config(Enumerable.Repeat(diffId, layerCount).ToArray());
                var configDigest = Digest(config);
                var mediaType = mutation == BoundaryOciMutation.InvalidManifestMediaType
                    ? "application/vnd.oci.image.manifest.v1+json+bad" : "application/vnd.oci.image.manifest.v1+json";
                var manifest = Manifest(configDigest, config.Length, layerDigest, compressed.Length, layerCount, mediaType,
                    mutation == BoundaryOciMutation.InvalidDescriptorSize);
                var manifestDigest = Digest(manifest);
                var index = Index(manifestDigest, manifest.Length, mutation == BoundaryOciMutation.InvalidSchema);
                if (mutation == BoundaryOciMutation.InvalidJson) index = Encoding.UTF8.GetBytes("{\"schemaVersion\":2,");

                var archive = Path.Combine(root, "boundary.oci.tar");
                using (var output = File.Create(archive))
                using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
                {
                    Entry(writer, "oci-layout", Encoding.UTF8.GetBytes("{\"imageLayoutVersion\":\"1.0.0\"}"));
                    Entry(writer, "index.json", index);
                    Entry(writer, "blobs/sha256/" + manifestDigest[7..], manifest);
                    Entry(writer, "blobs/sha256/" + configDigest[7..], config);
                    Entry(writer, "blobs/sha256/" + layerDigest[7..], compressed);
                }

                return new(root, archive, new(null, manifestDigest, "linux/amd64", configDigest, Enumerable.Repeat(diffId, layerCount).ToArray()));
            }
            catch
            {
                try { Directory.Delete(root, true); } catch { }
                throw;
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (DirectoryNotFoundException) { }
        }

        private static byte[] Layer()
        {
            using var output = new MemoryStream();
            using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "etc") { Mode = (UnixFileMode)493, Uid = 0, Gid = 0, ModificationTime = DateTimeOffset.UnixEpoch });
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, PayloadPath)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("payload\n")), Mode = (UnixFileMode)420, Uid = 0, Gid = 0, ModificationTime = DateTimeOffset.UnixEpoch
                });
            }
            return output.ToArray();
        }

        private static byte[] Config(IReadOnlyList<string> diffIds)
        {
            var ids = new JsonArray();
            foreach (var diffId in diffIds) ids.Add(diffId);
            return JsonBytes(new JsonObject
            {
                ["architecture"] = "amd64",
                ["os"] = "linux",
                ["config"] = new JsonObject { ["User"] = "0:0", ["Entrypoint"] = new JsonArray("/bin/true"), ["Env"] = new JsonArray("BOUNDARY=true") },
                ["rootfs"] = new JsonObject { ["type"] = "layers", ["diff_ids"] = ids }
            });
        }

        private static byte[] Manifest(string configDigest, int configSize, string layerDigest, int layerSize, int layerCount, string mediaType, bool badSize)
        {
            var layers = new JsonArray();
            for (var i = 0; i < layerCount; i++)
                layers.Add(new JsonObject { ["mediaType"] = "application/vnd.oci.image.layer.v1.tar+gzip", ["digest"] = layerDigest, ["size"] = badSize && i == 0 ? layerSize + 1 : layerSize });
            return JsonBytes(new JsonObject
            {
                ["schemaVersion"] = 2, ["mediaType"] = mediaType,
                ["config"] = new JsonObject { ["mediaType"] = "application/vnd.oci.image.config.v1+json", ["digest"] = configDigest, ["size"] = configSize },
                ["layers"] = layers
            });
        }

        private static byte[] Index(string manifestDigest, int manifestSize, bool badSchema) => JsonBytes(new JsonObject
            {
                ["schemaVersion"] = badSchema ? 1 : 2,
                ["manifests"] = new JsonArray(new JsonObject
                {
                    ["mediaType"] = "application/vnd.oci.image.manifest.v1+json", ["digest"] = manifestDigest, ["size"] = manifestSize,
                    ["platform"] = new JsonObject { ["os"] = "linux", ["architecture"] = "amd64" }
                })
            });

        private static byte[] JsonBytes(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString());
        private static byte[] Gzip(byte[] value) { using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(value); return output.ToArray(); }
        private static string Digest(byte[] value) => "sha256:" + Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
        private static void Entry(TarWriter writer, string path, byte[] value) => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(value), ModificationTime = DateTimeOffset.UnixEpoch });
    }
}
