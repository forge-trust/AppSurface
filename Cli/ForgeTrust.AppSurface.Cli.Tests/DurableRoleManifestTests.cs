using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Cli;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies strict, immutable and secret-safe parsing of durable role-manifest inputs.</summary>
public sealed class DurableRoleManifestTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [Fact]
    public void Parse_preserves_order_exact_names_profiles_and_hashes_original_bytes()
    {
        var json = "{ \"version\": 1, \"pairs\": ["
            + "{\"dispatcher\":\" Dispatch α \",\"runtime\":\"run A\",\"dispatcher_profile\":\"full\"},"
            + "{\"dispatcher\":\"source\",\"runtime\":\"run_B\",\"dispatcher_profile\":\"work_only\"} ] }";
        var bytes = StrictUtf8.GetBytes(json);

        var manifest = DurableRoleManifest.Parse(bytes);

        Assert.Equal(" Dispatch α ", manifest.Pairs[0].Dispatcher);
        Assert.Equal("run A", manifest.Pairs[0].Runtime);
        Assert.Equal("full", manifest.Pairs[0].DispatcherProfile);
        Assert.Equal("source", manifest.Pairs[1].Dispatcher);
        Assert.Equal("run_B", manifest.Pairs[1].Runtime);
        Assert.Equal("work_only", manifest.Pairs[1].DispatcherProfile);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), manifest.Sha256);
        Assert.NotEqual(DurableRoleManifest.Parse(StrictUtf8.GetBytes(json.Replace(" ", "", StringComparison.Ordinal))).Sha256, manifest.Sha256);
    }

    [Fact]
    public void Parse_does_not_retain_or_expose_mutable_input_bytes()
    {
        var bytes = StrictUtf8.GetBytes(Manifest("dispatcher", "runtime"));
        var manifest = DurableRoleManifest.Parse(bytes);
        var expectedHash = manifest.Sha256;
        Array.Fill(bytes, (byte)'x');

        Assert.Equal("dispatcher", manifest.Pairs[0].Dispatcher);
        Assert.Equal("runtime", manifest.Pairs[0].Runtime);
        Assert.Equal(expectedHash, manifest.Sha256);

        var changed = manifest.Pairs.SetItem(0, manifest.Pairs[0] with { Dispatcher = "changed" });
        Assert.Equal("dispatcher", manifest.Pairs[0].Dispatcher);
        Assert.Equal("changed", changed[0].Dispatcher);
    }

    [Theory]
    [InlineData("{\"version\":1,\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}]}")]
    [InlineData("{\"version\":1,\"ver\\u0073ion\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}]}")]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\",\"runtime\":\"r2\"}]}")]
    public void Parse_rejects_duplicate_decoded_properties(string json) => AssertInvalid(json, "duplicate");

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}]}")]
    [InlineData("{\"version\":1.0,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}]}")]
    [InlineData("{\"version\":1,\"pairs\":null}")]
    [InlineData("{\"version\":1,\"pairs\":[null]}")]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\"}]}")]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"other\"}]}")]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":1,\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}]}")]
    [InlineData("{\"version\":1,\"extra\":0,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}]}")]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\",\"extra\":0}]}")]
    public void Parse_rejects_wrong_root_pair_types_and_fields(string json) => AssertInvalid(json, "Manifest");

    [Fact]
    public void Parse_rejects_empty_and_over_limit_pair_counts()
    {
        AssertInvalid("{\"version\":1,\"pairs\":[]}", "1 and 32");
        var pairs = Enumerable.Range(0, 33)
            .Select(index => $"{{\"dispatcher\":\"d{index}\",\"runtime\":\"r{index}\",\"dispatcher_profile\":\"full\"}}");
        AssertInvalid($"{{\"version\":1,\"pairs\":[{string.Join(',', pairs)}]}}", "1 and 32");

        var maximumPairs = Enumerable.Range(0, DurableRoleManifest.MaximumPairs)
            .Select(index => $"{{\"dispatcher\":\"d{index}\",\"runtime\":\"r{index}\",\"dispatcher_profile\":\"full\"}}");
        Assert.Equal(DurableRoleManifest.MaximumPairs,
            DurableRoleManifest.Parse(StrictUtf8.GetBytes($"{{\"version\":1,\"pairs\":[{string.Join(',', maximumPairs)}]}}")).Pairs.Length);
    }

    [Fact]
    public void Parse_rejects_reused_role_names_within_a_pair() =>
        AssertInvalid(Manifest("same", "same"), "globally unique");

    [Fact]
    public void Parse_does_not_echo_invalid_role_text_in_diagnostics() =>
        AssertInvalid(Manifest("secret-marker\u0001", "runtime"), "role name is invalid");

    [Fact]
    public void Parse_rejects_duplicate_names_across_pairs_and_role_kinds()
    {
        AssertInvalid(
            "{\"version\":1,\"pairs\":["
            + "{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\"},"
            + "{\"dispatcher\":\"r\",\"runtime\":\"r2\",\"dispatcher_profile\":\"full\"}]}",
            "globally unique");
    }

    [Theory]
    [InlineData("")]
    [InlineData("\u0001")]
    [InlineData("a\u007f")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234")]
    [InlineData("éééééééééééééééééééééééééééééééé")]
    public void Parse_rejects_empty_control_or_more_than_63_utf8_byte_names(string role) =>
        AssertInvalid(Manifest(role, "runtime"), "role name is invalid");

    [Fact]
    public void Parse_accepts_names_at_63_utf8_bytes_and_rejects_more()
    {
        var boundary = new string('a', 61) + "é";
        Assert.Equal(boundary, DurableRoleManifest.Parse(StrictUtf8.GetBytes(Manifest(boundary, "runtime"))).Pairs[0].Dispatcher);
        AssertInvalid(Manifest(boundary + "a", "runtime"), "role name is invalid");
    }

    [Theory]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":\"\\uD800\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}]}")]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"\\uDC00\",\"dispatcher_profile\":\"full\"}]}")]
    public void Parse_rejects_unpaired_escaped_utf16_surrogates(string json) => AssertInvalid(json, "Unicode");

    [Fact]
    public void Parse_preserves_valid_surrogate_pairs_literal_escapes_and_case_distinct_roles()
    {
        var manifest = DurableRoleManifest.Parse(StrictUtf8.GetBytes(
            """{"version":1,"pairs":[{"dispatcher":"D\\uD800","runtime":"d\uD83D\uDE80","dispatcher_profile":"work_only"}]}"""));
        Assert.Equal("D\\uD800", manifest.Pairs[0].Dispatcher);
        Assert.Equal("d🚀", manifest.Pairs[0].Runtime);

        var caseDistinct = DurableRoleManifest.Parse(StrictUtf8.GetBytes(Manifest("Case", "case")));
        Assert.Equal("Case", caseDistinct.Pairs[0].Dispatcher);
        Assert.Equal("case", caseDistinct.Pairs[0].Runtime);
    }

    [Theory]
    [InlineData("{\"version\":null,\"pairs\":[]}")]
    [InlineData("{\"version\":\"1\",\"pairs\":[]}")]
    [InlineData("{\"version\":2147483648,\"pairs\":[]}")]
    [InlineData("{\"version\":1,\"pairs\":{}}")]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":null,\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}]}")]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":1}]}")]
    [InlineData("{\"version\":1,\"pairs\":[[]]}")]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\",\"runt\\u0069me\":\"r2\"}]}")]
    public void Parse_rejects_non_manifest_types_and_decoded_pair_duplicates(string json) =>
        AssertInvalid(json, "Manifest");

    [Theory]
    [InlineData("\\uD800x")]
    [InlineData("\\uD800\\u0041")]
    [InlineData("\\uD800\\uD800")]
    public void Parse_rejects_high_surrogates_without_a_low_surrogate(string escapedRole) =>
        AssertInvalid($"{{\"version\":1,\"pairs\":[{{\"dispatcher\":\"{escapedRole}\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}}]}}", "Unicode");

    [Fact]
    public void Parse_rejects_invalid_utf8_bom_comments_trailing_commas_and_malformed_json()
    {
        AssertInvalidBytes([0xC3, 0x28], "strict UTF-8");
        AssertInvalidBytes([0xEF, 0xBB, 0xBF, .. StrictUtf8.GetBytes(Manifest("d", "r"))], "byte-order mark");
        AssertInvalid("{/* comment */\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}]}", "strict UTF-8 JSON");
        AssertInvalid("{\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\",}]}", "strict UTF-8 JSON");
        AssertInvalid("not-json", "strict UTF-8 JSON");
    }

    [Fact]
    public void Parse_rejects_documents_deeper_than_eight_levels()
    {
        var nested = "0";
        for (var depth = 0; depth < 8; depth++)
        {
            nested = $"[{nested}]";
        }

        AssertInvalid($"{{\"version\":1,\"pairs\":[{{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\",\"nested\":{nested}}}]}}", "strict UTF-8 JSON");
    }

    [Fact]
    public void Parse_rejects_documents_over_65536_bytes()
    {
        var bytes = new byte[DurableRoleManifest.MaximumBytes + 1];
        AssertInvalidBytes(bytes, "65536-byte");
    }

    [Fact]
    public async Task ReadAsync_hashes_the_single_file_content_and_rejects_missing_or_oversized_files_safely()
    {
        using var directory = TestDirectory.Create();
        var path = Path.Join(directory.Path, "manifest.json");
        var bytes = StrictUtf8.GetBytes(Manifest("d", "r"));
        await File.WriteAllBytesAsync(path, bytes);

        var manifest = await DurableRoleManifest.ReadAsync(path, CancellationToken.None);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), manifest.Sha256);

        var exactLimitBytes = StrictUtf8.GetBytes(Manifest("d", "r"))
            .Concat(Enumerable.Repeat((byte)' ', DurableRoleManifest.MaximumBytes - StrictUtf8.GetByteCount(Manifest("d", "r"))))
            .ToArray();
        await File.WriteAllBytesAsync(path, exactLimitBytes);
        var exactLimit = await DurableRoleManifest.ReadAsync(path, CancellationToken.None);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(exactLimitBytes)).ToLowerInvariant(), exactLimit.Sha256);

        var missing = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await DurableRoleManifest.ReadAsync(Path.Join(directory.Path, "secret-path-marker"), CancellationToken.None));
        Assert.Equal("Manifest file could not be read.", missing.Message);
        Assert.DoesNotContain("secret-path-marker", missing.ToString(), StringComparison.Ordinal);

        var invalidPath = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await DurableRoleManifest.ReadAsync("invalid\0path-secret-marker", CancellationToken.None));
        Assert.Equal("Manifest file could not be read.", invalidPath.Message);
        Assert.DoesNotContain("path-secret-marker", invalidPath.ToString(), StringComparison.Ordinal);

        await File.WriteAllBytesAsync(path, new byte[DurableRoleManifest.MaximumBytes + 1]);
        var oversized = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await DurableRoleManifest.ReadAsync(path, CancellationToken.None));
        Assert.Equal("Manifest exceeds the 65536-byte limit.", oversized.Message);
    }

    [Fact]
    public async Task ReadAsync_propagates_cancellation()
    {
        using var directory = TestDirectory.Create();
        var path = Path.Join(directory.Path, "manifest.json");
        await File.WriteAllTextAsync(path, Manifest("d", "r"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DurableRoleManifest.ReadAsync(path, cancellation.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ReadAsync_rejects_missing_paths_with_a_fixed_diagnostic(string? path)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await DurableRoleManifest.ReadAsync(path!, CancellationToken.None));
        Assert.Equal("Manifest file could not be read.", error.Message);
    }

    [Fact]
    public async Task ReadAsync_rejects_a_directory_without_exposing_its_path()
    {
        using var directory = TestDirectory.Create();
        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await DurableRoleManifest.ReadAsync(directory.Path, CancellationToken.None));
        Assert.Equal("Manifest file could not be read.", error.Message);
        Assert.DoesNotContain(directory.Path, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_validates_owner_grammar_and_exact_disjointness()
    {
        var manifest = DurableRoleManifest.Parse(StrictUtf8.GetBytes(Manifest("Dispatcher", "runtime")));
        var request = new DurablePreflightRequest(manifest, "migration owner");

        Assert.Same(manifest, request.Manifest);
        Assert.Equal("migration owner", request.MigrationOwnerRole);
        Assert.Equal("dispatcher", new DurablePreflightRequest(manifest, "dispatcher").MigrationOwnerRole);
        Assert.Throws<ArgumentException>(() => new DurablePreflightRequest(manifest, "Dispatcher"));
        Assert.Throws<ArgumentException>(() => new DurablePreflightRequest(manifest, "runtime"));
        Assert.Throws<ArgumentException>(() => new DurablePreflightRequest(manifest, "bad\u0000owner"));
        Assert.Throws<ArgumentException>(() => new DurablePreflightRequest(manifest, new string('x', 64)));
        Assert.Throws<ArgumentException>(() => new DurablePreflightRequest(manifest, ""));
        Assert.Throws<ArgumentException>(() => new DurablePreflightRequest(manifest, "\uD800"));
        Assert.Throws<ArgumentNullException>(() => new DurablePreflightRequest(null!, "owner"));
        var boundaryOwner = new string('o', 63);
        Assert.Equal(boundaryOwner, new DurablePreflightRequest(manifest, boundaryOwner).MigrationOwnerRole);
    }

    [Fact]
    public void Unicode_escaped_names_and_literal_escape_sequences_are_preserved()
    {
        var valid = "{\"version\":1,\"pairs\":[{\"dispatcher\":\"\\uD83D\\uDE00\",\"runtime\":\"r\\\\uD800\",\"dispatcher_profile\":\"full\"}]}";
        var manifest = DurableRoleManifest.Parse(StrictUtf8.GetBytes(valid));
        Assert.Equal("😀", manifest.Pairs[0].Dispatcher);
        Assert.Equal(@"r\uD800", manifest.Pairs[0].Runtime);
        AssertInvalid("{\"version\":1,\"pairs\":[{\"dispatcher\":\"\\uD800x\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}]}", "Unicode");
        AssertInvalid("{\"version\":1,\"pairs\":[{\"dispatcher\":\"\\uD800\\u0041\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}]}", "Unicode");
        AssertInvalid("{\"version\":1,\"pairs\":[{\"dispatcher\":\"\\uZZZZ\",\"runtime\":\"r\",\"dispatcher_profile\":\"full\"}]}", "strict UTF-8 JSON");
    }

    [Theory]
    [InlineData("{\"version\":\"1\",\"pairs\":[]}")]
    [InlineData("{\"version\":null,\"pairs\":[]}")]
    [InlineData("{\"version\":1,\"pairs\":{}}")]
    [InlineData("{\"version\":1,\"pairs\":[[]]}")]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":null,\"dispatcher_profile\":\"full\"}]}")]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":true}]}")]
    [InlineData("{\"version\":1,\"pairs\":[{\"dispatcher\":\"d\",\"runtime\":\"r\",\"dispatcher_profile\":null}]}")]
    public void Shape_failures_do_not_expose_document_contents(string json) => AssertInvalid(json, "Manifest");

    [Fact]
    public async Task Directory_and_blank_paths_fail_safely_before_partial_content_can_escape()
    {
        using var directory = TestDirectory.Create();
        foreach (var path in new[] { directory.Path, "", " ", null! })
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
                await DurableRoleManifest.ReadAsync(path, CancellationToken.None));
            Assert.Equal("Manifest file could not be read.", exception.Message);
        }
    }

    private static string Manifest(string dispatcher, string runtime) =>
        $"{{\"version\":1,\"pairs\":[{{\"dispatcher\":{System.Text.Json.JsonSerializer.Serialize(dispatcher)},\"runtime\":{System.Text.Json.JsonSerializer.Serialize(runtime)},\"dispatcher_profile\":\"full\"}}]}}";

    private static void AssertInvalid(string json, string category) => AssertInvalidBytes(StrictUtf8.GetBytes(json), category);

    private static void AssertInvalidBytes(byte[] bytes, string category)
    {
        var error = Assert.Throws<ArgumentException>(() => DurableRoleManifest.Parse(bytes));
        Assert.Contains(category, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-marker", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TestDirectory Create() => new(System.IO.Directory.CreateTempSubdirectory("role-manifest-tests-").FullName);

        public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
    }
}
