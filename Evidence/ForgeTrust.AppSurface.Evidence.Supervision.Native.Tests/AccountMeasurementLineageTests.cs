using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Hash-chain and witness metadata only; no row starts an owner, manufactures admission or proves cleanup.</summary>
public sealed class AccountMeasurementLineageTests
{
    /// <summary>A consistent detached chain is accepted as data while its physical-result claims remain unproven here.</summary>
    [Fact]
    public void ConsistentDetachedChainBindsTheSameOriginalAncestor()
    {
        var fixture = new Fixture();
        fixture.Validate();
    }

    /// <summary>Rebound descendants cannot hide a forked ancestor, stale source or missing current witness.</summary>
    [Theory]
    [InlineData("fork-binding")]
    [InlineData("fork-report")]
    [InlineData("fork-native")]
    [InlineData("stale-head")]
    [InlineData("order")]
    [InlineData("variant")]
    [InlineData("prior-source")]
    [InlineData("immediate-report")]
    [InlineData("immediate-native")]
    [InlineData("artifact-run")]
    [InlineData("artifact-kind")]
    [InlineData("root-trx")]
    [InlineData("nullable-root")]
    [InlineData("witness-zero")]
    [InlineData("raw-c3")]
    [InlineData("c3-failure")]
    [InlineData("c3-trx")]
    public void OneInvalidLinkCannotEnrollTheMergedPacket(string mutation)
    {
        var fixture = new Fixture();
        fixture.Validate();
        switch (mutation)
        {
            case "fork-binding": fixture.Creation.Binding["prior_input_binding_sha256"] = new string('d', 64); fixture.RebindDescendant(); break;
            case "fork-report": fixture.Creation.Binding["prior_coverage_sha256"] = new string('d', 64); fixture.RebindDescendant(); break;
            case "fork-native": fixture.Creation.Binding["prior_native_receipt_sha256"] = new string('d', 64); fixture.RebindDescendant(); break;
            case "stale-head": fixture.C3Binding["source"] = new string('b', 40); fixture.RebindChain(); break;
            case "order": fixture.Rollback.Binding["prior_kind"] = "c3"; break;
            case "variant": fixture.Creation.Binding["variant"] = "C4AccountRollback"; fixture.RebindDescendant(); break;
            case "prior-source": fixture.Creation.Binding["prior_source_capture_sha256"] = new string('d', 64); fixture.RebindDescendant(); break;
            case "immediate-report": fixture.Rollback.Binding["prior_coverage_sha256"] = new string('d', 64); break;
            case "immediate-native": fixture.Rollback.Binding["prior_native_receipt_sha256"] = new string('d', 64); break;
            case "artifact-run": fixture.Rollback.Binding["prior_artifact_name"] = "issue779-c4-accounts-C4Accounts-100-1"; break;
            case "artifact-kind": fixture.Rollback.Binding["prior_artifact_name"] = "issue779-c4-accounts-C4AccountRollback-99-1"; break;
            case "root-trx": fixture.Creation.Binding["prior_root_trx_sha256"] = new string('d', 64); fixture.RebindDescendant(); break;
            case "nullable-root": fixture.Rollback.Binding["prior_root_trx_sha256"] = new string('d', 64); break;
            case "witness-zero":
                var witness = fixture.Rollback.Binding["current_witness"]!;
                fixture.Rollback.Supervision[AccountMeasurement.WitnessSource]![witness["declaring_type"]!.GetValue<string>()]!
                    [witness["method"]!.GetValue<string>()]!["Lines"]!["290"] = 0;
                break;
            case "raw-c3": fixture.C3Binding["schema"] = "issue779-c4-account-measurement-v2"; fixture.RebindChain(); break;
            case "c3-failure": fixture.C3Native["exit"] = 1; fixture.RebindChain(); break;
            case "c3-trx": fixture.C3Binding["root_file_tests"]!["executed"] = 38; fixture.RebindChain(); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.Validate());
    }

    /// <summary>Rebinding all descendant hashes cannot enroll a six-module ancestor or immediate predecessor.</summary>
    [Theory]
    [InlineData("C3", "original-dll")]
    [InlineData("C3", "instrumented-pdb")]
    [InlineData("C3", "missing-report")]
    [InlineData("C3", "duplicate-report")]
    [InlineData("C4Accounts", "original-dll")]
    [InlineData("C4Accounts", "instrumented-pdb")]
    [InlineData("C4Accounts", "missing-report")]
    [InlineData("C4Accounts", "duplicate-report")]
    public void SeventhModuleMustExistAtEveryAccountAncestor(string ancestor, string mutation)
    {
        const string module = "ForgeTrust.AppSurface.Evidence.Cli.dll";
        var fixture = new Fixture();
        fixture.Validate();
        var binding = ancestor == "C3" ? fixture.C3Binding : fixture.Creation.Binding;
        var report = ancestor == "C3" ? fixture.C3Report : fixture.Creation.Report;
        switch (mutation)
        {
            case "original-dll": binding["original"]!.AsObject().Remove(module); break;
            case "instrumented-pdb": binding["instrumented"]!.AsObject().Remove("ForgeTrust.AppSurface.Evidence.Cli.pdb"); break;
            case "missing-report": report.Remove(module); break;
            case "duplicate-report":
                report.Remove(module);
                report["/duplicate/ForgeTrust.AppSurface.Cli.dll"] = new JsonObject();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        fixture.RebindChain();
        Assert.ThrowsAny<Exception>(() => fixture.Validate());
    }

    /// <summary>The current fixed root inventory cannot accept a self-consistent obsolete or different count.</summary>
    [Theory]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(42)]
    public void ObsoleteOrDifferentRootInventoryCannotEnrollTheAccountChain(int count)
    {
        var fixture = new Fixture(count);
        Assert.ThrowsAny<Exception>(() => fixture.Validate());
    }

