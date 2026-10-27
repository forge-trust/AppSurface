using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    [InlineData(BoundaryOciMutation.InvalidLayout)]
    [InlineData(BoundaryOciMutation.InvalidConfigPlatform)]
    [InlineData(BoundaryOciMutation.InvalidConfigUnknown)]
    [InlineData(BoundaryOciMutation.InvalidConfigDiffId)]
    [InlineData(BoundaryOciMutation.InvalidManifestSchema)]
    [InlineData(BoundaryOciMutation.InvalidLayerMedia)]
    [InlineData(BoundaryOciMutation.InvalidLayerUnknown)]
    [InlineData(BoundaryOciMutation.InvalidConfigMedia)]
    [InlineData(BoundaryOciMutation.MissingSelectedManifest)]
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

    [Fact]
    public void Selected_file_mode_uid_gid_and_ancestor_contracts_are_enforced()
    {
        using var fixture = BoundaryOciFixture.Create();
        var hash = Digest(Encoding.UTF8.GetBytes("payload\n"));
        foreach (var expected in new[]
        {
            new OciExpectedFile(BoundaryOciFixture.PayloadPath, hash, Mode: 493),
            new OciExpectedFile(BoundaryOciFixture.PayloadPath, hash, Uid: 99),
            new OciExpectedFile(BoundaryOciFixture.PayloadPath, hash, Gid: 99),
            new OciExpectedFile("etc/payload/child", hash)
        })
        {
            Assert.Throws<InvalidDataException>(() => OciReader.ValidateExpectedFiles(
                new Dictionary<string, ObservedFile>
                {
                    ["etc"] = new("etc", "directory", "", 0, 493, 0, 0, null, 0),
                    [BoundaryOciFixture.PayloadPath] = new(BoundaryOciFixture.PayloadPath, "file", hash, 8, 420, 0, 0, null, 0)
                }, new Dictionary<string, OciExpectedFile> { [expected.Path] = expected }));
        }
    }

    [Fact]
    public void Selected_symlink_is_rejected_by_file_contract()
    {
        using var fixture = BoundaryOciFixture.Create();
        var hash = Digest(Encoding.UTF8.GetBytes("payload\n"));
        var observed = new Dictionary<string, ObservedFile>
        {
            ["etc"] = new("etc", "directory", "", 0, 493, 0, 0, null, 0),
            [BoundaryOciFixture.PayloadPath] = new(BoundaryOciFixture.PayloadPath, "symlink", "", 0, 511, 0, 0, "target", 0)
        };
        Assert.Throws<InvalidDataException>(() => OciReader.ValidateExpectedFiles(observed,
            new Dictionary<string, OciExpectedFile> { [BoundaryOciFixture.PayloadPath] = new(BoundaryOciFixture.PayloadPath, hash) }));
    }

    [Fact]
    public void Hashing_stream_reads_array_and_span_and_resets_hash()
    {
        var bytes = Encoding.UTF8.GetBytes("hash-me");
        using var input = new MemoryStream(bytes);
        using var stream = new OciReader.HashingStream(input, bytes.Length, new OciReader.Deadline(default, 1024));
        var first = new byte[4];
        Assert.Equal(4, stream.Read(first, 0, first.Length));
        Span<byte> second = stackalloc byte[3];
        Assert.Equal(3, stream.Read(second));
        Assert.Equal(SHA256.HashData(bytes), stream.Hash());
        Assert.Equal(SHA256.HashData([]), stream.Hash());
        Assert.Equal(bytes.Length, stream.BytesRead);
    }

    [Fact]
    public void Hashing_stream_rejects_cap_plus_one_and_unsupported_operations()
    {
        using var stream = new OciReader.HashingStream(new MemoryStream([1, 2, 3, 4]), 3, new OciReader.Deadline(default, 1024));
        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<InvalidDataException>(() => stream.Read(new byte[4], 0, 4));
        Assert.Throws<NotSupportedException>(() => _ = stream.Length);
        Assert.Throws<NotSupportedException>(() => _ = stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Flush());
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }

    [Fact]
    public void Hashing_stream_disposes_owned_hash_without_disposing_input()
    {
        using var input = new MemoryStream([1, 2, 3]);
        var stream = new OciReader.HashingStream(input, 3, new OciReader.Deadline(default, 1024));
        stream.ReadByte();
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Hash());
        Assert.Equal(2, input.ReadByte());
    }

    [Fact]
    public void Deadline_enforces_cancellation_duration_payload_and_metadata_bounds()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new OciReader.Deadline(cancelled.Token, 4).Check());
        Assert.Throws<TimeoutException>(() => new OciReader.Deadline(default, 4, TimeSpan.Zero).Check());
        var budget = new OciReader.Deadline(default, 4);
        Assert.Throws<InvalidDataException>(() => budget.ConsumePayload(5));
        var path = new string('x', 24 * 1024 * 1024);
        Assert.Throws<InvalidDataException>(() => new OciReader.Deadline(default, 4).AccountMetadata(path));
    }

    [Theory]
    [InlineData(BoundaryOciMutation.UserNull)]
    [InlineData(BoundaryOciMutation.UserAbsent)]
    [InlineData(BoundaryOciMutation.EntrypointNull)]
    [InlineData(BoundaryOciMutation.EntrypointAbsent)]
    [InlineData(BoundaryOciMutation.EnvironmentNull)]
    [InlineData(BoundaryOciMutation.EnvironmentAbsent)]
    [InlineData(BoundaryOciMutation.ExposedPortsObject)]
    public void Optional_config_fields_are_read_at_the_real_oci_boundary(BoundaryOciMutation mutation)
    {
        using var fixture = BoundaryOciFixture.Create(mutation);
        var observed = new OciReader().Read(fixture.Archive, fixture.Expected);
        Assert.Equal(mutation is BoundaryOciMutation.UserNull or BoundaryOciMutation.UserAbsent ? "" : "0:0", observed.ConfigUser);
        Assert.Equal(mutation is BoundaryOciMutation.EntrypointNull or BoundaryOciMutation.EntrypointAbsent ? Array.Empty<string>() : ["/bin/true"], observed.ConfigEntrypoint.ToArray());
        Assert.Equal(mutation is BoundaryOciMutation.EnvironmentNull or BoundaryOciMutation.EnvironmentAbsent ? Array.Empty<string>() : new[] { "BOUNDARY=true" }, observed.ConfigEnvironment.Select(x => $"{x.Key}={x.Value}"));
        Assert.Equal(mutation == BoundaryOciMutation.ExposedPortsObject ? new[] { "8080/tcp" } : [], observed.ConfigPorts.ToArray());
    }

    [Theory]
    [InlineData(BoundaryOciMutation.EnvironmentNullEntry)]
    [InlineData(BoundaryOciMutation.EnvironmentMalformed)]
    [InlineData(BoundaryOciMutation.ExposedPortsArray)]
    public void Malformed_config_fields_are_rejected_at_the_real_oci_boundary(BoundaryOciMutation mutation)
    {
        using var fixture = BoundaryOciFixture.Create(mutation);
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected));
    }

    [Theory]
    [InlineData("etc/file", "etc/file")]
    [InlineData("unicode/é", "unicode/é")]
    public void SafePath_accepts_canonical_paths(string input, string expected)
        => Assert.Equal(expected, OciReader.SafePath(input));

    [Theory]
    [InlineData("")]
    [InlineData("/absolute")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("unicode/e\u0301")]
    public void SafePath_rejects_unsafe_or_aliasing_paths(string path)
        => Assert.Throws<InvalidDataException>(() => OciReader.SafePath(path));

    [Fact]
    public void ReadEnvironment_returns_empty_for_absent_or_null()
    {
        using var absent = JsonDocument.Parse("{}");
        using var nullValue = JsonDocument.Parse("{\"Env\":null}");
        Assert.Empty(OciReader.ReadEnvironment(absent.RootElement));
        Assert.Empty(OciReader.ReadEnvironment(nullValue.RootElement));
    }

    [Theory]
    [InlineData("{\"Env\":\"bad\"}")]
    [InlineData("{\"Env\":[null]}")]
    [InlineData("{\"Env\":[\"NO_EQUALS\"]}")]
    [InlineData("{\"Env\":[\"=empty-name\"]}")]
    public void ReadEnvironment_rejects_wrong_shape_and_malformed_entries(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => OciReader.ReadEnvironment(document.RootElement));
    }

    [Fact]
    public void RemoveLower_named_whiteout_removes_only_matching_subtree()
    {
        var merged = Merged("etc/old", "etc/old/child", "etc/keep", "other/file");
        OciReader.RemoveLower(merged, "etc/old", 1);
        Assert.DoesNotContain("etc/old", merged.Keys);
        Assert.DoesNotContain("etc/old/child", merged.Keys);
        Assert.Contains("etc/keep", merged.Keys);
        Assert.Contains("other/file", merged.Keys);
    }

    [Fact]
    public void RemoveLower_opaque_whiteout_removes_all_lower_layer_paths()
    {
        var merged = Merged("etc/old", "etc/keep", "other/file");
        OciReader.RemoveLower(merged, "", 1);
        Assert.Empty(merged);
    }

    [Fact]
    public void RemoveLower_does_not_remove_same_or_upper_layer_paths()
    {
        var merged = Merged("etc/lower", "etc/upper");
        merged["etc/lower"] = merged["etc/lower"] with { Layer = 2 };
        merged["etc/upper"] = merged["etc/upper"] with { Layer = 3 };
        OciReader.RemoveLower(merged, "etc", 2);
        Assert.Contains("etc/lower", merged.Keys);
        Assert.Contains("etc/upper", merged.Keys);
    }

    [Fact]
    public void RemovePath_removes_named_path_and_descendants_only()
    {
        var merged = Merged("etc/file", "etc/file/child", "etc/other");
        OciReader.RemovePath(merged, "etc/file");
        Assert.DoesNotContain("etc/file", merged.Keys);
        Assert.DoesNotContain("etc/file/child", merged.Keys);
        Assert.Contains("etc/other", merged.Keys);
    }

    [Fact]
    public void SelectionState_publishes_exact_selected_bytes()
    {
        using var root = new TempDirectory();
        var output = Path.Combine(root.Path, "selected");
        var state = OciReader.SelectionState.Create(new OciReadSelection(new HashSet<string>(["etc/payload"], StringComparer.Ordinal), output), new OciReaderLimits());
        var content = Encoding.UTF8.GetBytes("payload\n");
        var staged = state.CapturePath("etc/payload")!;
        File.WriteAllBytes(staged, content);
        var hash = Digest(content);
        state.Publish(new Dictionary<string, ObservedFile>
        {
            ["etc/payload"] = new("etc/payload", "file", hash, content.Length, 420, 0, 0, null, 0)
        });

        Assert.Equal(content, File.ReadAllBytes(TestPathUtils.PathUnder(output, "etc/payload")));
        state.Cleanup();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("hash")]
    public void SelectionState_rejects_incomplete_publish_and_cleans_staging(string failure)
    {
        using var root = new TempDirectory();
        var output = Path.Combine(root.Path, "selected");
        var state = OciReader.SelectionState.Create(new OciReadSelection(new HashSet<string>(["etc/payload"], StringComparer.Ordinal), output), new OciReaderLimits());
        var content = Encoding.UTF8.GetBytes("payload\n");
        var staged = state.CapturePath("etc/payload")!;
        File.WriteAllBytes(staged, content);
        var type = failure == "directory" ? "directory" : "file";
        var hash = failure == "hash" ? Digest(Encoding.UTF8.GetBytes("wrong")) : Digest(content);
        var observed = failure == "missing" ? new Dictionary<string, ObservedFile>() : new Dictionary<string, ObservedFile>
        {
            ["etc/payload"] = new("etc/payload", type, hash, content.Length, 420, 0, 0, null, 0)
        };

        Assert.Throws<InvalidDataException>(() => state.Publish(observed));
        Assert.False(Directory.Exists(output));
        state.Cleanup();
    }

    [Fact]
    public void SelectionState_remove_is_idempotent_and_deletes_staged_bytes()
    {
        using var root = new TempDirectory();
        var state = OciReader.SelectionState.Create(new OciReadSelection(new HashSet<string>(["etc/payload"], StringComparer.Ordinal), Path.Combine(root.Path, "selected")), new OciReaderLimits());
        var staged = state.CapturePath("etc/payload")!;
        Assert.True(File.Exists(staged));
        state.Remove("etc/payload");
        state.Remove("etc/payload");
        Assert.False(File.Exists(staged));
        state.Cleanup();
    }

    [Fact]
    public void SelectionState_selected_output_budget_accepts_exact_cap_and_rejects_one_over()
    {
        using var root = new TempDirectory();
        var state = OciReader.SelectionState.Create(new OciReadSelection(new HashSet<string>(["etc/payload"], StringComparer.Ordinal), Path.Combine(root.Path, "selected")), new OciReaderLimits(MaxSelectedOutputBytes: 3));
        state.ConsumeSelected(3);
        Assert.Throws<InvalidDataException>(() => state.ConsumeSelected(1));
        state.Cleanup();
    }

    [Fact]
    public void SelectionState_rejects_preexisting_output_before_staging()
    {
        using var root = new TempDirectory();
        var output = Path.Combine(root.Path, "selected");
        Directory.CreateDirectory(output);
        Assert.Throws<InvalidDataException>(() => OciReader.SelectionState.Create(new OciReadSelection(new HashSet<string>(["etc/payload"], StringComparer.Ordinal), output), new OciReaderLimits()));
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oci-selection-tests-" + Guid.NewGuid().ToString("N"));
        internal TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static SortedDictionary<string, ObservedFile> Merged(params string[] paths)
        => new(paths.ToDictionary(path => path, path => new ObservedFile(path, "file", "", 0, 420, 0, 0, null, 0), StringComparer.Ordinal), StringComparer.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_or_blank_archives_are_rejected_before_graph_work(string archive)
    {
        using var fixture = BoundaryOciFixture.Create();
        Assert.Throws<ArgumentNullException>(() => new OciReader().Read(archive, fixture.Expected));
    }

    [Fact]
    public void Index_manifest_and_config_identity_mismatches_are_rejected()
    {
        using var fixture = BoundaryOciFixture.Create();
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive,
            fixture.Expected with { IndexDigest = Digest(Encoding.UTF8.GetBytes("wrong-index")) }));
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive,
            fixture.Expected with { ManifestDigest = "sha256:" + new string('a', 64) }));
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive,
            fixture.Expected with { ConfigDigest = "sha256:" + new string('b', 64) }));
    }

    [Theory]
    [InlineData("total-members")]
    [InlineData("paths")]
    [InlineData("expanded-total")]
    [InlineData("payload")]
    [InlineData("json")]
    public void Real_read_enforces_archive_and_expansion_limits(string limit)
    {
        using var fixture = BoundaryOciFixture.Create();
        var limits = limit switch
        {
            "total-members" => new OciReaderLimits(MaxTotalMembers: 1),
            "paths" => new OciReaderLimits(MaxPaths: 1),
            "expanded-total" => new OciReaderLimits(MaxExpandedTotalBytes: 1),
            "payload" => new OciReaderLimits(MaxPayloadBytes: 1, MaxPayloadAggregateBytes: 1),
            _ => new OciReaderLimits(MaxJsonBytes: 20)
        };
        var expected = limit == "payload" ? fixture.Expected with { PayloadPaths = new HashSet<string>([BoundaryOciFixture.PayloadPath], StringComparer.Ordinal) } : fixture.Expected;
        Assert.Throws<InvalidDataException>(() => new OciReader(limits).Read(fixture.Archive, expected));
    }

    [Fact]
    public void Config_user_expectation_mismatch_is_rejected_at_real_read_boundary()
    {
        using var fixture = BoundaryOciFixture.Create();
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected with { ConfigUser = "1000" }));
    }

    [Fact]
    public void Missing_archive_is_rejected_by_the_bounded_file_check()
    {
        using var fixture = BoundaryOciFixture.Create();
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(Path.Combine(fixture.Root, "missing.oci.tar"), fixture.Expected));
    }

    [Theory]
    [InlineData(BoundaryOciMutation.EntrypointObject)]
    [InlineData(BoundaryOciMutation.EntrypointNullEntry)]
    public void Entrypoint_shape_and_element_types_are_rejected_at_real_read_boundary(BoundaryOciMutation mutation)
    {
        using var fixture = BoundaryOciFixture.Create(mutation);
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected));
    }

    [Theory]
    [InlineData(BoundaryOciMutation.UnsupportedLayerNode)]
    [InlineData(BoundaryOciMutation.BlobOverDescriptor)]
    [InlineData(BoundaryOciMutation.BlobDigestMismatch)]
    public void Real_layer_bytes_reject_unsupported_nodes_and_blob_integrity_failures(BoundaryOciMutation mutation)
    {
        using var fixture = BoundaryOciFixture.Create(mutation);
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected));
    }

    [Fact]
    public void Index_manifest_descriptor_media_type_is_verified_separately_from_manifest_media_type()
    {
        using var fixture = BoundaryOciFixture.Create(BoundaryOciMutation.InvalidIndexManifestMediaType);
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected));
    }

    [Theory]
    [InlineData(BoundaryOciMutation.OuterDuplicatePath)]
    [InlineData(BoundaryOciMutation.OuterUnsupportedNode)]
    [InlineData(BoundaryOciMutation.OuterUnknownPath)]
    [InlineData(BoundaryOciMutation.InvalidBlobShort)]
    [InlineData(BoundaryOciMutation.InvalidBlobNonHex)]
    [InlineData(BoundaryOciMutation.InvalidBlobUppercase)]
    [InlineData(BoundaryOciMutation.DuplicateExpectedManifest)]
    [InlineData(BoundaryOciMutation.MalformedWhiteoutRoot)]
    [InlineData(BoundaryOciMutation.MalformedWhiteoutNested)]
    [InlineData(BoundaryOciMutation.MalformedWhiteoutNamed)]
    public void Real_archive_graph_mutations_are_rejected_without_publishing(BoundaryOciMutation mutation)
    {
        using var fixture = BoundaryOciFixture.Create(mutation);
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(fixture.Archive, fixture.Expected));
    }

    [Fact]
    public void Oversized_json_and_compressed_blob_are_rejected_at_real_read_boundary()
    {
        using var json = BoundaryOciFixture.Create(BoundaryOciMutation.OuterJsonOversized);
        Assert.Throws<InvalidDataException>(() => new OciReader().Read(json.Archive, json.Expected));

        using var compressed = BoundaryOciFixture.Create();
        Assert.Throws<InvalidDataException>(() => new OciReader(new OciReaderLimits(MaxCompressedBlobBytes: 1)).Read(compressed.Archive, compressed.Expected));
    }

    [Theory]
    [InlineData(BoundaryOciMutation.PositiveHardlink)]
    [InlineData(BoundaryOciMutation.PositiveSymlink)]
    [InlineData(BoundaryOciMutation.PositiveV7RegularFile)]
    public void Supported_link_and_v7_nodes_are_materialized(BoundaryOciMutation mutation)
    {
        using var fixture = BoundaryOciFixture.Create(mutation);
        var observed = new OciReader().Read(fixture.Archive, fixture.Expected);
        Assert.Contains(observed.Entries, entry => entry.Path == BoundaryOciFixture.PayloadPath && entry.Type == "file");
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("short")]
    [InlineData("growing")]
    [InlineData("wrong-digest")]
    [InlineData("cancelled")]
    public void Spool_enforces_exact_bytes_digest_and_deadline(string caseName)
    {
        using var fixture = BoundaryOciFixture.Create();
        var source = Encoding.UTF8.GetBytes("spooled-bytes");
        var input = caseName == "short" ? source[..^1] : caseName == "growing" ? source.Concat(new byte[] { 0xFF }).ToArray() : source;
        using var cancellation = new CancellationTokenSource();
        if (caseName == "cancelled") cancellation.Cancel();
        var digest = caseName == "wrong-digest" ? Digest(Encoding.UTF8.GetBytes("different")) : Digest(source);
        var expected = source.Length;
        using var stream = new MemoryStream(input);

        if (caseName == "exact")
        {
            OciReader.Spool(stream, Path.Combine(fixture.Root, "spooled"), expected, digest, new OciReader.Deadline(cancellation.Token, 1024));
            Assert.Equal(source, File.ReadAllBytes(Path.Combine(fixture.Root, "spooled")));
        }
        else if (caseName == "cancelled")
            Assert.Throws<OperationCanceledException>(() => { OciReader.Spool(stream, Path.Combine(fixture.Root, caseName), expected, digest, new OciReader.Deadline(cancellation.Token, 1024)); });
        else
            Assert.Throws<InvalidDataException>(() => { OciReader.Spool(stream, Path.Combine(fixture.Root, caseName), expected, digest, new OciReader.Deadline(cancellation.Token, 1024)); });
    }

    [Theory]
    [InlineData("short")]
    [InlineData("growing")]
    [InlineData("cancelled")]
    public void ReadBounded_enforces_exact_length_and_deadline(string caseName)
    {
        var source = Encoding.UTF8.GetBytes("bounded-bytes");
        var input = caseName == "short" ? source[..^1] : caseName == "growing" ? source.Concat(new byte[] { 0xFF }).ToArray() : source;
        using var cancellation = new CancellationTokenSource();
        if (caseName == "cancelled") cancellation.Cancel();
        using var stream = new MemoryStream(input);

        if (caseName == "short")
            Assert.Throws<EndOfStreamException>(() => OciReader.ReadBounded(stream, source.Length, new OciReader.Deadline(cancellation.Token, 1024)));
        else if (caseName == "growing")
            Assert.Throws<InvalidDataException>(() => OciReader.ReadBounded(stream, source.Length, new OciReader.Deadline(cancellation.Token, 1024)));
        else
            Assert.Throws<OperationCanceledException>(() => OciReader.ReadBounded(stream, source.Length, new OciReader.Deadline(cancellation.Token, 1024)));
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
        TwoLayers,
        InvalidLayout,
        InvalidConfigPlatform,
        InvalidConfigUnknown,
        InvalidConfigDiffId,
        InvalidManifestSchema,
        InvalidLayerMedia,
        InvalidLayerUnknown,
        InvalidConfigMedia,
        MissingSelectedManifest,
        InvalidIndexManifestMediaType,
        OuterDuplicatePath,
        OuterUnsupportedNode,
        OuterUnknownPath,
        InvalidBlobShort,
        InvalidBlobNonHex,
        InvalidBlobUppercase,
        OuterJsonOversized,
        DuplicateExpectedManifest,
        PositiveHardlink,
        PositiveSymlink,
        PositiveV7RegularFile,
        UnsupportedLayerNode,
        BlobOverDescriptor,
        BlobDigestMismatch,
        MalformedWhiteoutRoot,
        MalformedWhiteoutNested,
        MalformedWhiteoutNamed,
        UserNull,
        UserAbsent,
        EntrypointNull,
        EntrypointAbsent,
        EntrypointObject,
        EntrypointNullEntry,
        EnvironmentNull,
        EnvironmentAbsent,
        EnvironmentNullEntry,
        EnvironmentMalformed,
        ExposedPortsObject,
        ExposedPortsArray
    }

    private sealed class BoundaryOciFixture : IDisposable
    {
        private BoundaryOciFixture(string root, string archive, OciExpectedImage expected)
        {
            (Root, Archive, Expected) = (root, archive, expected);
        }

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
                var layer = Layer(mutation);
                var compressed = Gzip(layer);
                var layerDigest = Digest(compressed);
                var diffId = Digest(layer);
                var layerCount = mutation == BoundaryOciMutation.TwoLayers ? 2 : 1;
                var config = Config(Enumerable.Repeat(diffId, layerCount).ToArray(), mutation);
                var configNode = JsonNode.Parse(config)!.AsObject();
                if (mutation == BoundaryOciMutation.InvalidConfigPlatform) configNode["architecture"] = "arm64";
                if (mutation == BoundaryOciMutation.InvalidConfigUnknown) configNode["unrecognized"] = true;
                if (mutation == BoundaryOciMutation.InvalidConfigDiffId) configNode["rootfs"]!["diff_ids"]![0] = "sha256:" + new string('0', 64);
                config = JsonBytes(configNode);
                var configDigest = Digest(config);
                var mediaType = mutation == BoundaryOciMutation.InvalidManifestMediaType
                    ? "application/vnd.oci.image.manifest.v1+json+bad" : "application/vnd.oci.image.manifest.v1+json";
                var manifest = Manifest(configDigest, config.Length, layerDigest, compressed.Length, layerCount, mediaType,
                    mutation == BoundaryOciMutation.InvalidDescriptorSize);
                var manifestNode = JsonNode.Parse(manifest)!.AsObject();
                if (mutation == BoundaryOciMutation.InvalidManifestSchema) manifestNode["schemaVersion"] = 1;
                if (mutation == BoundaryOciMutation.InvalidLayerMedia) manifestNode["layers"]![0]!["mediaType"] = "unsupported";
                if (mutation == BoundaryOciMutation.InvalidLayerUnknown) manifestNode["layers"]![0]!["unknown"] = true;
                if (mutation == BoundaryOciMutation.InvalidConfigMedia) manifestNode["config"]!["mediaType"] = "unsupported";
                manifest = JsonBytes(manifestNode);
                var manifestDigest = Digest(manifest);
                var index = Index(manifestDigest, manifest.Length, mutation == BoundaryOciMutation.InvalidSchema);
                if (mutation == BoundaryOciMutation.InvalidIndexManifestMediaType)
                {
                    var indexNode = JsonNode.Parse(index)!.AsObject();
                    indexNode["manifests"]![0]!["mediaType"] = "unsupported";
                    index = JsonBytes(indexNode);
                }
                if (mutation == BoundaryOciMutation.DuplicateExpectedManifest)
                {
                    var indexNode = JsonNode.Parse(index)!.AsObject();
                    indexNode["manifests"]!.AsArray().Add(new JsonObject
                    {
                        ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
                        ["digest"] = manifestDigest,
                        ["size"] = manifest.Length,
                        ["platform"] = new JsonObject { ["os"] = "linux", ["architecture"] = "amd64" }
                    });
                    index = JsonBytes(indexNode);
                }
                if (mutation == BoundaryOciMutation.MissingSelectedManifest)
                {
                    var indexNode = JsonNode.Parse(index)!.AsObject(); indexNode["manifests"] = new JsonArray(); index = JsonBytes(indexNode);
                }
                if (mutation == BoundaryOciMutation.InvalidJson) index = Encoding.UTF8.GetBytes("{\"schemaVersion\":2,");
                if (mutation == BoundaryOciMutation.OuterJsonOversized)
                    index = index.Concat(new byte[Math.Max(0, 1_048_577 - index.Length)]).ToArray();

                var archive = Path.Combine(root, "boundary.oci.tar");
                using (var output = File.Create(archive))
                using (var writer = new TarWriter(output, mutation == BoundaryOciMutation.PositiveV7RegularFile ? TarEntryFormat.V7 : TarEntryFormat.Pax, leaveOpen: true))
                {
                    Action<TarWriter, string, byte[]> entry = mutation == BoundaryOciMutation.PositiveV7RegularFile ? V7Entry : Entry;
                    entry(writer, "oci-layout", Encoding.UTF8.GetBytes(mutation == BoundaryOciMutation.InvalidLayout ? "{\"imageLayoutVersion\":\"2.0.0\"}" : "{\"imageLayoutVersion\":\"1.0.0\"}"));
                    entry(writer, "index.json", index);
                    var manifestPath = "blobs/sha256/" + manifestDigest[7..];
                    var configPath = "blobs/sha256/" + configDigest[7..];
                    var layerPath = "blobs/sha256/" + layerDigest[7..];
                    if (mutation == BoundaryOciMutation.InvalidBlobShort) layerPath = layerPath[..^1];
                    if (mutation == BoundaryOciMutation.InvalidBlobNonHex) layerPath = layerPath[..^1] + "g";
                    if (mutation == BoundaryOciMutation.InvalidBlobUppercase)
                    {
                        var hexLetter = 13 + layerPath[13..].IndexOfAny("abcdef".ToCharArray());
                        layerPath = layerPath[..hexLetter] + char.ToUpperInvariant(layerPath[hexLetter]) + layerPath[(hexLetter + 1)..];
                    }
                    entry(writer, manifestPath, manifest);
                    entry(writer, configPath, config);
                    var archiveLayer = mutation == BoundaryOciMutation.BlobOverDescriptor
                        ? compressed.Concat(new byte[] { 0 }).ToArray()
                        : mutation == BoundaryOciMutation.BlobDigestMismatch
                            ? compressed.Select((value, i) => i == 0 ? (byte)(value ^ 0xFF) : value).ToArray()
                            : compressed;
                    entry(writer, layerPath, archiveLayer);
                    if (mutation == BoundaryOciMutation.OuterDuplicatePath) Entry(writer, "index.json", index);
                    if (mutation == BoundaryOciMutation.OuterUnknownPath) Entry(writer, "unexpected", Array.Empty<byte>());
                    if (mutation == BoundaryOciMutation.OuterUnsupportedNode)
                        writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "unsupported") { LinkName = "index.json", ModificationTime = DateTimeOffset.UnixEpoch });
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

        private static byte[] Layer(BoundaryOciMutation mutation)
        {
            using var output = new MemoryStream();
            using (var writer = new TarWriter(output, mutation == BoundaryOciMutation.PositiveV7RegularFile ? TarEntryFormat.V7 : TarEntryFormat.Pax, leaveOpen: true))
            {
                TarEntry directory = mutation == BoundaryOciMutation.PositiveV7RegularFile
                    ? new V7TarEntry(TarEntryType.Directory, "etc")
                    : new PaxTarEntry(TarEntryType.Directory, "etc");
                directory.Mode = (UnixFileMode)493; directory.Uid = 0; directory.Gid = 0; directory.ModificationTime = DateTimeOffset.UnixEpoch;
                writer.WriteEntry(directory);
                TarEntry payload = mutation == BoundaryOciMutation.PositiveV7RegularFile
                    ? new V7TarEntry(TarEntryType.V7RegularFile, PayloadPath)
                    : new PaxTarEntry(TarEntryType.RegularFile, PayloadPath);
                payload.DataStream = new MemoryStream(Encoding.UTF8.GetBytes("payload\n"));
                payload.Mode = (UnixFileMode)420; payload.Uid = 0; payload.Gid = 0; payload.ModificationTime = DateTimeOffset.UnixEpoch;
                writer.WriteEntry(payload);
                if (mutation == BoundaryOciMutation.PositiveHardlink)
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "etc/hardlink") { LinkName = PayloadPath, Mode = (UnixFileMode)420, Uid = 0, Gid = 0, ModificationTime = DateTimeOffset.UnixEpoch });
                if (mutation == BoundaryOciMutation.PositiveSymlink)
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "etc/symlink") { LinkName = PayloadPath, Mode = (UnixFileMode)511, Uid = 0, Gid = 0, ModificationTime = DateTimeOffset.UnixEpoch });
                if (mutation == BoundaryOciMutation.MalformedWhiteoutRoot)
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, ".wh.") { DataStream = new MemoryStream(), ModificationTime = DateTimeOffset.UnixEpoch });
                if (mutation == BoundaryOciMutation.MalformedWhiteoutNested)
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "etc/.wh.") { DataStream = new MemoryStream(), ModificationTime = DateTimeOffset.UnixEpoch });
                if (mutation == BoundaryOciMutation.MalformedWhiteoutNamed)
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "etc/.wh.") { DataStream = new MemoryStream(), ModificationTime = DateTimeOffset.UnixEpoch });
                if (mutation == BoundaryOciMutation.UnsupportedLayerNode)
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.Fifo, "etc/fifo") { ModificationTime = DateTimeOffset.UnixEpoch });
            }
            return output.ToArray();
        }

        private static byte[] Config(IReadOnlyList<string> diffIds, BoundaryOciMutation mutation)
        {
            var ids = new JsonArray();
            foreach (var diffId in diffIds) ids.Add(diffId);
            var config = new JsonObject
            {
                ["architecture"] = "amd64",
                ["os"] = "linux",
                ["config"] = new JsonObject { ["User"] = "0:0", ["Entrypoint"] = new JsonArray("/bin/true"), ["Env"] = new JsonArray("BOUNDARY=true") },
                ["rootfs"] = new JsonObject { ["type"] = "layers", ["diff_ids"] = ids }
            };
            var inner = config["config"]!.AsObject();
            switch (mutation)
            {
                case BoundaryOciMutation.UserNull: inner["User"] = null; break;
                case BoundaryOciMutation.UserAbsent: inner.Remove("User"); break;
                case BoundaryOciMutation.EntrypointNull: inner["Entrypoint"] = null; break;
                case BoundaryOciMutation.EntrypointAbsent: inner.Remove("Entrypoint"); break;
                case BoundaryOciMutation.EntrypointObject: inner["Entrypoint"] = new JsonObject(); break;
                case BoundaryOciMutation.EntrypointNullEntry: inner["Entrypoint"] = new JsonArray { null }; break;
                case BoundaryOciMutation.EnvironmentNull: inner["Env"] = null; break;
                case BoundaryOciMutation.EnvironmentAbsent: inner.Remove("Env"); break;
                case BoundaryOciMutation.EnvironmentNullEntry: inner["Env"] = new JsonArray { null }; break;
                case BoundaryOciMutation.EnvironmentMalformed: inner["Env"] = new JsonArray("MALFORMED"); break;
                case BoundaryOciMutation.ExposedPortsObject: inner["ExposedPorts"] = new JsonObject { ["8080/tcp"] = null }; break;
                case BoundaryOciMutation.ExposedPortsArray: inner["ExposedPorts"] = new JsonArray("8080/tcp"); break;
            }

            return JsonBytes(config);
        }

        private static byte[] Manifest(string configDigest, int configSize, string layerDigest, int layerSize, int layerCount, string mediaType, bool badSize)
        {
            var layers = new JsonArray();
            for (var i = 0; i < layerCount; i++)
                layers.Add(new JsonObject { ["mediaType"] = "application/vnd.oci.image.layer.v1.tar+gzip", ["digest"] = layerDigest, ["size"] = badSize && i == 0 ? layerSize + 1 : layerSize });
            return JsonBytes(new JsonObject
            {
                ["schemaVersion"] = 2,
                ["mediaType"] = mediaType,
                ["config"] = new JsonObject { ["mediaType"] = "application/vnd.oci.image.config.v1+json", ["digest"] = configDigest, ["size"] = configSize },
                ["layers"] = layers
            });
        }

        private static byte[] Index(string manifestDigest, int manifestSize, bool badSchema) => JsonBytes(new JsonObject
        {
            ["schemaVersion"] = badSchema ? 1 : 2,
            ["manifests"] = new JsonArray(new JsonObject
            {
                ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
                ["digest"] = manifestDigest,
                ["size"] = manifestSize,
                ["platform"] = new JsonObject { ["os"] = "linux", ["architecture"] = "amd64" }
            })
        });

        private static byte[] JsonBytes(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString());
        private static byte[] Gzip(byte[] value) { using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(value); return output.ToArray(); }
        private static string Digest(byte[] value) => "sha256:" + Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
        private static void Entry(TarWriter writer, string path, byte[] value) => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(value), ModificationTime = DateTimeOffset.UnixEpoch });
        private static void V7Entry(TarWriter writer, string path, byte[] value) => writer.WriteEntry(new V7TarEntry(TarEntryType.V7RegularFile, path) { DataStream = new MemoryStream(value), ModificationTime = DateTimeOffset.UnixEpoch });
    }
}
