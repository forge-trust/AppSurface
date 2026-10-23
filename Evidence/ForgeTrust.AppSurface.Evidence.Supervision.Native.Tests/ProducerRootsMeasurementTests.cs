using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Detached JSON contract controls only: no owner, account, capability, process or native proof is constructed.</summary>
public sealed class ProducerRootsMeasurementTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Base = "416b30919e6c4d03b634d9d3fe558884b02f0b91";
    private const string Supervision = "ForgeTrust.AppSurface.Evidence.Supervision";
    private const string MutationDocument = "Evidence/" + Supervision + "/LinuxRetainedReportSet.cs";
    private const string QuotaDocument = "Evidence/" + Supervision + "/SupervisionRunOutputBudget.cs";
    private const string MutationHash = "95ca9977655b70408a238336390438d6db0fce4ae504bf95c2f006535a6105d4";
    private const string QuotaHash = "1603bed8d52f248cbca947dbe5f97827b2df6857727ecebc2482b1f5c6825f7d";
    private static readonly string[] Cases = ["normal", "test-failure", "cancel", "quota", "ignored-cancel",
        "artifact-mutation", "cleanup-failure"];

    /// <summary>The model is internally consistent; successful comparison grants no native acceptance.</summary>
    [Fact]
    public void DetachedV2ModelHasNoAuthority()
    {
        var fixture = new Fixture();
        fixture.Packets().Validate();
        Assert.False(fixture.Binding["native_acceptance"]!.GetValue<bool>());
        Assert.False(fixture.Binding["worker_imported"]!.GetValue<bool>());
        Assert.All(fixture.Rows, static row => Assert.False(row!["native_acceptance"]!.GetValue<bool>()));
    }

    /// <summary>Each eligibility flag and closed schema is checked after a valid adjacent packet.</summary>
    [Theory]
    [InlineData("v1")]
    [InlineData("not-ready")]
    [InlineData("worker-imported")]
    [InlineData("not-root-only")]
    [InlineData("acceptance")]
    [InlineData("outer-exit")]
    [InlineData("inner-exit")]
    [InlineData("driver-exit")]
    [InlineData("boolean-exit")]
    [InlineData("fractional-exit")]
    [InlineData("extra-field")]
    public void GatePacketRejectsOneChangedField(string mutation)
    {
        var fixture = new Fixture();
        fixture.Packets().Validate();
        switch (mutation)
        {
            case "v1": fixture.Binding["schema"] = "issue779-producer-roots-measurement-v1"; break;
            case "not-ready": fixture.Binding["ready_for_gate"] = false; break;
            case "worker-imported": fixture.Binding["worker_imported"] = true; break;
            case "not-root-only": fixture.Binding["root_only"] = false; break;
            case "acceptance": fixture.Binding["native_acceptance"] = true; break;
            case "outer-exit": fixture.Binding["outer_collector_exit"] = 1; break;
            case "inner-exit": fixture.Binding["inner_collector_exit"] = -1; break;
            case "driver-exit": fixture.Binding["driver_exit"] = 1; break;
            case "boolean-exit": fixture.Binding["driver_exit"] = false; break;
            case "fractional-exit": fixture.Binding["driver_exit"] = JsonNode.Parse("0.0"); break;
            case "extra-field": fixture.Binding["acceptance_override"] = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.Packets().Validate());
    }

    /// <summary>Both variants require all fourteen restored hashes and seven changed DLLs, not a projected image map.</summary>
    [Theory]
    [InlineData("None", "restoration")]
    [InlineData("C2", "restoration")]
    [InlineData("None", "unchanged-dll")]
    [InlineData("C2", "unchanged-dll")]
    [InlineData("None", "missing-pdb")]
    [InlineData("C2", "missing-pdb")]
    [InlineData("None", "extra-module")]
    [InlineData("C2", "bad-hash")]
    public void ImageMapsRemainCompleteAndRestored(string variant, string mutation)
    {
        var fixture = new Fixture();
        fixture.Packets().Validate();
        var original = fixture.Binding["original"]![variant]!.AsObject();
        var instrumented = fixture.Binding["instrumented"]![variant]!.AsObject();
        var restored = fixture.Binding["restored"]![variant]!.AsObject();
        const string dll = "ForgeTrust.AppSurface.Evidence.Coverage.dll";
        switch (mutation)
        {
            case "restoration": restored[dll] = new string('d', 64); break;
            case "unchanged-dll": instrumented[dll] = original[dll]!.GetValue<string>(); break;
            case "missing-pdb": instrumented.Remove("ForgeTrust.AppSurface.Evidence.Coverage.pdb"); break;
            case "extra-module": original["foreign.dll"] = new string('a', 64); break;
            case "bad-hash": instrumented[dll] = new string('B', 64); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.Packets().Validate());
    }

    /// <summary>All fourteen leaves are required in each original, instrumented and restored variant map.</summary>
    [Theory]
    [InlineData("None", "original", ".dll")]
    [InlineData("None", "original", ".pdb")]
    [InlineData("None", "instrumented", ".dll")]
    [InlineData("None", "instrumented", ".pdb")]
    [InlineData("None", "restored", ".dll")]
    [InlineData("None", "restored", ".pdb")]
    [InlineData("C2", "original", ".dll")]
    [InlineData("C2", "original", ".pdb")]
    [InlineData("C2", "instrumented", ".dll")]
    [InlineData("C2", "instrumented", ".pdb")]
    [InlineData("C2", "restored", ".dll")]
    [InlineData("C2", "restored", ".pdb")]
    public void SeventhImageLeavesCannotBeRemoved(string variant, string kind, string suffix)
    {
        var fixture = new Fixture();
        fixture.Packets().Validate();
        fixture.Binding[kind]![variant]!.AsObject().Remove("ForgeTrust.AppSurface.Evidence.Cli" + suffix);
        Assert.ThrowsAny<Exception>(() => fixture.Packets().Validate());
    }

    /// <summary>The seventh DLL must change under instrumentation and match its original after restoration.</summary>
    [Theory]
    [InlineData("None", "unchanged")]
    [InlineData("C2", "unchanged")]
    [InlineData("None", "restoration")]
    [InlineData("C2", "restoration")]
    [InlineData("None", "hash")]
    [InlineData("C2", "hash")]
    public void SeventhImageHashesRemainBound(string variant, string mutation)
    {
        const string module = "ForgeTrust.AppSurface.Evidence.Cli.dll";
        var fixture = new Fixture();
        fixture.Packets().Validate();
        switch (mutation)
        {
            case "unchanged": fixture.Binding["instrumented"]![variant]![module] = new string('a', 64); break;
            case "restoration": fixture.Binding["restored"]![variant]![module] = new string('d', 64); break;
            case "hash": fixture.Binding["original"]![variant]![module] = new string('B', 64); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.Packets().Validate());
    }

    /// <summary>Rebound inner, combined and rollback report digests cannot hide an omitted or duplicate seventh module.</summary>
    [Theory]
    [InlineData("inner", false)]
    [InlineData("combined", false)]
    [InlineData("rollback", false)]
    [InlineData("inner", true)]
    [InlineData("combined", true)]
    [InlineData("rollback", true)]
    public void SeventhReportModuleCannotBeReplacedByAnotherBasename(string partition, bool duplicate)
    {
        var fixture = new Fixture();
        fixture.Packets().Validate();
        var report = partition switch
        {
            "inner" => fixture.Inner,
            "combined" => fixture.Combined,
            "rollback" => fixture.Rollback.Report,
            _ => throw new ArgumentOutOfRangeException(nameof(partition))
        };
        report.Remove("ForgeTrust.AppSurface.Evidence.Cli.dll");
        if (duplicate) report["/duplicate/ForgeTrust.AppSurface.Cli.dll"] = new JsonObject();
        Assert.ThrowsAny<Exception>(() => fixture.Packets().Validate());
    }

    /// <summary>Full capture metadata and fixed context remain bound even when an invalid packet's hashes are recomputed.</summary>
    [Theory]
    [InlineData("count")]
    [InlineData("source-path")]
    [InlineData("source-mode")]
    [InlineData("source-blob")]
    [InlineData("missing-required-source")]
    [InlineData("base")]
    [InlineData("head")]
    [InlineData("relative-path")]
    [InlineData("same-tool-image")]
    public void SourceAndContextRetainTheirRequiredMetadata(string mutation)
    {
        var fixture = new Fixture();
        fixture.Packets().Validate();
        var files = fixture.Source["files"]!.AsObject();
        switch (mutation)
        {
            case "count": fixture.Source["count"] = files.Count - 1; break;
            case "source-path":
                var original = files[".github/issue779-checkpoint4/producer_roots.py"]!.DeepClone();
                files.Remove(".github/issue779-checkpoint4/producer_roots.py");
                files["../producer_roots.py"] = original;
                break;
            case "source-mode": files[MutationDocument]!["mode"] = "100644"; break;
            case "source-blob": files[MutationDocument]!["git_blob"] = new string('g', 40); break;
            case "missing-required-source":
                files.Remove(".github/issue779-checkpoint4/producer_roots.py");
                fixture.Source["count"] = files.Count;
                break;
            case "base": fixture.Binding["context"]!["base"] = new string('a', 40); break;
            case "head": fixture.Binding["context"]!["source"] = new string('b', 40); break;
            case "relative-path": fixture.Binding["context"]!["paths"]!["runtime"] = "runtime"; break;
            case "same-tool-image": fixture.Binding["context"]!["paths"]!["mutation_tool"] = "/fixture/tool"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.Packets().Validate());
    }

    /// <summary>Native semantics are tested with rebound aggregate hashes so stale digests cannot mask the intended guard.</summary>
    [Theory]
    [InlineData("header-exit")]
    [InlineData("header-trusted")]
    [InlineData("order")]
    [InlineData("duplicate-generation")]
    [InlineData("normal-exit")]
    [InlineData("negative-exit")]
    [InlineData("groups")]
    [InlineData("final-groups")]
    [InlineData("failure-category")]
    [InlineData("inventory")]
    [InlineData("accounts")]
    [InlineData("cleanup-latch")]
    [InlineData("cleanup-absence")]
    [InlineData("cancel")]
    [InlineData("descendant")]
    [InlineData("quota-boundary")]
    [InlineData("quota-limit")]
    [InlineData("quota-boolean")]
    [InlineData("mutation-equal")]
    [InlineData("mutation-length")]
    [InlineData("mutation-inode")]
    [InlineData("mutation-returned")]
    public void NativeRowsRequireTheExistingSevenCaseContract(string mutation)
    {
        var fixture = new Fixture();
        fixture.Packets().Validate();
        var quota = fixture.Rows[3]!["output_observation"]!;
        var events = fixture.Rows[5]!["artifact_mutation"]!;
        switch (mutation)
        {
            case "header-exit": fixture.Native["exit"] = false; break;
            case "header-trusted": fixture.Native["trusted_eligibility"] = true; break;
            case "order": fixture.Rows[0]!["case"] = "test-failure"; break;
            case "duplicate-generation": fixture.Rows[1]!["generation"] = fixture.Rows[0]!["generation"]!.GetValue<string>(); break;
            case "normal-exit": fixture.Rows[0]!["owner_terminal"]!["wrapper_exit"] = 1; break;
            case "negative-exit": fixture.Rows[1]!["owner_terminal"]!["wrapper_exit"] = 0; break;
            case "groups": fixture.Rows[0]!["groups_empty"] = false; break;
            case "final-groups": fixture.Rows[0]!["final_groups_empty"] = false; break;
            case "failure-category": fixture.Rows[0]!["failure_category"] = null; break;
            case "inventory": fixture.Rows[0]!["input_inventory_sha256"] = new string('d', 64); break;
            case "accounts": fixture.Rows[0]!["accounts_absent"] = false; break;
            case "cleanup-latch": fixture.Rows[6]!["quarantined"] = false; break;
            case "cleanup-absence": fixture.Rows[6]!["accounts_absent"] = true; break;
            case "cancel": fixture.Rows[2]!["cancel_sent"] = false; break;
            case "descendant": fixture.Rows[4]!["descendant_term_observed"] = false; break;
            case "quota-boundary": quota["received_bytes"] = 16 * 1024 * 1024; break;
            case "quota-limit": quota["received_byte_limit"] = 16 * 1024 * 1024 - 1; break;
            case "quota-boolean": quota["received_bytes"] = true; break;
            case "mutation-equal": events[0]!["changed_sha256"] = events[0]!["original_sha256"]!.GetValue<string>(); break;
            case "mutation-length": events[0]!["length"] = 0; break;
            case "mutation-inode": events[0]!["inode"] = 0; break;
            case "mutation-returned": events[1]!["returned_bytes"] = 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.Packets().Validate());
    }

    /// <summary>Actual contract boundaries permit a one-MiB mutation and nonzero failed-wrapper codes without changing policy.</summary>
    [Fact]
    public void NativeMetadataAcceptsItsDocumentedBoundaryNeighbors()
    {
        var fixture = new Fixture();
        fixture.Packets().Validate();
        fixture.Rows[5]!["artifact_mutation"]![0]!["length"] = 1024 * 1024;
        fixture.Packets().Validate();
        fixture.Rows[5]!["artifact_mutation"]![0]!["length"] = 1024 * 1024 + 1;
        Assert.ThrowsAny<Exception>(() => fixture.Packets().Validate());
        fixture.Rows[5]!["artifact_mutation"]![0]!["length"] = 1024 * 1024;
        fixture.Rows[1]!["owner_terminal"]!["wrapper_exit"] = -6;
        fixture.Packets().Validate();
    }

    /// <summary>Reviewed source hashes and exact spans prevent widening a quota-arm witness to other method hits.</summary>
    [Theory]
    [InlineData("C2", "zero")]
    [InlineData("None", "zero")]
    [InlineData("C2", "source-hash")]
    [InlineData("None", "source-hash")]
    [InlineData("C2", "capture-hash")]
    [InlineData("None", "capture-hash")]
    [InlineData("C2", "method")]
    [InlineData("None", "type")]
    [InlineData("C2", "span")]
    [InlineData("None", "span")]
    [InlineData("None", "neighbor-line")]
    [InlineData("C2", "combined-zero")]
    [InlineData("C2", "document-alias")]
    public void CurrentWitnessCannotBorrowAnUnrelatedPositiveLine(string variant, string mutation)
    {
        var fixture = new Fixture();
        fixture.Packets().Validate();
        var witness = fixture.Binding["current_witness"]![variant]!;
        var report = variant == "C2" ? fixture.Inner : fixture.Combined;
        var lines = Fixture.WitnessLines(report, variant == "C2");
        switch (mutation)
        {
            case "zero": foreach (var key in lines.Select(static row => row.Key).ToArray()) lines[key] = 0; break;
            case "source-hash": witness["source_sha256"] = new string('d', 64); break;
            case "capture-hash": fixture.Source["files"]![variant == "C2" ? MutationDocument : QuotaDocument]!["sha256"] = new string('d', 64); break;
            case "method": witness["method"] = "System.Void Other::Run()"; break;
            case "type": witness["type"] = "Other"; break;
            case "span": witness["start"] = 1; break;
            case "neighbor-line": lines.Clear(); lines["172"] = 1; break;
            case "combined-zero": Fixture.WitnessLines(fixture.Combined, mutation: true)["148"] = 0; break;
            case "document-alias":
                var documents = report[Supervision + ".dll"]!.AsObject();
                documents["/other/" + MutationDocument] = documents["/source/" + MutationDocument]!.DeepClone();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.Packets().Validate());
    }

    /// <summary>Shape checks inspect actual raw standard report values without computing or changing coverage.</summary>
    [Theory]
    [InlineData("negative-line")]
    [InlineData("boolean-line")]
    [InlineData("fractional-line")]
    [InlineData("overflow-line")]
    [InlineData("zero-line-number")]
    [InlineData("negative-branch")]
    [InlineData("missing-branch-field")]
    [InlineData("extra-module")]
    [InlineData("duplicate-module-leaf")]
    public void StandardReportRejectsInvalidCounterData(string mutation)
    {
        var fixture = new Fixture();
        fixture.Packets().Validate();
        var lines = Fixture.WitnessLines(fixture.Inner, mutation: true);
        var method = Fixture.WitnessMethod(fixture.Inner, mutation: true);
        switch (mutation)
        {
            case "negative-line": lines["148"] = -1; break;
            case "boolean-line": lines["148"] = true; break;
            case "fractional-line": lines["148"] = JsonNode.Parse("1.5"); break;
            case "overflow-line": lines["148"] = JsonNode.Parse("9223372036854775808"); break;
            case "zero-line-number": lines["0"] = 0; break;
            case "negative-branch": method["Branches"]![0]!["Hits"] = -1; break;
            case "missing-branch-field": method["Branches"]![0]!.AsObject().Remove("Ordinal"); break;
            case "extra-module": fixture.Inner["foreign.dll"] = new JsonObject(); break;
            case "duplicate-module-leaf": fixture.Inner["/other/ForgeTrust.AppSurface.Cli.dll"] = new JsonObject(); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.Packets().Validate());
    }

    /// <summary>A different run/attempt cannot be smuggled in through a plausible rollback artifact name.</summary>
    [Theory]
    [InlineData("kind")]
    [InlineData("variant")]
    [InlineData("run")]
    [InlineData("attempt")]
    [InlineData("leading-zero")]
    [InlineData("order")]
    [InlineData("extra-field")]
    [InlineData("rollback-v1")]
    [InlineData("rollback-prior-kind")]
    public void ExternalPredecessorMatchesItsImmediateSuppliedPacket(string mutation)
    {
        var fixture = new Fixture();
        fixture.Packets().Validate();
        var predecessor = fixture.Binding["external_predecessor"]!;
        switch (mutation)
        {
            case "kind": predecessor["kind"] = "accounts"; break;
            case "variant": predecessor["variant"] = "C4Accounts"; break;
            case "run": predecessor["artifact_name"] = "issue779-c4-accounts-C4AccountRollback-100-1"; break;
            case "attempt": predecessor["artifact_name"] = "issue779-c4-accounts-C4AccountRollback-99-2"; break;
            case "leading-zero": predecessor["artifact_name"] = "issue779-c4-accounts-C4AccountRollback-099-1"; break;
            case "order": predecessor["merge_order"]![3] = "None"; break;
            case "extra-field": predecessor["native_acceptance"] = false; break;
            case "rollback-v1": fixture.Rollback.Binding["schema"] = "issue779-c4-account-measurement-v1"; break;
            case "rollback-prior-kind": fixture.Rollback.Binding["prior_kind"] = "c3"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.Packets().Validate());
    }

    /// <summary>The bind-time context retains the identical predecessor; object member order is immaterial.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("digest")]
    [InlineData("artifact")]
    [InlineData("order")]
    [InlineData("extra")]
    [InlineData("reordered")]
    public void ContextAndFinalPredecessorCannotDiverge(string mutation)
    {
        var packet = new Fixture().Packets();
        packet.Validate();
        var binding = JsonNode.Parse(packet.Inputs[1])!.AsObject();
        var context = binding["context"]!.AsObject();
        var predecessor = context["external_predecessor"]!.AsObject();
        switch (mutation)
        {
            case "missing": context.Remove("external_predecessor"); break;
            case "null": context["external_predecessor"] = null; break;
            case "digest": predecessor["coverage_sha256"] = new string('d', 64); break;
            case "artifact": predecessor["artifact_name"] = "issue779-c4-accounts-C4AccountRollback-100-1"; break;
            case "order": predecessor["merge_order"]![3] = "None"; break;
            case "extra": predecessor["replacement"] = false; break;
            case "reordered":
                var reversed = new JsonObject();
                foreach (var row in predecessor.Reverse()) reversed[row.Key] = row.Value!.DeepClone();
                context["external_predecessor"] = reversed;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        packet.Inputs[1] = JsonSerializer.SerializeToUtf8Bytes(binding);
        if (mutation == "reordered") packet.Validate();
        else Assert.ThrowsAny<Exception>(() => packet.Validate());
    }

    /// <summary>Changing one supplied raw packet without updating its binding fails even when the JSON is still valid.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void RawBytesAreBoundIncludingInsignificantWhitespace(int index)
    {
        var packet = new Fixture().Packets();
        packet.Validate();
        packet.Inputs[index] = packet.Inputs[index].Concat(new[] { (byte)' ' }).ToArray();
        Assert.ThrowsAny<Exception>(() => packet.Validate());
    }

    /// <summary>Each raw-input digest and length claim is independently checked against the supplied bytes.</summary>
    [Theory]
    [InlineData("source")]
    [InlineData("native")]
    [InlineData("native-bytes")]
    [InlineData("combined-bytes")]
    [InlineData("inner-bytes")]
    [InlineData("predecessor-source")]
    [InlineData("predecessor-binding")]
    [InlineData("predecessor-coverage")]
    [InlineData("predecessor-native")]
    public void DigestClaimsCannotPointToOtherWellFormedBytes(string mutation)
    {
        var packet = new Fixture().Packets();
        packet.Validate();
        var binding = JsonNode.Parse(packet.Inputs[1])!.AsObject();
        switch (mutation)
        {
            case "source": binding["context"]!["source_capture_sha256"] = new string('d', 64); break;
            case "native": binding["native_receipt_sha256"] = new string('d', 64); break;
            case "native-bytes": binding["native_receipt_bytes"] = packet.Inputs[4].Length + 1; break;
            case "combined-bytes": binding["reports"]!["native-producer-roots.json"]!["bytes"] = packet.Inputs[2].Length + 1; break;
            case "inner-bytes": binding["reports"]!["native-producer-roots-c2.json"]!["bytes"] = packet.Inputs[3].Length + 1; break;
            case "predecessor-source": binding["external_predecessor"]!["source_capture_sha256"] = new string('d', 64); break;
            case "predecessor-binding": binding["external_predecessor"]!["input_binding_sha256"] = new string('d', 64); break;
            case "predecessor-coverage": binding["external_predecessor"]!["coverage_sha256"] = new string('d', 64); break;
            case "predecessor-native": binding["external_predecessor"]!["native_receipt_sha256"] = new string('d', 64); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        packet.Inputs[1] = JsonSerializer.SerializeToUtf8Bytes(binding);
        Assert.ThrowsAny<Exception>(() => packet.Validate());
    }

    /// <summary>Metadata case aliases are duplicates; standard report names retain ordinal case-sensitive semantics.</summary>
    [Theory]
    [InlineData("schema")]
    [InlineData("SCHEMA")]
    [InlineData("nested-report")]
    [InlineData("nested-metadata-alias")]
    public void DuplicateMembersCannotReplaceObservations(string mutation)
    {
        var packet = new Fixture().Packets();
        packet.Validate();
        if (mutation == "nested-report")
        {
            var text = Encoding.UTF8.GetString(packet.Inputs[3]);
            const string marker = "\"Lines\":{\"148\":1}";
            Assert.Contains(marker, text, StringComparison.Ordinal);
            packet.Inputs[3] = Encoding.UTF8.GetBytes(text.Replace(marker, marker + ",\"Lines\":{\"148\":1}", StringComparison.Ordinal));
            var binding = JsonNode.Parse(packet.Inputs[1])!.AsObject();
            binding["reports"]!["native-producer-roots-c2.json"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(packet.Inputs[3]));
            binding["reports"]!["native-producer-roots-c2.json"]!["bytes"] = packet.Inputs[3].Length;
            packet.Inputs[1] = JsonSerializer.SerializeToUtf8Bytes(binding);
        }
        else if (mutation == "nested-metadata-alias")
        {
            var text = Encoding.UTF8.GetString(packet.Inputs[4]);
            const string marker = "\"passed\":true";
            Assert.Contains(marker, text, StringComparison.Ordinal);
            packet.Inputs[4] = Encoding.UTF8.GetBytes(text.Replace(marker, marker + ",\"PASSED\":false", StringComparison.Ordinal));
            var binding = JsonNode.Parse(packet.Inputs[1])!.AsObject();
            binding["native_receipt_sha256"] = Convert.ToHexStringLower(SHA256.HashData(packet.Inputs[4]));
            binding["native_receipt_bytes"] = packet.Inputs[4].Length;
            packet.Inputs[1] = JsonSerializer.SerializeToUtf8Bytes(binding);
        }
        else
        {
            var text = Encoding.UTF8.GetString(packet.Inputs[1]);
            packet.Inputs[1] = Encoding.UTF8.GetBytes(text[..^1] + ",\"" + mutation + "\":\"issue779-producer-roots-measurement-v2\"}");
        }
        Assert.ThrowsAny<Exception>(() => packet.Validate());
    }

    /// <summary>Distinct report method names differing by case are not metadata aliases.</summary>
    [Fact]
    public void StandardReportNamesUseOrdinalComparison()
    {
        var fixture = new Fixture();
        fixture.Packets().Validate();
        var documents = fixture.Inner["ForgeTrust.AppSurface.Cli.dll"]!.AsObject();
        documents["/source/DataOnly.cs"] = new JsonObject
        {
            ["DataOnly"] = new JsonObject { ["Method"] = Fixture.Method(1), ["method"] = Fixture.Method(1) }
        };
        fixture.Packets().Validate();
    }

    /// <summary>Every input retains its own declared byte limit before parsing; these are not native lifecycle controls.</summary>
    [Theory]
    [InlineData(0, 8)]
    [InlineData(1, 8)]
    [InlineData(2, 32)]
    [InlineData(3, 32)]
    [InlineData(4, 8)]
    [InlineData(5, 8)]
    [InlineData(6, 32)]
    [InlineData(7, 8)]
    public void OversizedRawInputIsRejectedBeforeParsing(int index, int maximumMib)
    {
        var packet = new Fixture().Packets();
        packet.Validate();
        packet.Inputs[index] = new byte[maximumMib * 1024 * 1024 + 1];
        Assert.ThrowsAny<Exception>(() => packet.Validate());
    }

    /// <summary>Rebound prior reports cannot supply either current witness; missing data stays absent and unchanged.</summary>
    [Theory]
    [InlineData("quota-method-absent", true)]
    [InlineData("quota-point-absent", true)]
    [InlineData("quota-zero", true)]
    [InlineData("quota-alias-zero", true)]
    [InlineData("quota-positive", false)]
    [InlineData("quota-alias-positive", false)]
    [InlineData("quota-alias-two-zero", false)]
    [InlineData("quota-ambiguous-documents", false)]
    [InlineData("quota-wrong-document-positive", true)]
    [InlineData("c2-zero", false)]
    [InlineData("c2-positive", false)]
    [InlineData("c2-wrong-document-zero", false)]
    [InlineData("c2-wrong-document-positive", false)]
    public void PriorContributionCannotStandInForCurrentExecution(string mutation, bool valid)
    {
        var fixture = new Fixture();
        var baseline = fixture.Packets();
        var baselineReport = baseline.Inputs[6].ToArray();
        baseline.Validate();
        Assert.Equal(baselineReport, baseline.Inputs[6]);
        var documents = fixture.Rollback.Supervision;
        var isC2 = mutation.StartsWith("c2-", StringComparison.Ordinal);
        var type = Supervision + (isC2 ? ".LinuxRetainedReportSet" : ".SupervisionRunOutputBudget");
        var methodName = isC2 ? "System.Byte[] " + type + "::MutateRetainedReportForC2(System.Threading.CancellationToken)"
            : "System.Boolean " + type + "::Charge(System.Int32)";
        var wrongDocument = mutation.Contains("wrong-document", StringComparison.Ordinal);
        var path = wrongDocument ? "/unrelated/Other.cs" : "/prior/" + (isC2 ? MutationDocument : QuotaDocument);
        var method = Fixture.Method(isC2 ? 148 : 172);
        var lines = method["Lines"]!.AsObject();
        if (isC2) lines["148"] = mutation.EndsWith("-zero", StringComparison.Ordinal) ? 0 : 1;
        else
        {
            if (mutation is "quota-zero" or "quota-ambiguous-documents") lines["173"] = 0;
            if (mutation is "quota-positive" or "quota-wrong-document-positive") lines["173"] = 1;
            if (mutation == "quota-alias-zero") lines["0173"] = 0;
            if (mutation == "quota-alias-positive") lines["0173"] = 1;
            if (mutation == "quota-alias-two-zero") { lines["173"] = 0; lines["0173"] = 0; }
        }
        var methods = new JsonObject();
        if (mutation != "quota-method-absent") methods[methodName] = method;
        documents[path] = new JsonObject { [type] = methods };
        if (mutation == "quota-ambiguous-documents") documents[QuotaDocument] = documents[path]!.DeepClone();
        // Rebuild rollback's own hashes and both predecessor copies, preserving the genuine current-witness controls.
        var packet = fixture.Packets();
        var originalReport = packet.Inputs[6].ToArray();
        if (valid) packet.Validate();
        else Assert.ThrowsAny<Exception>(() => packet.Validate());
        Assert.Equal(originalReport, packet.Inputs[6]);
    }

    private sealed record Packet(byte[][] Inputs)
    {
        internal void Validate() => ProducerRootsMeasurement.Validate(Inputs[0], Inputs[1], Inputs[2], Inputs[3],
            Inputs[4], Inputs[5], Inputs[6], Inputs[7]);
    }

    // Synthetic metadata models deliberately do not satisfy independent full-source/artifact/native authentication.
    private sealed class Fixture
    {
        internal AccountMeasurementTests.Fixture Rollback { get; } = new("C4AccountRollback");
        internal JsonObject Source => Rollback.Source;
        internal JsonObject Binding { get; } = new();
        internal JsonObject Native { get; } = new();
        internal JsonArray Rows { get; } = new();
        internal JsonObject Combined { get; }
        internal JsonObject Inner { get; } = new();

        internal Fixture()
        {
            Rollback.ConfigureMerged();
            foreach (var module in Rollback.Report)
            {
                Inner[module.Key] = new JsonObject();
                foreach (var document in module.Value!.AsObject())
                    foreach (var type in document.Value!.AsObject())
                        foreach (var method in type.Value!.AsObject()) method.Value!["Branches"] = new JsonArray();
            }
            Combined = Rollback.Report.DeepClone().AsObject();
            foreach (var row in new[] { (MutationDocument, MutationHash), (QuotaDocument, QuotaHash),
                (".github/issue779-checkpoint4/producer_roots.py", new string('c', 64)),
                (".github/issue779-checkpoint2/run-native.py", new string('c', 64)) })
                Source["files"]![row.Item1] = new JsonObject
                { ["sha256"] = row.Item2, ["git_blob"] = new string('c', 40), ["mode"] = "0644" };
            Source["count"] = Source["files"]!.AsObject().Count;
            Rollback.Binding["prior_kind"] = "accounts";
            Rollback.Binding["prior_variant"] = "C4Accounts";
            Rollback.Binding["prior_artifact_name"] = "issue779-c4-accounts-C4Accounts-99-1";
            Rollback.Binding["prior_root_trx_sha256"] = null;
            foreach (var key in new[] { "prior_input_binding_sha256", "prior_coverage_sha256", "prior_native_receipt_sha256" })
                Rollback.Binding[key] = new string('c', 64);
            Binding["schema"] = "issue779-producer-roots-measurement-v2";
            Binding["root_only"] = true; Binding["worker_imported"] = false;
            Binding["native_acceptance"] = false; Binding["ready_for_gate"] = true;
            Binding["context"] = new JsonObject { ["source"] = Head, ["base"] = Base,
                ["package_smoke_sha256"] = new string('c', 64), ["paths"] = new JsonObject
                { ["tool"] = "/fixture/tool", ["mutation_tool"] = "/fixture/mutation", ["runtime"] = "/fixture/runtime",
                    ["subject"] = "/fixture/subject", ["evidence"] = "/fixture/evidence" } };
            foreach (var kind in new[] { "original", "instrumented", "restored" })
                Binding[kind] = new JsonObject { ["None"] = Rollback.Binding[kind == "restored" ? "original" : kind]!.DeepClone(),
                    ["C2"] = Rollback.Binding[kind == "restored" ? "original" : kind]!.DeepClone() };
            foreach (var key in new[] { "original_binding_sha256", "instrumented_binding_sha256", "inner_exit_sha256",
                "driver_exit_sha256", "input_inventory_sha256" }) Binding[key] = new string('c', 64);
            foreach (var key in new[] { "outer_collector_exit", "inner_collector_exit", "driver_exit" }) Binding[key] = 0;
            var caseHashes = new JsonObject();
            foreach (var name in Cases) caseHashes[name] = new string('c', 64);
            Binding["case_receipt_sha256"] = caseHashes;
            Binding["external_predecessor"] = new JsonObject { ["kind"] = "accounts-rollback", ["variant"] = "C4AccountRollback",
                ["artifact_name"] = "issue779-c4-accounts-C4AccountRollback-99-1",
                ["merge_order"] = JsonSerializer.SerializeToNode(new[] { "C3", "C4Accounts", "C4AccountRollback", "C2", "None" }) };
            Binding["current_witness"] = new JsonObject { ["C2"] = Witness(mutation: true), ["None"] = Witness(mutation: false) };
            AddWitness(Inner, mutation: true);
            AddWitness(Combined, mutation: true);
            AddWitness(Combined, mutation: false);
            Native["schema"] = "issue779-csharp-checkpoint2-native-v1";
            Native["source"] = Head; Native["base"] = Base; Native["exit"] = 0;
            Native["native_acceptance"] = false; Native["trusted_eligibility"] = false; Native["cases"] = Rows;
            for (var index = 0; index < Cases.Length; index++)
            {
                var name = Cases[index];
                var cleanup = name == "cleanup-failure";
                var row = new JsonObject { ["case"] = name, ["generation"] = index.ToString("x32", System.Globalization.CultureInfo.InvariantCulture),
                    ["passed"] = true, ["groups_empty"] = true, ["final_groups_empty"] = true,
                    ["native_acceptance"] = false, ["accounts_absent"] = !cleanup, ["accounts_quarantined"] = cleanup,
                    ["owner_terminal"] = new JsonObject { ["wrapper_exit"] = index == 0 ? 0 : 1 },
                    ["input_inventory_sha256"] = new string('c', 64), ["stdout_sha256"] = new string('c', 64),
                    ["stderr_sha256"] = new string('c', 64) };
                if (cleanup) row["quarantined"] = true;
                if (name is "cancel" or "ignored-cancel") row["cancel_sent"] = true;
                if (name == "ignored-cancel") row["descendant_term_observed"] = true;
                if (name == "quota") row["output_observation"] = new JsonObject { ["quota_exceeded"] = true,
                    ["received_byte_limit"] = 16 * 1024 * 1024, ["received_bytes"] = 16 * 1024 * 1024 + 1 };
                if (name == "artifact-mutation") row["artifact_mutation"] = new JsonArray
                {
                    new JsonObject { ["schema"] = "evidence-c2-artifact-mutation-v1", ["phase"] = "mutated",
                        ["original_sha256"] = new string('a', 64), ["changed_sha256"] = new string('b', 64),
                        ["length"] = 1, ["device_major"] = 0, ["device_minor"] = 1, ["inode"] = 1 },
                    new JsonObject { ["schema"] = "evidence-c2-artifact-mutation-v1", ["phase"] = "read-rejected", ["returned_bytes"] = 0 }
                };
                Rows.Add(row);
            }
        }

        internal Packet Packets()
        {
            var source = Serialize(Source);
            Rollback.Binding["prior_source_capture_sha256"] = Digest(source);
            var rollback = Rollback.Packets();
            var native = Serialize(Native);
            var inner = Serialize(Inner);
            var combined = Serialize(Combined);
            Binding["context"]!["source_capture_sha256"] = Digest(source);
            Binding["native_receipt_sha256"] = Digest(native);
            Binding["native_receipt_bytes"] = native.Length;
            Binding["reports"] = new JsonObject { ["native-producer-roots-c2.json"] = ReportBinding(inner),
                ["native-producer-roots.json"] = ReportBinding(combined) };
            foreach (var row in new[] { ("source_capture_sha256", source), ("input_binding_sha256", rollback.Binding),
                ("coverage_sha256", rollback.Coverage), ("native_receipt_sha256", rollback.Native) })
                Binding["external_predecessor"]![row.Item1] = Digest(row.Item2);
            Binding["context"]!["external_predecessor"] = Binding["external_predecessor"]!.DeepClone();
            return new Packet([source, Serialize(Binding), combined, inner, native, rollback.Binding, rollback.Coverage, rollback.Native]);
        }

        private static JsonObject ReportBinding(byte[] bytes) => new() { ["sha256"] = Digest(bytes), ["bytes"] = bytes.Length };
        private static JsonObject Witness(bool mutation)
        {
            var type = Supervision + (mutation ? ".LinuxRetainedReportSet" : ".SupervisionRunOutputBudget");
            return new JsonObject { ["document"] = mutation ? MutationDocument : QuotaDocument, ["type"] = type,
                ["method"] = mutation ? "System.Byte[] " + type + "::MutateRetainedReportForC2(System.Threading.CancellationToken)"
                    : "System.Boolean " + type + "::Charge(System.Int32)", ["start"] = mutation ? 148 : 173,
                ["end"] = mutation ? 199 : 173, ["source_sha256"] = mutation ? MutationHash : QuotaHash };
        }
        private static void AddWitness(JsonObject report, bool mutation)
        {
            var witness = Witness(mutation);
            report[Supervision + ".dll"]!["/source/" + witness["document"]!.GetValue<string>()] = new JsonObject
            {
                [witness["type"]!.GetValue<string>()] = new JsonObject
                    { [witness["method"]!.GetValue<string>()] = Method(mutation ? 148 : 173) }
            };
        }
        internal static JsonObject WitnessMethod(JsonObject report, bool mutation)
        {
            var witness = Witness(mutation);
            return report[Supervision + ".dll"]!["/source/" + witness["document"]!.GetValue<string>()]!
                [witness["type"]!.GetValue<string>()]![witness["method"]!.GetValue<string>()]!.AsObject();
        }
        internal static JsonObject WitnessLines(JsonObject report, bool mutation) => WitnessMethod(report, mutation)["Lines"]!.AsObject();
        internal static JsonObject Method(int line) => new()
        {
            ["Lines"] = new JsonObject { [line.ToString(System.Globalization.CultureInfo.InvariantCulture)] = 1 },
            ["Branches"] = new JsonArray { new JsonObject { ["Line"] = line, ["Offset"] = 0, ["EndOffset"] = 1,
                ["Path"] = 0, ["Ordinal"] = 0, ["Hits"] = 1 } }
        };
        private static byte[] Serialize(JsonNode value) => JsonSerializer.SerializeToUtf8Bytes(value);
        private static string Digest(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    }
}