    private sealed class Fixture
    {
        internal AccountMeasurementTests.Fixture Creation { get; } = new("C4Accounts");
        internal AccountMeasurementTests.Fixture Rollback { get; } = new("C4AccountRollback");
        internal JsonObject C3Binding { get; } = new();
        internal JsonObject C3Native { get; } = new();
        internal JsonObject C3Report => _c3Report;
        private readonly JsonObject _c3Report;
        private readonly byte[] _trx;

        internal Fixture(int rootCount = 41)
        {
            Creation.ConfigureMerged(); Rollback.ConfigureMerged();
            _c3Report = Creation.Report.DeepClone().AsObject();
            _c3Report["ForgeTrust.AppSurface.Evidence.Supervision.dll"]!.AsObject().Remove(AccountMeasurement.WitnessSource);
            C3Binding["schema"] = "issue779-native-instrumentation-v1";
            C3Binding["source"] = new string('a', 40);
            C3Binding["collector"] = "coverlet.console/10.0.1";
            C3Binding["original"] = Creation.Binding["original"]!.DeepClone();
            C3Binding["instrumented"] = Creation.Binding["instrumented"]!.DeepClone();
            C3Native["schema"] = "issue779-csharp-checkpoint3-native-v1";
            C3Native["source"] = new string('a', 40);
            C3Native["base"] = "416b30919e6c4d03b634d9d3fe558884b02f0b91";
            C3Native["exit"] = 0; C3Native["trusted_eligibility"] = false;
            C3Native["cases"] = Cases(["normal", "sdk-normal", "readiness", "factory-stall", "cancel", "stuck-descendant", "disposal-failure"]);
            C3Native["workspace_guards"] = Cases(["bundle-extra-directory", "bundle-input-bytes", "bundle-input-mode", "scratch-fifo", "scratch-hardlink", "scratch-oversize"]);
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var classes = new[] { "LinuxProtectedNodeNativeTests", "LinuxRetainedReportNativeTests", "LinuxKernelIdentityNativeTests", "LinuxDcpIdentityNativeTests" };
            var document = new XDocument(new XElement(ns + "TestRun",
                new XElement(ns + "ResultSummary", new XElement(ns + "Counters",
                    new XAttribute("total", rootCount), new XAttribute("executed", rootCount), new XAttribute("passed", rootCount),
                    new XAttribute("failed", 0), new XAttribute("error", 0), new XAttribute("notExecuted", 0), new XAttribute("warning", 0))),
                new XElement(ns + "Results", Enumerable.Range(0, rootCount).Select(index => new XElement(ns + "UnitTestResult",
                    new XAttribute("outcome", "Passed"), new XAttribute("testName", "ForgeTrust.AppSurface.Evidence.Supervision.Tests."
                        + classes[index % classes.Length] + ".DetachedMetadata" + index))))));
            _trx = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
            C3Binding["root_file_tests"] = new JsonObject { ["trx_sha256"] = Digest(_trx),
                ["executed"] = rootCount, ["passed"] = rootCount, ["native_acceptance"] = false };
            Creation.Binding["prior_kind"] = "c3"; Creation.Binding["prior_variant"] = "C3";
            Creation.Binding["prior_artifact_name"] = "issue779-checkpoint3-application-99-1";
            Rollback.Binding["prior_kind"] = "accounts"; Rollback.Binding["prior_variant"] = "C4Accounts";
            Rollback.Binding["prior_artifact_name"] = "issue779-c4-accounts-C4Accounts-99-1";
            RebindChain();
        }

        private static JsonArray Cases(string[] names) => new(names.Select(static name => (JsonNode)new JsonObject
        {
            ["case"] = name, ["passed"] = true, ["groups_empty"] = true,
            ["final_groups_empty"] = true, ["native_acceptance"] = false
        }).ToArray());

        internal void RebindChain()
        {
            var packet = Creation.Packets();
            C3Binding["source_capture_sha256"] = Digest(packet.Source);
            C3Binding["coverage_sha256"] = Digest(Bytes(_c3Report));
            C3Binding["native_receipt_sha256"] = Digest(Bytes(C3Native));
            Link(Creation.Binding, packet.Source, Bytes(C3Binding), Bytes(_c3Report), Bytes(C3Native));
            Creation.Binding["prior_root_trx_sha256"] = Digest(_trx);
            RebindDescendant();
        }

        internal void RebindDescendant()
        {
            var creation = Creation.Packets();
            Link(Rollback.Binding, creation.Source, creation.Binding, creation.Coverage, creation.Native);
            Rollback.Binding["prior_root_trx_sha256"] = null;
        }

        internal void Validate()
        {
            var creation = Creation.Packets();
            var rollback = Rollback.Packets();
            Assert.Equal(creation.Source, rollback.Source);
            AccountMeasurementLineage.Validate(creation.Source, Bytes(C3Binding), Bytes(_c3Report), Bytes(C3Native), _trx,
                creation.Binding, creation.Coverage, creation.Native, rollback.Binding, rollback.Coverage, rollback.Native);
        }

        private static void Link(JsonObject binding, byte[] source, byte[] priorBinding, byte[] coverage, byte[] native)
        {
            foreach (var item in new[] { ("prior_source_capture", source), ("prior_input_binding", priorBinding),
                ("prior_coverage", coverage), ("prior_native_receipt", native) })
                binding[item.Item1 + "_sha256"] = Digest(item.Item2);
        }

        private static byte[] Bytes(JsonObject value) => JsonSerializer.SerializeToUtf8Bytes(value);
        private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
