using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceCoverageFanInTests
{
    [Fact]
    public async Task VerifyAsync_requires_explicit_trusted_identity_and_inventory()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => EvidenceCoverageFanIn.VerifyAsync(new EvidenceCoverageFanInRequest(
            "primary", ["linux", "windows", "darwin"], "output", "repo", "solution.slnx", new HashSet<string>(), "commit", "tree", "sdk", "Debug", new Dictionary<string, string?>(), "primary-gate.json", "composed-gate.json", "diff")).AsTask());

        Assert.Contains("inventory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_accepts_complete_synthetic_bundle_with_different_attested_roots()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();

        var result = await fixture.VerifyAsync();

        Assert.True(result.Verified);
        Assert.Equal(4, result.InputDirectories.Count);
        Assert.Equal(8, result.InputMethodLines);
        Assert.Equal(2, result.RetainedMethodLines);
    }

    [Fact]
    public async Task VerifyAsync_rejects_internal_entities_without_invoking_any_external_process()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Primary, "coverage.cobertura.xml"), "<!DOCTYPE coverage [<!ENTITY x 'bad'>]><coverage>&x;</coverage>");
        var manifestPath = Path.Combine(fixture.Primary, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["files"]!.AsObject()["coverage.cobertura.xml"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(fixture.Primary, "coverage.cobertura.xml")))).ToLowerInvariant();
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.True(exception.Message.Contains("entity", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_rejects_tampered_manifest_checksum_and_wrong_native_filter()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var manifestPath = Path.Combine(fixture.Native[0], "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["filter"] = "wrong";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("filter", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("commit", "different commit")]
    [InlineData("sdk", "different sdk")]
    [InlineData("configuration", "Release")]
    public async Task VerifyAsync_rejects_native_identity_mismatches(string property, string value)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await fixture.MutateNativeManifestAsync(property, value);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("mismatch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_rejects_forged_passed_gate_with_failing_percentages()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var gate = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ComposedGate))!.AsObject();
        gate["patchLine"]!["percent"] = 0;
        await File.WriteAllTextAsync(fixture.ComposedGate, gate.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("numerical", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("sourceRoot")]
    public async Task VerifyAsync_rejects_primary_manifest_trust_mutations(string property)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await fixture.MutateManifestAsync(fixture.Primary, manifest =>
        {
            if (property == "run")
                manifest["run"]!.AsObject()["GITHUB_RUN_ID"] = "2";
            else
                manifest[property] = "relative/root";
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains(property == "run" ? "provenance" : "identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("include")]
    [InlineData("extra")]
    public async Task VerifyAsync_requires_the_exact_original_manifest_policy(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await fixture.MutateManifestAsync(fixture.Native[0], manifest =>
        {
            var policy = manifest["policy"]!.AsObject();
            if (mutation == "include")
                policy["include"] = "[ForgeTrust.*]*";
            else
                policy["unexpected"] = true;
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("policy", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    // Value: protects=Manifest policies cannot replace required keys with duplicate names;
    // fails_when=Object equality accepts repeated keys because only raw property counts match;
    // why_new=JsonObject policy mutations cannot represent duplicate property names;
    // seam=none;
    // no_claim=This synthetic bundle verifies policy rejection, not input attestation.
    public async Task VerifyAsync_rejects_duplicate_policy_keys_even_when_the_raw_property_count_matches()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        Assert.True((await fixture.VerifyAsync()).Verified);

        var manifestPath = Path.Combine(fixture.Primary, "manifest.json");
        var originalManifest = await File.ReadAllTextAsync(manifestPath);
        using var original = JsonDocument.Parse(originalManifest);
        var originalPolicy = original.RootElement.GetProperty("policy");
        const string duplicatePolicy = """
            {
              "driver": "collector",
              "driver": "collector",
              "driver": "collector",
              "driver": "collector",
              "driver": "collector"
            }
            """;
        var mutatedManifest = originalManifest.Replace(
            originalPolicy.GetRawText(), duplicatePolicy, StringComparison.Ordinal);
        Assert.NotEqual(originalManifest, mutatedManifest);

        using var mutated = JsonDocument.Parse(mutatedManifest);
        var policy = mutated.RootElement.GetProperty("policy");
        Assert.Equal(5, originalPolicy.EnumerateObject().Count());
        Assert.Equal(5, policy.EnumerateObject().Count());
        Assert.All(policy.EnumerateObject(), property =>
        {
            Assert.Equal("driver", property.Name);
            Assert.Equal("collector", property.Value.GetString());
        });
        foreach (var required in new[] { "format", "exclude", "include", "normalization" })
        {
            Assert.False(policy.TryGetProperty(required, out _));
        }

        await File.WriteAllTextAsync(manifestPath, mutatedManifest);
        var manifestHash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(manifestPath))).ToLowerInvariant();
        await FanInFixture.MutateJsonAsync(fixture.Receipt, receipt =>
            receipt["inputs"]![0]!["manifestSha256"] = manifestHash);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());
    }

    [Fact]
    public async Task VerifyAsync_uses_receipt_manifest_order_and_requires_primary_first()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();

        var reversed = fixture.Native.Reverse().ToArray();
        var result = await fixture.VerifyAsync(reversed);

        Assert.Equal(fixture.Primary, result.InputDirectories[0]);
        Assert.Equal(4, result.InputDirectories.Count);
    }

    [Fact]
    public async Task VerifyAsync_rejects_duplicate_receipt_manifest_hashes()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Output, "verification.json")))!.AsObject();
        var inputs = receipt["inputs"]!.AsArray();
        inputs[1]!["manifestSha256"] = inputs[2]!["manifestSha256"]!.GetValue<string>();
        await File.WriteAllTextAsync(Path.Combine(fixture.Output, "verification.json"), receipt.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("duplicated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_crosschecks_the_complete_solution_test_inventory()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await File.WriteAllTextAsync(fixture.Solution,
            "<Solution><Project Path=\"tests/Foo.Tests/Foo.Tests.csproj\" /><Project Path=\"tests/Bar.Tests/Bar.Tests.csproj\" /></Solution>");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("inventory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_rejects_failed_primary_timing_after_refreshing_its_attestation()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var timings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Primary, "timings.json")))!.AsObject();
        timings["merge"]!["exitCode"] = 1;
        await File.WriteAllTextAsync(Path.Combine(fixture.Primary, "timings.json"), timings.ToJsonString());
        await fixture.RefreshManifestAndReceiptAsync(fixture.Primary, "timings.json");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("timing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    public async Task VerifyAsync_rejects_missing_or_truncated_primary_timings(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var timingsPath = Path.Combine(fixture.Primary, "timings.json");
        if (mutation == "missing")
            File.Delete(timingsPath);
        else
            await File.WriteAllTextAsync(timingsPath, "{");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.True(exception.Message.Contains("inventory", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("Malformed", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("timing", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_does_not_combine_partial_platform_branches_into_new_full_coverage()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        foreach (var file in new[] { "coverage.cobertura.xml", "collector.cobertura.xml" })
        {
            var path = TestPathUtils.PathUnder(fixture.Native[0], file);
            var xml = (await File.ReadAllTextAsync(path)).Replace("100% (1/1)", "0% (0/1)", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, xml);
            await fixture.RefreshManifestAndReceiptAsync(fixture.Native[0], file);
        }

        var result = await fixture.VerifyAsync();

        Assert.Equal(1, result.BranchesCovered);
        Assert.Equal(1, result.BranchesValid);
    }

    [Theory]
    [InlineData("effectiveThresholds", "line", 94.4)]
    [InlineData("line", "valid", 0)]
    public async Task VerifyAsync_rejects_independent_gate_mutations(string section, string property, double value)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var gate = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ComposedGate))!.AsObject();
        gate[section]![property] = value;
        if (section == "line" && property == "valid")
            gate["line"]!["covered"] = 0;
        await File.WriteAllTextAsync(fixture.ComposedGate, gate.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.True(exception.Message.Contains("threshold", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("numerical", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_rejects_gate_counts_that_do_not_match_composed_xml()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var gate = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ComposedGate))!.AsObject();
        gate["line"]!["covered"] = 0;
        gate["line"]!["percent"] = 0;
        await File.WriteAllTextAsync(fixture.ComposedGate, gate.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("aggregate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_rejects_gate_diff_snapshot_mutation()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var gate = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ComposedGate))!.AsObject();
        gate["patchDiffSource"]!["diffFile"] = "different.diff";
        await File.WriteAllTextAsync(fixture.ComposedGate, gate.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("diff source", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("missing")]
    [InlineData("extra")]
    public async Task VerifyAsync_requires_an_exact_attested_file_inventory(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var target = Path.Combine(fixture.Native[0], "collector.cobertura.xml");
        if (mutation == "hash")
        {
            await fixture.MutateManifestAsync(fixture.Native[0], manifest =>
                manifest["files"]!.AsObject()["collector.cobertura.xml"] = "0");
        }
        else if (mutation == "missing")
        {
            File.Delete(target);
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Native[0], "unexpected.txt"), "unexpected");
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.True(exception.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("inventory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_accepts_external_doctype_without_resolving_external_data()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var coveragePath = Path.Combine(fixture.Primary, "coverage.cobertura.xml");
        var coverage = await File.ReadAllTextAsync(coveragePath);
        var external = "<!DOCTYPE coverage SYSTEM \"missing-external.dtd\">" + coverage;
        await File.WriteAllTextAsync(coveragePath, external);
        await File.WriteAllTextAsync(Path.Combine(fixture.Output, "primary-paths.cobertura.xml"), external);
        await fixture.RefreshManifestAndReceiptAsync(fixture.Primary, "coverage.cobertura.xml");
        await fixture.RefreshOutputHashesAsync();

        var result = await fixture.VerifyAsync();

        Assert.True(result.Verified);
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("scope", "\"native\"")]
    [InlineData("configuration", "\"Release\"")]
    [InlineData("commit", "\"other\"")]
    [InlineData("tree", "\"other\"")]
    [InlineData("sdk", "\"other\"")]
    [InlineData("sourceRoot", "null")]
    [InlineData("sourceRoot", "\"\"")]
    [InlineData("sourceRoot", "\"relative\"")]
    [InlineData("filter", "\"filtered\"")]
    [InlineData("run", "[]")]
    [InlineData("run", "{}")]
    [InlineData("run", "{\"GITHUB_REPOSITORY\":\"repo\",\"GITHUB_RUN_ID\":null,\"GITHUB_RUN_ATTEMPT\":\"1\"}")]
    [InlineData("policy", "null")]
    [InlineData("policy", "[]")]
    [InlineData("files", "{}")]
    public async Task VerifyAsync_rejects_primary_manifest_boundary_values(string property, string json)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await fixture.MutateManifestAsync(fixture.Primary, manifest => manifest[property] = JsonNode.Parse(json));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());
    }

    [Theory]
    [InlineData("driver", "\"msbuild\"")]
    [InlineData("format", "\"opencover\"")]
    [InlineData("exclude", "null")]
    [InlineData("normalization", "\"other\"")]
    [InlineData("include", "true")]
    public async Task VerifyAsync_checks_every_instrumentation_policy_field(string property, string json)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await fixture.MutateManifestAsync(fixture.Native[0], manifest => manifest["policy"]![property] = JsonNode.Parse(json));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("policy", exception.Message);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("coverageDriver")]
    [InlineData("coverageFiles")]
    [InlineData("missing-project")]
    [InlineData("extra-project")]
    [InlineData("wrong-project")]
    [InlineData("exitCode")]
    [InlineData("executionStatus")]
    [InlineData("coverageArtifactStatus")]
    public async Task VerifyAsync_rejects_attested_incomplete_or_failed_primary_execution(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await FanInFixture.MutateJsonAsync(Path.Combine(fixture.Primary, "timings.json"), timings =>
        {
            var projects = timings["projects"]!.AsArray();
            switch (mutation)
            {
                case "configuration": timings[mutation] = "Release"; break;
                case "coverageDriver": timings[mutation] = "msbuild"; break;
                case "coverageFiles": timings["artifacts"]![mutation] = 0; break;
                case "missing-project": projects.Clear(); break;
                case "extra-project": projects.Add(projects[0]!.DeepClone()); break;
                case "wrong-project": projects[0]!["project"] = "tests/Other.Tests.csproj"; break;
                case "exitCode": projects[0]![mutation] = 1; break;
                case "executionStatus": projects[0]![mutation] = "cancelled"; break;
                case "coverageArtifactStatus": projects[0]![mutation] = "missing"; break;
            }
        });
        await fixture.RefreshManifestAndReceiptAsync(fixture.Primary, "timings.json");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("Primary solution", exception.Message);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("tree")]
    [InlineData("sdk")]
    [InlineData("configuration")]
    [InlineData("policy")]
    [InlineData("run")]
    [InlineData("outputSha256")]
    [InlineData("primaryPathsSha256")]
    [InlineData("retainedClassLines")]
    [InlineData("retainedMethodLines")]
    [InlineData("linesCovered")]
    [InlineData("branchesCovered")]
    [InlineData("branchesValid")]
    [InlineData("inputClassLines")]
    [InlineData("inputMethodLines")]
    public async Task VerifyAsync_rejects_receipt_identity_hash_and_counter_tampering(string property)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await FanInFixture.MutateJsonAsync(fixture.Receipt, receipt =>
        {
            if (property is "inputClassLines" or "inputMethodLines")
            {
                // Same total, forged attribution to individual inputs.
                var counts = receipt["inventory"]![property]!.AsArray();
                counts[0] = counts[0]!.GetValue<int>() + 1;
                counts[1] = counts[1]!.GetValue<int>() - 1;
            }
            else if (receipt["inventory"]!.AsObject().ContainsKey(property))
                receipt["inventory"]![property] = 999;
            else if (property == "policy")
                receipt[property]!["include"] = "[Subset]*";
            else if (property == "run")
                receipt[property]!["GITHUB_RUN_ATTEMPT"] = "2";
            else
                receipt[property] = "tampered";
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.True(exception.Message.Contains("receipt", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("inventory", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("null-hash")]
    [InlineData("unknown-hash")]
    [InlineData("primary-last")]
    public async Task VerifyAsync_rejects_receipt_input_inventory_and_primary_order_tampering(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await FanInFixture.MutateJsonAsync(fixture.Receipt, receipt =>
        {
            var inputs = receipt["inputs"]!.AsArray();
            switch (mutation)
            {
                case "missing": inputs.RemoveAt(3); break;
                case "extra": inputs.Add(inputs[1]!.DeepClone()); break;
                case "null-hash": inputs[1]!["manifestSha256"] = null; break;
                case "unknown-hash": inputs[1]!["manifestSha256"] = new string('0', 64); break;
                case "primary-last":
                    var primary = inputs[0]!;
                    inputs.RemoveAt(0);
                    inputs.Add(primary);
                    break;
            }
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains(mutation == "primary-last" ? "primary" : "input", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("no-packages")]
    [InlineData("no-classes")]
    [InlineData("wrong-root")]
    [InlineData("root-child")]
    [InlineData("source-kind")]
    [InlineData("nested-source")]
    [InlineData("package-kind")]
    [InlineData("class-kind")]
    [InlineData("class-child")]
    [InlineData("method-kind")]
    [InlineData("method-child")]
    [InlineData("line-kind")]
    [InlineData("line-child")]
    [InlineData("condition-kind")]
    [InlineData("nested-condition")]
    public async Task VerifyAsync_rejects_unexpected_cobertura_structure(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await fixture.MutateXmlAsync(fixture.Primary, "coverage.cobertura.xml", root =>
        {
            var cls = root.Descendants("class").Single();
            var method = cls.Descendants("method").First();
            var line = cls.Element("lines")!.Element("line")!;
            switch (mutation)
            {
                case "no-packages": root.Element("packages")!.Remove(); break;
                case "no-classes": root.Descendants("classes").Single().Remove(); break;
                case "wrong-root": root.Name = "report"; break;
                case "root-child": root.Add(new XElement("unexpected")); break;
                case "source-kind": root.Add(new XElement("sources", new XElement("unexpected"))); break;
                case "nested-source": root.Add(new XElement("sources", new XElement("source", new XElement("unexpected")))); break;
                case "package-kind": root.Descendants("package").Single().Name = "unexpected"; break;
                case "class-kind": cls.Name = "unexpected"; break;
                case "class-child": cls.Add(new XElement("unexpected")); break;
                case "method-kind": method.Name = "unexpected"; break;
                case "method-child": method.Add(new XElement("unexpected")); break;
                case "line-kind": line.Name = "unexpected"; break;
                case "line-child": line.Add(new XElement("unexpected")); break;
                case "condition-kind": line.Add(new XElement("conditions", new XElement("unexpected"))); break;
                case "nested-condition": line.Add(new XElement("conditions", new XElement("condition", new XElement("unexpected")))); break;
            }
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("Cobertura", exception.Message);
    }

    [Theory]
    [InlineData("lines-valid", "0")]
    [InlineData("lines-covered", "-1")]
    [InlineData("lines-covered", "2")]
    [InlineData("branches-covered", "-1")]
    [InlineData("branches-covered", "2")]
    [InlineData("branches-valid", "-1")]
    [InlineData("lines-valid", null)]
    [InlineData("lines-valid", "not-a-number")]
    [InlineData("lines-valid", "999999999999999999999")]
    public async Task VerifyAsync_rejects_invalid_xml_aggregate_counters(string property, string? value)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await fixture.MutateXmlAsync(fixture.Primary, "coverage.cobertura.xml", root => root.SetAttributeValue(property, value));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());
    }

    [Theory]
    [InlineData(null, "true")]
    [InlineData(null, "TRUE")]
    [InlineData("missing", "true")]
    [InlineData("100% 1/1", "true")]
    [InlineData("100% (1)", "true")]
    [InlineData("100% (1/1", "true")]
    [InlineData("100% (1/1) trailing", "true")]
    [InlineData("100% (1/0)", "true")]
    [InlineData("200% (2/1)", "true")]
    [InlineData("0% (-1/1)", "true")]
    [InlineData("0% (a/1)", "true")]
    [InlineData("0% (1/b)", "true")]
    [InlineData("0% (999999999999999999/1)", "true")]
    [InlineData("0% (0/999999999999999999)", "true")]
    public async Task VerifyAsync_requires_complete_valid_branch_counter_syntax(string? counters, string branch)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await fixture.MutateXmlAsync(fixture.Primary, "coverage.cobertura.xml", root =>
        {
            var line = root.Descendants("class").Elements("lines").Elements("line").Single();
            line.SetAttributeValue("condition-coverage", counters);
            line.SetAttributeValue("branch", branch);
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("Branch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../src/C.cs")]
    [InlineData("src/../C.cs")]
    [InlineData("/foreign/root/C.cs")]
    [InlineData("Z:\\foreign\\C.cs")]
    [InlineData("")]
    public async Task VerifyAsync_rejects_paths_outside_the_attested_source_root(string filename)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await fixture.MutateXmlAsync(fixture.Native[0], "collector.cobertura.xml", root =>
            root.Descendants("class").Single().SetAttributeValue("filename", filename));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("source root", exception.Message);
    }

    [Theory]
    [InlineData("point")]
    [InlineData("hit")]
    [InlineData("denominator")]
    [InlineData("empty")]
    public async Task VerifyAsync_rejects_native_normalization_that_drops_observations(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var file = mutation is "denominator" or "empty" ? "collector.cobertura.xml" : "coverage.cobertura.xml";
        await fixture.MutateXmlAsync(fixture.Native[0], file, root =>
        {
            var lines = root.Descendants("class").Elements("lines").Single();
            switch (mutation)
            {
                case "point": lines.Element("line")!.SetAttributeValue("number", 2); break;
                case "hit": lines.Element("line")!.SetAttributeValue("hits", 0); break;
                case "denominator": root.SetAttributeValue("branches-valid", 2); break;
                case "empty": lines.Remove(); break;
            }
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("normalization", exception.Message);
    }

    [Theory]
    [InlineData(1, 3, "33.3333")]
    [InlineData(2, 3, "66.6667")]
    [InlineData(1, 6, "16.6667")]
    public async Task VerifyAsync_accepts_python_six_digit_branch_percentages_under_foreign_culture(int covered, int valid, string percent)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var primary = XDocument.Load(Path.Combine(fixture.Primary, "coverage.cobertura.xml"));
        var native = new XDocument(primary);
        var addedClass = new XElement("class", new XAttribute("name", "N"), new XAttribute("filename", "src/N.cs"),
            new XElement("lines", new XElement("line", new XAttribute("number", 2), new XAttribute("hits", 1),
                new XAttribute("branch", "true"), new XAttribute("condition-coverage", $"{percent}% ({covered}/{valid})"))));
        native.Descendants("classes").Single().Add(new XElement(addedClass));
        var composed = new XDocument(primary);
        composed.Root!.Element("packages")!.Add(new XElement("package", new XAttribute("name", "P"),
            new XElement("classes", addedClass)));
        foreach (var document in new[] { native, composed })
        {
            document.Root!.SetAttributeValue("lines-covered", 2);
            document.Root.SetAttributeValue("lines-valid", 2);
            document.Root.SetAttributeValue("branches-covered", covered + 1);
            document.Root.SetAttributeValue("branches-valid", valid + 1);
            document.Root.SetAttributeValue("branch-rate", ((covered + 1) / (double)(valid + 1)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        await fixture.WriteDocumentsAsync(primary, [native, primary, primary], composed);
        var priorCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

            var result = await fixture.VerifyAsync();

            Assert.True(result.Verified);
            Assert.Equal(2, result.RetainedClassLines);
            Assert.Equal(covered + 1, result.BranchesCovered);
            Assert.Equal(valid + 1, result.BranchesValid);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Fact]
    public async Task Trusted_request_survives_web_json_roundtrip_and_verifies()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(fixture.Request, options);
        var request = JsonSerializer.Deserialize<EvidenceCoverageFanInRequest>(json, options);

        Assert.NotNull(request);
        Assert.True(request.ExpectedTestProjects.SetEquals(fixture.Request.ExpectedTestProjects));
        var result = await EvidenceCoverageFanIn.VerifyAsync(request);
        Assert.True(result.Verified);
    }

    [Theory]
    [InlineData("blank-identity")]
    [InlineData("configuration")]
    [InlineData("missing-solution")]
    [InlineData("outside-solution")]
    [InlineData("native-count")]
    [InlineData("native-platform")]
    public async Task VerifyAsync_rejects_untrusted_request_boundaries(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var request = fixture.Request;
        switch (mutation)
        {
            case "blank-identity": request = request with { Commit = " " }; break;
            case "configuration": request = request with { Configuration = "production" }; break;
            case "missing-solution": request = request with { SolutionFile = Path.Combine(fixture.Repository, "missing.slnx") }; break;
            case "outside-solution": request = request with { SolutionFile = Path.Combine(fixture.Primary, "outside.slnx") }; break;
            case "native-count": request = request with { NativeCaptureDirectories = fixture.Native.Take(2).ToArray() }; break;
            case "native-platform": await fixture.MutateNativeManifestAsync("platform", "OtherOS"); break;
        }

        if (mutation.StartsWith("native", StringComparison.Ordinal))
            await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceCoverageFanIn.VerifyAsync(request).AsTask());
        else
            await Assert.ThrowsAsync<ArgumentException>(() => EvidenceCoverageFanIn.VerifyAsync(request).AsTask());
    }

    [Theory]
    [InlineData("manifest", "missing")]
    [InlineData("manifest", "empty")]
    [InlineData("manifest", "oversized")]
    [InlineData("manifest", "malformed")]
    [InlineData("receipt", "malformed")]
    [InlineData("receipt", "oversized")]
    [InlineData("gate", "malformed")]
    [InlineData("gate", "missing-key")]
    [InlineData("gate", "wrong-type")]
    public async Task VerifyAsync_rejects_bounded_or_malformed_json_at_each_trust_boundary(string role, string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var path = role switch
        {
            "manifest" => Path.Combine(fixture.Primary, "manifest.json"),
            "receipt" => fixture.Receipt,
            _ => fixture.ComposedGate,
        };
        switch (mutation)
        {
            case "missing": File.Delete(path); break;
            case "empty": await File.WriteAllTextAsync(path, ""); break;
            case "oversized":
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                    stream.SetLength(1024L * 1024 + 1);
                break;
            case "malformed": await File.WriteAllTextAsync(path, "{"); break;
            case "missing-key": await FanInFixture.MutateJsonAsync(path, json => json.Remove("passed")); break;
            case "wrong-type": await FanInFixture.MutateJsonAsync(path, json => json["thresholds"] = "invalid"); break;
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        if (mutation == "malformed")
            Assert.IsAssignableFrom<JsonException>(exception.InnerException);
        else if (mutation is "missing-key" or "wrong-type")
            Assert.NotNull(exception.InnerException);
        else
            Assert.Contains("evidence", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("malformed")]
    public async Task VerifyAsync_rejects_invalid_composed_xml_before_accepting_its_receipt(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var path = Path.Combine(fixture.Output, "coverage.cobertura.xml");
        switch (mutation)
        {
            case "missing": File.Delete(path); break;
            case "empty": await File.WriteAllTextAsync(path, ""); break;
            case "oversized":
                // SetLength creates a sparse file; no 512 MiB buffer, contents, or hash is materialized.
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                    stream.SetLength(512L * 1024 * 1024 + 1);
                break;
            case "malformed": await File.WriteAllTextAsync(path, "<coverage>"); break;
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("coverage", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_honors_caller_cancellation_without_reclassifying_it_as_bad_evidence()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EvidenceCoverageFanIn.VerifyAsync(fixture.Request, cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("oversized")]
    public async Task VerifyAsync_bounds_attested_input_files_before_hashing(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var path = Path.Combine(fixture.Native[0], "collector.cobertura.xml");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            stream.SetLength(mutation == "oversized" ? 512L * 1024 * 1024 + 1 : 0);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("Invalid evidence", exception.Message);
    }

    [Theory]
    [InlineData("primary", "passed", null, "false")]
    [InlineData("primary", "tolerancePercent", null, "0")]
    [InlineData("primary", "patchLineMode", null, "\"coverlet\"")]
    [InlineData("primary", "thresholds", "line", "94")]
    [InlineData("primary", "thresholds", "branch", "84")]
    [InlineData("primary", "effectiveThresholds", "line", "94")]
    [InlineData("primary", "effectiveThresholds", "branch", "84")]
    [InlineData("composed", "thresholds", "line", "1")]
    [InlineData("composed", "thresholds", "branch", "1")]
    [InlineData("composed", "thresholds", "patchLine", "94")]
    [InlineData("composed", "thresholds", "patchBranch", "84")]
    [InlineData("composed", "effectiveThresholds", "patchLine", "94")]
    [InlineData("composed", "effectiveThresholds", "patchBranch", "84")]
    [InlineData("composed", "coverage", null, "\"primary-paths.cobertura.xml\"")]
    [InlineData("composed", "patchDiffSource", "diffBase", "\"different\"")]
    [InlineData("composed", "patchBranch", "covered", "-1")]
    [InlineData("composed", "patchBranch", "covered", "2")]
    [InlineData("composed", "patchBranch", "measurable", "0")]
    [InlineData("composed", "patchBranch", "percent", "84")]
    public async Task VerifyAsync_rejects_independent_gate_policy_and_numeric_mutations(
        string role, string section, string? property, string jsonValue)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await FanInFixture.MutateJsonAsync(role == "primary" ? fixture.PrimaryGate : fixture.ComposedGate, gate =>
        {
            if (property is null)
                gate[section] = JsonNode.Parse(jsonValue);
            else
                gate[section]![property] = JsonNode.Parse(jsonValue);
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("gate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("patchLine")]
    [InlineData("patchBranch")]
    public async Task VerifyAsync_rejects_a_smaller_patch_denominator_even_when_both_percentages_pass(string metric)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await FanInFixture.MutateJsonAsync(fixture.PrimaryGate, gate =>
        {
            gate[metric]!["covered"] = 2;
            gate[metric]!["measurable"] = 2;
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("removed measurable", exception.Message);
    }

    [Theory]
    [InlineData("equivalent", true)]
    [InlineData("array-order", false)]
    [InlineData("array-length", false)]
    [InlineData("number", false)]
    [InlineData("boolean", false)]
    [InlineData("type", false)]
    [InlineData("property-name", false)]
    public async Task VerifyAsync_compares_the_entire_diff_snapshot_semantically(string mutation, bool accepted)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var left = JsonNode.Parse("{\"diffBase\":\"diff\",\"snapshot\":[1,true,null,{\"label\":\"source\"}],\"empty\":false}")!;
        var right = JsonNode.Parse("{\"empty\":false,\"snapshot\":[1.0,true,null,{\"label\":\"source\"}],\"diffBase\":\"diff\"}")!;
        switch (mutation)
        {
            case "array-order": right["snapshot"] = JsonNode.Parse("[true,1,null,{\"label\":\"source\"}]"); break;
            case "array-length": right["snapshot"]!.AsArray().RemoveAt(3); break;
            case "number": right["snapshot"]![0] = 2; break;
            case "boolean": right["empty"] = true; break;
            case "type": right["snapshot"]![0] = "1"; break;
            case "property-name": right["snapshot"]![3] = JsonNode.Parse("{\"other\":\"source\"}"); break;
        }
        await FanInFixture.MutateJsonAsync(fixture.PrimaryGate, gate => gate["patchDiffSource"] = left);
        await FanInFixture.MutateJsonAsync(fixture.ComposedGate, gate => gate["patchDiffSource"] = right);

        if (accepted)
            Assert.True((await fixture.VerifyAsync()).Verified);
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());
            Assert.Contains("snapshots differ", exception.Message);
        }
    }

    [Theory]
    [InlineData("extra-attribute")]
    [InlineData("missing-attribute")]
    [InlineData("wrong-hit")]
    [InlineData("extra-record")]
    [InlineData("wrong-element")]
    [InlineData("rate")]
    [InlineData("invalid-rate")]
    public async Task VerifyAsync_rejects_composed_xml_semantic_changes_even_with_refreshed_output_hash(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await fixture.MutateXmlAsync(fixture.Output, "coverage.cobertura.xml", root =>
        {
            var cls = root.Descendants("class").Single();
            switch (mutation)
            {
                case "extra-attribute": cls.SetAttributeValue("unexpected", "value"); break;
                case "missing-attribute": cls.Attribute("name")!.Remove(); break;
                case "wrong-hit": cls.Element("lines")!.Element("line")!.SetAttributeValue("hits", 9); break;
                case "extra-record": cls.Element("methods")!.Add(new XElement(cls.Descendants("method").First())); break;
                case "wrong-element": cls.Element("lines")!.AddFirst(new XElement("line", new XAttribute("number", 2), new XAttribute("hits", 0))); break;
                case "rate": root.SetAttributeValue("line-rate", "0.5"); break;
                case "invalid-rate": root.SetAttributeValue("line-rate", "invalid"); break;
            }
        });
        await fixture.RefreshOutputHashesAsync();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("composition", exception.Message);
    }

    [Theory]
    [InlineData("number", "0", false)]
    [InlineData("hits", "-1", false)]
    [InlineData("number", "0", true)]
    [InlineData("hits", "-1", true)]
    [InlineData("number", null, true)]
    [InlineData("hits", null, true)]
    public async Task VerifyAsync_rejects_invalid_class_or_method_points(string attribute, string? value, bool method)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await fixture.MutateXmlAsync(fixture.Primary, "coverage.cobertura.xml", root =>
        {
            var lines = method ? root.Descendants("method").First().Element("lines")! : root.Descendants("class").Single().Element("lines")!;
            lines.Element("line")!.SetAttributeValue(attribute, value);
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());
    }

    [Theory]
    [InlineData("/synthetic/primary/root/src/C.cs")]
    [InlineData("src//./C.cs")]
    [InlineData("src\\C.cs")]
    public async Task VerifyAsync_normalizes_only_attested_roots_and_redundant_path_segments(string filename)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await fixture.MutateXmlAsync(fixture.Primary, "coverage.cobertura.xml", root =>
            root.Descendants("class").Single().SetAttributeValue("filename", filename));

        var result = await fixture.VerifyAsync();

        Assert.True(result.Verified);
        Assert.Equal(1, result.RetainedClassLines);
        Assert.Equal(4, result.InputClassLines);
        Assert.Equal(4, result.InputManifestHashes.Count);
    }

    [Fact]
    public async Task VerifyAsync_preserves_leaf_text_and_accepts_numeric_rate_spelling_and_attribute_order()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var primary = XDocument.Load(Path.Combine(fixture.Primary, "coverage.cobertura.xml"));
        primary.Root!.AddFirst(new XElement("sources", new XElement("source", "/source")));
        var composed = XDocument.Load(Path.Combine(fixture.Output, "coverage.cobertura.xml"));
        await fixture.WriteDocumentsAsync(primary, [primary, primary, primary], composed);
        await fixture.MutateXmlAsync(fixture.Output, "coverage.cobertura.xml", root =>
        {
            root.SetAttributeValue("line-rate", "1.0000000000");
            root.SetAttributeValue("branch-rate", "1e0");
            var attributes = root.Attributes().Select(attribute => new XAttribute(attribute)).Reverse().ToArray();
            root.RemoveAttributes();
            root.Add(attributes);
        });
        await fixture.RefreshOutputHashesAsync();

        Assert.True((await fixture.VerifyAsync()).Verified);

        await fixture.MutateXmlAsync(fixture.Output, "primary-paths.cobertura.xml", root =>
            root.Element("sources")!.Element("source")!.Value = "/different-source");
        await fixture.RefreshOutputHashesAsync();
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());
        Assert.Contains("primary paths", exception.Message);
    }

    [Theory]
    [InlineData("no-lines")]
    [InlineData("no-methods")]
    [InlineData("no-method-lines")]
    [InlineData("unnamed-method")]
    public async Task VerifyAsync_retains_optional_method_records_without_inventing_coverage(string variation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var primary = XDocument.Load(Path.Combine(fixture.Primary, "coverage.cobertura.xml"));
        var native = new XDocument(primary);
        var extra = new XElement("class", new XAttribute("name", "N"), new XAttribute("filename", "src/N.cs"));
        if (variation == "no-lines")
            extra.Add(new XElement("methods", new XElement("method", new XAttribute("name", "Empty"), new XAttribute("signature", "()"))));
        else
        {
            extra.Add(new XElement("lines", new XElement("line", new XAttribute("number", 2), new XAttribute("hits", 1))));
            if (variation != "no-methods")
            {
                var method = new XElement("method");
                if (variation == "no-method-lines")
                    method.Add(new XAttribute("name", "Empty"), new XAttribute("signature", "()"));
                else
                    method.Add(new XElement("lines", new XElement("line", new XAttribute("number", 2), new XAttribute("hits", 1))));
                extra.AddFirst(new XElement("methods", method));
            }
        }
        native.Descendants("classes").Single().Add(extra);
        var expected = new XElement(extra);
        foreach (var method in expected.Descendants("method"))
        {
            method.SetAttributeValue("name", method.Attribute("name")?.Value ?? "");
            method.SetAttributeValue("signature", method.Attribute("signature")?.Value ?? "");
            if (method.Element("lines") is null) method.Add(new XElement("lines"));
        }
        if (expected.Element("lines") is null) expected.Add(new XElement("lines"));
        foreach (var line in expected.Descendants("line")) line.SetAttributeValue("branch", "false");
        var composed = new XDocument(primary);
        composed.Root!.Element("packages")!.Add(new XElement("package", new XAttribute("name", "P"), new XElement("classes", expected)));
        var valid = variation == "no-lines" ? 1 : 2;
        foreach (var document in new[] { native, composed })
        {
            document.Root!.SetAttributeValue("lines-covered", valid);
            document.Root.SetAttributeValue("lines-valid", valid);
        }
        await fixture.WriteDocumentsAsync(primary, [native, primary, primary], composed);

        var result = await fixture.VerifyAsync();

        Assert.True(result.Verified);
        Assert.Equal(valid, result.RetainedClassLines);
        Assert.Equal(variation == "unnamed-method" ? 3 : 2, result.RetainedMethodLines);
    }

    [Fact]
    public async Task VerifyAsync_preserves_duplicate_classes_methods_and_lines_with_ordinal_record_order()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var primary = XDocument.Load(Path.Combine(fixture.Primary, "coverage.cobertura.xml"));
        var native = XDocument.Parse("""
            <coverage version="appsurface-verified-fanin-v1" lines-covered="6" lines-valid="6" branches-covered="1" branches-valid="1" line-rate="1" branch-rate="1">
              <packages>
                <package name="Z"><classes><class name="C" filename="src/C.cs"><lines><line number="1" hits="1" /></lines></class></classes></package>
                <package name="P"><classes>
                  <class name="C" filename="src/C.cs">
                    <methods>
                      <method name="Z" signature=""><lines><line number="1" hits="1" /></lines></method>
                      <method name="M" signature="()"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (1/1)" /></lines></method>
                      <method name="M" signature="()"><lines><line number="1" hits="2" /></lines></method>
                      <method name="A" signature=""><lines><line number="1" hits="1" /></lines></method>
                    </methods>
                    <lines><line number="1" hits="1" branch="true" condition-coverage="100% (1/1)" /><line number="1" hits="2" /></lines>
                  </class>
                  <class name="C" filename="src/C.cs"><lines><line number="2" hits="1" /></lines></class>
                  <class name="C" filename="src/B.cs"><lines><line number="1" hits="1" /></lines></class>
                  <class name="A" filename="src/A.cs"><lines><line number="1" hits="1" /></lines></class>
                </classes></package>
              </packages>
            </coverage>
            """);
        var composed = XDocument.Parse("""
            <coverage version="appsurface-verified-fanin-v1" lines-covered="6" lines-valid="6" branches-covered="1" branches-valid="1" line-rate="1" branch-rate="1">
              <packages>
                <package name="P"><classes><class name="A" filename="src/A.cs"><lines><line number="1" hits="1" branch="false" /></lines></class></classes></package>
                <package name="P"><classes><class name="C" filename="src/B.cs"><lines><line number="1" hits="1" branch="false" /></lines></class></classes></package>
                <package name="P"><classes><class name="C" filename="src/C.cs">
                  <methods>
                    <method name="A" signature=""><lines><line number="1" hits="1" branch="false" /></lines></method>
                    <method name="M" signature="()"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (1/1)" /></lines></method>
                    <method name="M" signature="()"><lines><line number="1" hits="2" branch="false" /></lines></method>
                    <method name="M" signature="(int)"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (1/1)" /></lines></method>
                    <method name="Z" signature=""><lines><line number="1" hits="1" branch="false" /></lines></method>
                  </methods>
                  <lines><line number="1" hits="1" branch="true" condition-coverage="100% (1/1)" /><line number="1" hits="2" branch="false" /></lines>
                </class></classes></package>
                <package name="P"><classes><class name="C" filename="src/C.cs"><lines><line number="2" hits="1" branch="false" /></lines></class></classes></package>
                <package name="Z"><classes><class name="C" filename="src/C.cs"><lines><line number="1" hits="1" branch="false" /></lines></class></classes></package>
              </packages>
            </coverage>
            """);
        await fixture.WriteDocumentsAsync(primary, [native, primary, primary], composed);

        var result = await fixture.VerifyAsync(fixture.Native.Reverse().ToArray());

        Assert.True(result.Verified);
        Assert.Equal(9, result.InputClassLines);
        Assert.Equal(6, result.RetainedClassLines);
        Assert.Equal(10, result.InputMethodLines);
        Assert.Equal(5, result.RetainedMethodLines);
        Assert.Equal(6, result.LinesCovered);
        Assert.Equal(new[] { fixture.Primary, fixture.Native[2], fixture.Native[0], fixture.Native[1] }, result.InputDirectories);
    }

    [Fact]
    public async Task VerifyAsync_rejects_extra_composed_files_even_when_all_attested_reports_match()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Output, "unattested.txt"), "unattested");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains("Composed output inventory", exception.Message);
    }

    [Theory]
    [InlineData("empty-project-inventory")]
    [InlineData("existing-outside-solution")]
    [InlineData("duplicate-native-directory")]
    [InlineData("empty-attested-run")]
    [InlineData("missing-run-key")]
    public async Task VerifyAsync_requires_complete_trusted_request_and_workflow_identity(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var request = fixture.Request;
        switch (mutation)
        {
            case "empty-project-inventory":
                request = request with { ExpectedTestProjects = new HashSet<string>() };
                break;
            case "existing-outside-solution":
                var outsideSolution = Path.Combine(fixture.Primary, "outside.slnx");
                await File.WriteAllTextAsync(outsideSolution, await File.ReadAllTextAsync(fixture.Solution));
                request = request with { SolutionFile = outsideSolution };
                break;
            case "duplicate-native-directory":
                request = request with { NativeCaptureDirectories = [fixture.Native[0], fixture.Native[0], fixture.Native[2]] };
                break;
            case "empty-attested-run":
                request = request with { Run = new Dictionary<string, string?>() };
                foreach (var directory in new[] { fixture.Primary }.Concat(fixture.Native))
                    await fixture.MutateManifestAsync(directory, manifest => manifest["run"] = new JsonObject());
                break;
            case "missing-run-key":
                await fixture.MutateManifestAsync(fixture.Native[0], manifest =>
                {
                    var run = manifest["run"]!.AsObject();
                    run.Remove("GITHUB_RUN_ID");
                    run["UNTRUSTED_RUN_ID"] = "1";
                });
                break;
        }

        if (mutation is "empty-project-inventory" or "existing-outside-solution")
            await Assert.ThrowsAsync<ArgumentException>(() => EvidenceCoverageFanIn.VerifyAsync(request).AsTask());
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceCoverageFanIn.VerifyAsync(request).AsTask());
            Assert.Contains(mutation == "duplicate-native-directory" ? "Exactly one" : "provenance", exception.Message);
        }
    }

    [Fact]
    public async Task VerifyAsync_rejects_duplicate_project_execution_even_when_project_and_report_counts_match()
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        const string secondProject = "tests/Bar.IntegrationTests/Bar.IntegrationTests.csproj";
        var solution = XDocument.Load(fixture.Solution);
        solution.Root!.Add(new XElement("Project", new XAttribute("Path", secondProject)));
        await File.WriteAllTextAsync(fixture.Solution, solution.ToString());
        await FanInFixture.MutateJsonAsync(Path.Combine(fixture.Primary, "timings.json"), timings =>
        {
            var projects = timings["projects"]!.AsArray();
            projects.Add(projects[0]!.DeepClone());
            timings["artifacts"]!["coverageFiles"] = 2;
        });
        await fixture.RefreshManifestAndReceiptAsync(fixture.Primary, "timings.json");
        var request = fixture.Request;
        request.ExpectedTestProjects.Add(secondProject);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceCoverageFanIn.VerifyAsync(request).AsTask());

        Assert.Contains("Primary solution project inventory differs", exception.Message);
    }

    [Theory]
    [InlineData("missing-filename", "source root")]
    [InlineData("zero-line", "Invalid coverage line")]
    [InlineData("negative-hits", "Invalid coverage line")]
    [InlineData("drive-relative-root", "identity or policy")]
    [InlineData("empty-primary-records", "no measurable class lines")]
    public async Task VerifyAsync_checks_collector_observations_and_primary_measurement_before_accepting_outputs(
        string mutation, string expectedReason)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        if (mutation == "drive-relative-root")
            await fixture.MutateManifestAsync(fixture.Native[0], manifest => manifest["sourceRoot"] = "C:relative");
        else
        {
            var directory = mutation == "empty-primary-records" ? fixture.Primary : fixture.Native[0];
            var file = mutation == "empty-primary-records" ? "coverage.cobertura.xml" : "collector.cobertura.xml";
            await fixture.MutateXmlAsync(directory, file, root =>
            {
                var cls = root.Descendants("class").Single();
                switch (mutation)
                {
                    case "missing-filename": cls.Attribute("filename")!.Remove(); break;
                    case "zero-line": cls.Element("lines")!.Element("line")!.SetAttributeValue("number", 0); break;
                    case "negative-hits": cls.Element("lines")!.Element("line")!.SetAttributeValue("hits", -1); break;
                    case "empty-primary-records": cls.Element("lines")!.Remove(); break;
                }
            });
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        Assert.Contains(expectedReason, exception.Message);
    }

    [Theory]
    [InlineData("malformed-attested-xml")]
    [InlineData("namespaced-method")]
    [InlineData("invalid-primary-rate")]
    public async Task VerifyAsync_distinguishes_xml_parse_errors_from_semantic_output_mismatches(string mutation)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        if (mutation == "malformed-attested-xml")
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Primary, "coverage.cobertura.xml"), "<coverage><packages></coverage>");
            await fixture.RefreshManifestAndReceiptAsync(fixture.Primary, "coverage.cobertura.xml");
        }
        else if (mutation == "namespaced-method")
            await fixture.MutateXmlAsync(fixture.Output, "coverage.cobertura.xml", root =>
                root.Descendants("method").First().Name = XName.Get("method", "urn:foreign"));
        else
            await fixture.MutateXmlAsync(fixture.Primary, "coverage.cobertura.xml", root =>
                root.SetAttributeValue("line-rate", "invalid"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.VerifyAsync().AsTask());

        if (mutation == "malformed-attested-xml")
        {
            Assert.IsType<System.Xml.XmlException>(exception.InnerException);
            Assert.Contains("Invalid coverage XML", exception.Message);
        }
        else
        {
            Assert.Null(exception.InnerException);
            Assert.Contains(mutation == "namespaced-method" ? "composed output" : "primary paths output", exception.Message);
        }
    }

    [Theory]
    [InlineData("partial", "50% (1/2)")]
    [InlineData("different-denominator", "50% (1/2)")]
    [InlineData("full", "100% (2/2)")]
    [InlineData("nonbranch", null)]
    public async Task VerifyAsync_promotes_method_branches_only_from_a_full_observation_with_the_same_denominator(
        string observation, string? expectedCounters)
    {
        using var fixture = new FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var primary = XDocument.Load(Path.Combine(fixture.Primary, "coverage.cobertura.xml"));
        var primaryLine = primary.Descendants("method").First().Descendants("line").Single();
        primaryLine.SetAttributeValue("condition-coverage", observation == "nonbranch" ? null : "50% (1/2)");
        primaryLine.SetAttributeValue("branch", observation == "nonbranch" ? "false" : "true");
        var native = new XDocument(primary);
        native.Descendants("method").First().Descendants("line").Single().SetAttributeValue("condition-coverage", observation switch
        {
            "different-denominator" => "100% (3/3)",
            "full" => "100% (2/2)",
            _ => observation == "nonbranch" ? null : "50% (1/2)",
        });
        var composed = new XDocument(primary);
        composed.Descendants("method").First().Descendants("line").Single().SetAttributeValue("condition-coverage", expectedCounters);
        await fixture.WriteDocumentsAsync(primary, [native, primary, primary], composed);

        var result = await fixture.VerifyAsync();

        Assert.True(result.Verified);
        Assert.Equal(2, result.RetainedMethodLines);
        Assert.Equal(8, result.InputMethodLines);
        Assert.Equal(1, result.BranchesCovered);
        Assert.Equal(1, result.BranchesValid);
    }

    internal sealed class FanInFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "fanin-" + Guid.NewGuid().ToString("N"));
        private const string Commit = "commit";
        private const string Tree = "tree";
        private const string Sdk = "sdk";
        private static readonly Dictionary<string, string?> Run = new() { ["GITHUB_REPOSITORY"] = "repo", ["GITHUB_RUN_ID"] = "1", ["GITHUB_RUN_ATTEMPT"] = "1" };
        public string Primary => Path.Combine(_root, "primary");
        public string Output => Path.Combine(_root, "output");
        public string Solution => Path.Combine(Repository, "ForgeTrust.AppSurface.slnx");
        public string[] Native => [Path.Combine(_root, "linux"), Path.Combine(_root, "windows"), Path.Combine(_root, "darwin")];
        public string Repository => Path.Combine(_root, "repo");
        public string PrimaryGate => Path.Combine(_root, "primary-gate.json");
        public string ComposedGate => Path.Combine(_root, "composed-gate.json");
        public FanInFixture() { Directory.CreateDirectory(Repository); File.WriteAllText(Path.Combine(Repository, "ForgeTrust.AppSurface.slnx"), "<Solution><Project Path=\"tests/Foo.Tests/Foo.Tests.csproj\" /></Solution>"); }
        public async Task WriteMinimalBundleAsync()
        {
            Directory.CreateDirectory(Primary); Directory.CreateDirectory(Output); foreach (var dir in Native) Directory.CreateDirectory(dir);
            var xml = "<coverage version=\"appsurface-verified-fanin-v1\" lines-covered=\"1\" lines-valid=\"1\" branches-covered=\"1\" branches-valid=\"1\" line-rate=\"1\" branch-rate=\"1\"><packages><package name=\"P\"><classes><class name=\"C\" filename=\"src/C.cs\"><methods><method name=\"M\" signature=\"()\"><lines><line number=\"1\" hits=\"1\" branch=\"true\" condition-coverage=\"100% (1/1)\" /></lines></method><method name=\"M\" signature=\"(int)\"><lines><line number=\"1\" hits=\"1\" branch=\"true\" condition-coverage=\"100% (1/1)\" /></lines></method></methods><lines><line number=\"1\" hits=\"1\" branch=\"true\" condition-coverage=\"100% (1/1)\" /></lines></class></classes></package></packages></coverage>";
            await File.WriteAllTextAsync(Path.Combine(Primary, "coverage.cobertura.xml"), xml); await File.WriteAllTextAsync(Path.Combine(Output, "coverage.cobertura.xml"), xml); await File.WriteAllTextAsync(Path.Combine(Output, "primary-paths.cobertura.xml"), xml);
            var timings = new { configuration = "Debug", coverageDriver = "collector", merge = new { exitCode = 0 }, artifacts = new { coverageFiles = 1 }, projects = new[] { new { project = "tests/Foo.Tests/Foo.Tests.csproj", exitCode = 0, executionStatus = "completed", coverageArtifactStatus = "produced" } } };
            await File.WriteAllTextAsync(Path.Combine(Primary, "timings.json"), JsonSerializer.Serialize(timings));
            await WriteManifestAsync(Primary, "solution", null, Run, "/synthetic/primary/root"); for (var i = 0; i < Native.Length; i++) { await File.WriteAllTextAsync(Path.Combine(Native[i], "collector.cobertura.xml"), xml); await File.WriteAllTextAsync(Path.Combine(Native[i], "coverage.cobertura.xml"), xml); await WriteManifestAsync(Native[i], "native", new[] { "Linux", "Windows", "Darwin" }[i], Run, new[] { "D:\\synthetic\\windows\\root", "/synthetic/linux/root", "/synthetic/darwin/root" }[i]); }
            var manifestDirectories = new[] { Primary, Native[0], Native[1], Native[2] }.OrderBy(path => path == Primary ? "" : JsonNode.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")))!["platform"]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
            await File.WriteAllTextAsync(PrimaryGate, GateJson("diff", false)); await File.WriteAllTextAsync(ComposedGate, GateJson("diff", true));
            var receipt = new { commit = Commit, tree = Tree, sdk = Sdk, configuration = "Debug", policy = Policy(), run = Run, inventory = new { inputClassLines = new[] { 1, 1, 1, 1 }, retainedClassLines = 1, inputMethodLines = new[] { 2, 2, 2, 2 }, retainedMethodLines = 2, linesCovered = 1, branchesCovered = 1, branchesValid = 1 }, inputs = manifestDirectories.Select(path => new { manifestSha256 = Sha256(Path.Combine(path, "manifest.json")) }), outputSha256 = Sha256(Path.Combine(Output, "coverage.cobertura.xml")), primaryPathsSha256 = Sha256(Path.Combine(Output, "primary-paths.cobertura.xml")) };
            await File.WriteAllTextAsync(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(receipt));
        }
        private async Task WriteManifestAsync(string dir, string scope, string? platform, Dictionary<string, string?> run, string sourceRoot)
        {
            var files = scope == "solution" ? new[] { "coverage.cobertura.xml", "timings.json" } : new[] { "coverage.cobertura.xml", "collector.cobertura.xml" }; var hashes = new Dictionary<string, string>(); foreach (var file in files) hashes[file] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(dir, file)))).ToLowerInvariant();
            var value = new { schemaVersion = 1, commit = Commit, tree = Tree, sdk = Sdk, configuration = "Debug", sourceRoot, platform, policy = new { driver = "collector", format = "cobertura", exclude = "[*.Tests]*,[*.IntegrationTests]*", include = (string?)null, normalization = "appsurface-coverage-merge" }, scope, filter = scope == "native" ? "FullyQualifiedName~CoverageRunArtifactReaderTests|FullyQualifiedName~CoverageRunOutputGuardSecurityTests|FullyQualifiedName~CoverageGateTests|FullyQualifiedName~CoverageArtifact|FullyQualifiedName~CoverageStorageCommand" : null, run, files = hashes }; await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(value));
        }
        private static string GateJson(string diff, bool patch) => JsonSerializer.Serialize(new { passed = true, coverage = patch ? "coverage.cobertura.xml" : "primary-paths.cobertura.xml", tolerancePercent = .5, patchLineMode = "codecov", patchDiffSource = new { diffBase = diff }, thresholds = new { line = patch ? 0 : 95, branch = patch ? 0 : 85, patchLine = patch ? 95 : (double?)null, patchBranch = patch ? 85 : (double?)null }, effectiveThresholds = new { line = patch ? 0 : 94.5, branch = patch ? 0 : 84.5, patchLine = patch ? 94.5 : (double?)null, patchBranch = patch ? 84.5 : (double?)null }, line = new { covered = 1, valid = 1, percent = 100d }, branch = new { covered = 1, valid = 1, percent = 100d }, patchLine = new { covered = 1, measurable = 1, percent = 100d }, patchBranch = new { covered = 1, measurable = 1, percent = 100d } });
        public async Task MutateNativeManifestAsync(string property, string value)
        {
            var path = Path.Combine(Native[0], "manifest.json"); var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject(); manifest[property] = value; await File.WriteAllTextAsync(path, manifest.ToJsonString());
        }
        private static string Sha256(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        public ValueTask<EvidenceCoverageFanInResult> VerifyAsync(IReadOnlyList<string>? native = null) => EvidenceCoverageFanIn.VerifyAsync(new EvidenceCoverageFanInRequest(Primary, native ?? Native, Output, Repository, Solution, new HashSet<string> { "tests/Foo.Tests/Foo.Tests.csproj" }, Commit, Tree, Sdk, "Debug", Run, PrimaryGate, ComposedGate, "diff"));
        public async Task MutateManifestAsync(string directory, Action<JsonObject> mutate)
        {
            var path = Path.Combine(directory, "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            mutate(manifest);
            await File.WriteAllTextAsync(path, manifest.ToJsonString());
        }
        public async Task RefreshManifestAndReceiptAsync(string directory, string file)
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest["files"]!.AsObject()[file] = Sha256(TestPathUtils.PathUnder(directory, file));
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

            var receiptPath = Path.Combine(Output, "verification.json");
            var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
            var ordered = new[] { Primary }.Concat(Native.OrderBy(path => JsonNode.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")))!["platform"]!.GetValue<string>(), StringComparer.Ordinal)).ToArray();
            var inputs = receipt["inputs"]!.AsArray();
            for (var index = 0; index < ordered.Length; index++)
                inputs[index]!["manifestSha256"] = Sha256(Path.Combine(ordered[index], "manifest.json"));
            await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        }
        public async Task RefreshOutputHashesAsync()
        {
            var receiptPath = Path.Combine(Output, "verification.json");
            var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
            receipt["outputSha256"] = Sha256(Path.Combine(Output, "coverage.cobertura.xml"));
            receipt["primaryPathsSha256"] = Sha256(Path.Combine(Output, "primary-paths.cobertura.xml"));
            await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

        private static object Policy() => new
        {
            driver = "collector",
            format = "cobertura",
            exclude = "[*.Tests]*,[*.IntegrationTests]*",
            include = (string?)null,
            normalization = "appsurface-coverage-merge",
        };

        public string Receipt => Path.Combine(Output, "verification.json");
        public EvidenceCoverageFanInRequest Request => new(
            Primary, Native, Output, Repository, Solution,
            new HashSet<string> { "tests/Foo.Tests/Foo.Tests.csproj" },
            Commit, Tree, Sdk, "Debug", Run, PrimaryGate, ComposedGate, "diff");

        public static async Task MutateJsonAsync(string path, Action<JsonObject> mutate)
        {
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            mutate(document);
            await File.WriteAllTextAsync(path, document.ToJsonString());
        }

        public async Task MutateXmlAsync(string directory, string file, Action<XElement> mutate)
        {
            var path = TestPathUtils.PathUnder(directory, file);
            var document = XDocument.Load(path);
            mutate(document.Root!);
            await File.WriteAllTextAsync(path, document.ToString());
            if (directory != Output)
                await RefreshManifestAndReceiptAsync(directory, file);
        }

        public async Task WriteDocumentsAsync(XDocument primary, XDocument[] natives, XDocument composed)
        {
            await File.WriteAllTextAsync(Path.Combine(Primary, "coverage.cobertura.xml"), primary.ToString());
            await RefreshManifestAndReceiptAsync(Primary, "coverage.cobertura.xml");
            for (var index = 0; index < natives.Length; index++)
            {
                foreach (var file in new[] { "coverage.cobertura.xml", "collector.cobertura.xml" })
                {
                    await File.WriteAllTextAsync(TestPathUtils.PathUnder(Native[index], file), natives[index].ToString());
                    await RefreshManifestAndReceiptAsync(Native[index], file);
                }
            }
            await File.WriteAllTextAsync(Path.Combine(Output, "coverage.cobertura.xml"), composed.ToString());
            await File.WriteAllTextAsync(Path.Combine(Output, "primary-paths.cobertura.xml"), primary.ToString());
            var inputs = new[] { primary, natives[2], natives[0], natives[1] };
            await MutateJsonAsync(Receipt, receipt =>
            {
                receipt["inventory"] = JsonSerializer.SerializeToNode(new
                {
                    inputClassLines = inputs.Select(x => x.Descendants("class").Elements("lines").Elements("line").Count()).ToArray(),
                    inputMethodLines = inputs.Select(x => x.Descendants("method").Elements("lines").Elements("line").Count()).ToArray(),
                    retainedClassLines = (int)composed.Root!.Attribute("lines-valid")!,
                    retainedMethodLines = composed.Descendants("method").Elements("lines").Elements("line").Count(),
                    linesCovered = (int)composed.Root.Attribute("lines-covered")!,
                    branchesCovered = (int)composed.Root.Attribute("branches-covered")!,
                    branchesValid = (int)composed.Root.Attribute("branches-valid")!,
                });
            });
            await RefreshOutputHashesAsync();
            await SetGateCountsAsync(PrimaryGate, primary);
            await SetGateCountsAsync(ComposedGate, composed);
        }

        private static Task SetGateCountsAsync(string path, XDocument document) => MutateJsonAsync(path, gate =>
        {
            foreach (var (name, prefix) in new[] { ("line", "lines"), ("branch", "branches") })
            {
                var covered = (int)document.Root!.Attribute(prefix + "-covered")!;
                var valid = (int)document.Root.Attribute(prefix + "-valid")!;
                gate[name] = JsonSerializer.SerializeToNode(new { covered, valid, percent = valid == 0 ? 100 : covered * 100d / valid });
            }
        });
    }
}
