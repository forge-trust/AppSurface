using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Receipt consistency data only; no fixture starts a worker, samples a PID or creates admission.</summary>
public sealed class SdkFailureMeasurementTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Source = "unused\nresult = CreateResult(producer, EvidenceProducerOutcome.Failed,\nThe restricted coverage process exited with code {process.ExitCode";

    [Fact]
    public void ConsistentDetachedReceiptAndReportAreDataOnly()
    {
        var fixture = new Fixture();
        fixture.Validate();
        Assert.False(fixture.Root["native_acceptance"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("worker-file-absent", "ForgeTrust.AppSurface.Cli.dll")]
    [InlineData("prior-root-file-unchanged", "ForgeTrust.AppSurface.Cli.dll")]
    [InlineData("worker-file-absent", "ForgeTrust.AppSurface.Evidence.Contracts.dll")]
    [InlineData("prior-root-file-unchanged", "ForgeTrust.AppSurface.Evidence.Contracts.dll")]
    [InlineData("worker-file-absent", "ForgeTrust.AppSurface.Evidence.Coverage.dll")]
    [InlineData("prior-root-file-unchanged", "ForgeTrust.AppSurface.Evidence.Coverage.dll")]
    [InlineData("worker-file-absent", "ForgeTrust.AppSurface.Evidence.Aspire.dll")]
    [InlineData("prior-root-file-unchanged", "ForgeTrust.AppSurface.Evidence.Aspire.dll")]
    public void OptionalUnusedRowsCannotStandInForRequiredWorkerHandoffs(string outcome, string requiredModule)
    {
        var fixture = new Fixture();
        foreach (var index in new[] { 2, 5 })
        {
            var row = fixture.Root["worker_measurements"]![0]!["modules"]![index]!.AsObject();
            row["outcome"] = outcome;
            row.Remove("worker_sha256"); row.Remove("cached_sha256"); row.Remove("host_sha256");
        }
        fixture.Validate();
        var required = fixture.Root["worker_measurements"]![0]!["modules"]!.AsArray()
            .Single(value => value!["module"]!.GetValue<string>() == requiredModule)!.AsObject();
        required["outcome"] = outcome;
        foreach (var key in new[] { "worker_sha256", "cached_sha256", "host_sha256" }) required.Remove(key);
        Assert.ThrowsAny<Exception>(() => fixture.Validate());
    }

    /// <summary>Each original required SDK handoff remains bound to its actual instrumented image.</summary>
    [Theory]
    [InlineData("ForgeTrust.AppSurface.Cli.dll")]
    [InlineData("ForgeTrust.AppSurface.Evidence.Contracts.dll")]
    [InlineData("ForgeTrust.AppSurface.Evidence.Coverage.dll")]
    [InlineData("ForgeTrust.AppSurface.Evidence.Aspire.dll")]
    public void OriginalSdkHandoffsCannotClaimAnotherImage(string module)
    {
        var fixture = new Fixture();
        fixture.Validate();
        var row = fixture.Root["worker_measurements"]![0]!["modules"]!.AsArray()
            .Single(value => value!["module"]!.GetValue<string>() == module)!.AsObject();
        row["module_sha256"] = new string('d', 64);
        Assert.ThrowsAny<Exception>(() => fixture.Validate());
    }

    [Theory]
    [InlineData("passed")]
    [InlineData("finalized")]
    [InlineData("restored_originals")]
    [InlineData("groups_empty")]
    [InlineData("final_groups_empty")]
    [InlineData("native_acceptance")]
    [InlineData("root_exit")]
    [InlineData("verdict")]
    [InlineData("claim")]
    [InlineData("eligibility")]
    [InlineData("producer-status")]
    [InlineData("producer-join")]
    [InlineData("actual-test-count")]
    [InlineData("marker-owner")]
    [InlineData("marker-bytes")]
    [InlineData("account-presence")]
    [InlineData("numeric-account")]
    [InlineData("duplicate-account")]
    [InlineData("foreign-account-name")]
    [InlineData("wrong-numeric-id")]
    [InlineData("shared-uid")]
    [InlineData("wrong-app-id")]
    [InlineData("ready-guard")]
    [InlineData("app-exit")]
    [InlineData("worker-exit")]
    [InlineData("worker-pidfd")]
    [InlineData("worker-cache")]
    [InlineData("optional-cache")]
    [InlineData("optional-image")]
    [InlineData("optional-outcome")]
    [InlineData("support-bytes")]
    [InlineData("worker-phase")]
    [InlineData("worker-ack")]
    [InlineData("worker-live")]
    [InlineData("worker-binding")]
    [InlineData("worker-bytes")]
    [InlineData("original-map")]
    [InlineData("failed-arm-hits")]
    public void OneInvalidObservationCannotReplaceTheSeparateFailedSdkControl(string mutation)
    {
        var fixture = new Fixture();
        fixture.Validate();
        var root = fixture.Root;
        var info = root["sdk_failure"]!;
        var worker = root["worker_measurements"]![0]!;
        switch (mutation)
        {
            case "native_acceptance": root[mutation] = true; break;
            case "root_exit": root[mutation] = 0; break;
            case "verdict": info["execution_verdict"] = "Passed"; break;
            case "claim": info["claim_kind"] = "ObservationOnly"; break;
            case "eligibility": info["eligibility"] = "Informational"; break;
            case "producer-status": info["producer"]!["normal_status"] = 0; break;
            case "producer-join": info["producer"]!["pump_joined"] = false; break;
            case "actual-test-count": info["producer"]!["tests"]!["failed"] = 0; break;
            case "marker-owner": info["test_branch_marker"]!["sealed_root_owned"] = false; break;
            case "marker-bytes": info["test_branch_marker"]!["sha256"] = new string('d', 64); break;
            case "account-presence": root["accounts"]![0]!["present"] = true; break;
            case "numeric-account": root["numeric_accounts"]![0]!["absent"] = false; break;
            case "duplicate-account": root["accounts"]![1] = root["accounts"]![0]!.DeepClone(); break;
            case "foreign-account-name": root["accounts"]![0]!["name"] = "canary"; break;
            case "wrong-numeric-id": root["numeric_accounts"]![0]!["id"] = 999; break;
            case "shared-uid": info["account_binding"]!["subject_uid"] = 1001; break;
            case "wrong-app-id": root["apphost"]!["uid"] = 999; break;
            case "ready-guard": info["original_application_ready_guard"]!["original_application_ready_guard"] = false; break;
            case "app-exit": root["application_observations"]![0]!["main_status"] = 1; break;
            case "worker-exit": worker["normal_exit_code"] = 0; break;
            case "support-bytes": root["exit_support"]!["ForgeTrust.AppSurface.Evidence.ExitBarrier.Support.dll"] = new string('d', 64); break;
            case "worker-cache": worker["modules"]![0]!["cached_sha256"] = new string('d', 64); break;
            case "optional-cache": worker["modules"]![2]!["cached_sha256"] = new string('d', 64); break;
            case "optional-image": worker["modules"]![2]!["module_sha256"] = new string('d', 64); break;
            case "optional-outcome": worker["modules"]![2]!["outcome"] = "private-canary"; break;
            case "worker-phase": worker["capture_phase"] = "terminal"; break;
            case "worker-ack": worker["acknowledged_before_original_exit"] = false; break;
            case "worker-live": worker["cached_original_pid_live"] = false; break;
            case "worker-pidfd": worker["original_pidfd_exit"] = false; break;
            case "worker-binding": worker["pid"] = 999; break;
            case "worker-bytes": worker["modules"]![0]!["host_sha256"] = new string('d', 64); break;
            case "original-map": root["sdk_original"]!["ForgeTrust.AppSurface.Cli.dll"] = new string('d', 64); break;
            case "failed-arm-hits": fixture.Report["ForgeTrust.AppSurface.Evidence.Coverage.dll"]!["EvidenceRestrictedCoverageProducer.cs"]!["Producer"]!["RunAsync"]!["Lines"]!["2"] = 0; break;
            default: root[mutation] = false; break;
        }
        // Rebind serialization digests so the selected semantic check, not a stale outer hash, rejects.
        Assert.ThrowsAny<Exception>(() => fixture.Validate());
    }

    /// <summary>The seventh inventory row and any reported handoff remain bound after outer digests are rebound.</summary>
    [Theory]
    [InlineData("missing-worker")]
    [InlineData("duplicate-worker")]
    [InlineData("worker-image")]
    [InlineData("worker-cache")]
    [InlineData("worker-bytes")]
    [InlineData("host-bytes")]
    [InlineData("original-dll")]
    [InlineData("original-pdb")]
    [InlineData("original-hash")]
    [InlineData("instrumented-dll")]
    [InlineData("instrumented-pdb")]
    [InlineData("unchanged-dll")]
    [InlineData("missing-report")]
    [InlineData("duplicate-report")]
    public void SeventhModuleCannotBeOmittedOrProjectedFromTheFailedSdkPacket(string mutation)
    {
        const string module = "ForgeTrust.AppSurface.Evidence.Cli.dll";
        var fixture = new Fixture();
        fixture.Validate();
        var modules = fixture.Root["worker_measurements"]![0]!["modules"]!.AsArray();
        var row = modules.Single(static value => value!["module"]!.GetValue<string>() == module)!.AsObject();
        switch (mutation)
        {
            case "missing-worker": modules.Remove(row); break;
            case "duplicate-worker": row["module"] = "ForgeTrust.AppSurface.Cli.dll"; break;
            case "worker-image": row["module_sha256"] = new string('d', 64); break;
            case "worker-cache": row["cached_sha256"] = new string('d', 64); break;
            case "worker-bytes": row["worker_sha256"] = new string('d', 64); break;
            case "host-bytes": row["host_sha256"] = new string('d', 64); break;
            case "original-dll": fixture.Root["sdk_original"]!.AsObject().Remove(module); break;
            case "original-pdb": fixture.Root["sdk_original"]!.AsObject().Remove("ForgeTrust.AppSurface.Evidence.Cli.pdb"); break;
            case "original-hash": fixture.Root["sdk_original"]![module] = new string('d', 64); break;
            case "instrumented-dll": fixture.Root["sdk_instrumented"]!.AsObject().Remove(module); break;
            case "instrumented-pdb": fixture.Root["sdk_instrumented"]!.AsObject().Remove("ForgeTrust.AppSurface.Evidence.Cli.pdb"); break;
            case "unchanged-dll": fixture.Root["sdk_instrumented"]![module] = new string('a', 64); break;
            case "missing-report": fixture.Report.Remove(module); break;
            case "duplicate-report":
                fixture.Report.Remove(module);
                fixture.Report["/duplicate/ForgeTrust.AppSurface.Cli.dll"] = new JsonObject();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.Validate());
    }

    /// <summary>The SDK host need not execute protected CLI code; its seven-module inventory remains complete.</summary>
    [Theory]
    [InlineData("worker-file-absent", "missing-document")]
    [InlineData("worker-file-absent", "zero-document")]
    [InlineData("prior-root-file-unchanged", "missing-document")]
    [InlineData("prior-root-file-unchanged", "zero-document")]
    [InlineData("unchanged-worker-handoff", "missing-document")]
    [InlineData("unchanged-worker-handoff", "zero-document")]
    public void SdkEvidenceCliMayBeUnusedWithoutProjectingTheImage(string outcome, string document)
    {
        const string module = "ForgeTrust.AppSurface.Evidence.Cli.dll";
        var fixture = new Fixture();
        fixture.Validate();
        var row = fixture.Root["worker_measurements"]![0]!["modules"]!.AsArray()
            .Single(static value => value!["module"]!.GetValue<string>() == module)!.AsObject();
        row["outcome"] = outcome;
        if (outcome != "unchanged-worker-handoff")
            foreach (var key in new[] { "worker_sha256", "cached_sha256", "host_sha256" }) row.Remove(key);
        if (document == "missing-document") fixture.Report[module]!.AsObject().Clear();
        else fixture.Report[module]!["EvidenceProtectedCliExecution.cs"]!["DetachedProtectedCli"]!["RunAsync"]!["Lines"]!["1"] = 0;
        fixture.Validate();
        Assert.Equal(1, fixture.Root["root_exit"]!.GetValue<int>());
        Assert.False(fixture.Root["native_acceptance"]!.GetValue<bool>());
    }

    private sealed class Fixture
    {
        internal JsonObject Root { get; } = new();
        internal JsonObject Report { get; } = new();
        private readonly JsonObject _binding = new();

        internal Fixture()
        {
            var modules = new[] { "ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Evidence.Contracts",
                "ForgeTrust.AppSurface.Evidence.Planner", "ForgeTrust.AppSurface.Evidence.Coverage", "ForgeTrust.AppSurface.Evidence.Aspire",
                "ForgeTrust.AppSurface.Evidence.Supervision", "ForgeTrust.AppSurface.Evidence.Cli" };
            var original = new JsonObject(); var instrumented = new JsonObject(); var handoffs = new JsonArray();
            foreach (var module in modules)
            {
                foreach (var suffix in new[] { ".dll", ".pdb" })
                {
                    original[module + suffix] = new string('a', 64);
                    instrumented[module + suffix] = new string('b', 64);
                }
                handoffs.Add(new JsonObject { ["module"] = module + ".dll", ["outcome"] = "unchanged-worker-handoff",
                    ["module_sha256"] = new string('b', 64), ["cached_sha256"] = new string('c', 64), ["worker_sha256"] = new string('c', 64), ["host_sha256"] = new string('c', 64) });
                Report[module + ".dll"] = new JsonObject();
            }
            Report["ForgeTrust.AppSurface.Evidence.Coverage.dll"] = new JsonObject
            {
                ["EvidenceRestrictedCoverageProducer.cs"] = new JsonObject { ["Producer"] = new JsonObject
                    { ["RunAsync"] = new JsonObject { ["Lines"] = new JsonObject { ["2"] = 1 } } } }
            };
            Report["ForgeTrust.AppSurface.Evidence.Cli.dll"] = new JsonObject
            {
                ["EvidenceProtectedCliExecution.cs"] = new JsonObject { ["DetachedProtectedCli"] = new JsonObject
                    { ["RunAsync"] = new JsonObject { ["Lines"] = new JsonObject { ["1"] = 1 } } } }
            };
            _binding["sdk_original"] = original.DeepClone();
            Root["sdk_original"] = original; Root["sdk_instrumented"] = instrumented;
            Root["schema"] = "issue779-sdk-failed-producer-v1"; Root["case"] = "sdk-test-failure";
            Root["source"] = Head; Root["generation"] = new string('a', 32); Root["root_exit"] = 1;
            Root["native_acceptance"] = false;
            foreach (var flag in new[] { "passed", "finalized", "restored_originals", "groups_empty", "final_groups_empty" }) Root[flag] = true;
            Root["producer"] = new JsonObject { ["pid"] = 123, ["uid"] = 1003, ["gid"] = 1004 };
            Root["apphost"] = new JsonObject { ["uid"] = 1005, ["gid"] = 1006 };
            var accounts = new JsonArray(); var numeric = new JsonArray();
            foreach (var item in new[] { ("passwd", "evw", 1001), ("passwd", "evs", 1003), ("passwd", "eva", 1005),
                ("group", "evw", 1002), ("group", "evs", 1004), ("group", "eva", 1006), ("group", "evr", 1007), ("group", "evg", 1008) })
            {
                accounts.Add(new JsonObject { ["present"] = false, ["table"] = item.Item1, ["name"] = item.Item2 + new string('a', 28) });
                numeric.Add(new JsonObject { ["absent"] = true, ["table"] = item.Item1, ["id"] = item.Item3 });
            }
            Root["accounts"] = accounts; Root["numeric_accounts"] = numeric;
            Root["application_observations"] = new JsonArray { new JsonObject
            {
                ["phase"] = "settled", ["main_code"] = 1, ["main_status"] = 0, ["monitor_failed"] = false,
                ["group_empty_twice"] = true, ["pumps_joined"] = true, ["pending_start_joined"] = true
            } };
            var support = new JsonObject
            {
                ["ForgeTrust.AppSurface.Evidence.ExitBarrier.Support.dll"] = new string('e', 64),
                ["ForgeTrust.AppSurface.Evidence.ExitBarrier.Support.pdb"] = new string('f', 64)
            };
            Root["exit_support"] = support;
            _binding["exit_support"] = support.DeepClone();
            Root["worker_measurements"] = new JsonArray { new JsonObject
            {
                ["schema"] = "evidence-worker-measurement-v3", ["normal_exit_code"] = 1, ["original_pidfd_exit"] = true,
                ["capture_phase"] = "pre_exit_after_original_handlers", ["cached_original_pid_live"] = true,
                ["acknowledged_before_original_exit"] = true, ["barrier_nonce_sha256"] = new string('c', 64),
                ["native_acceptance"] = false, ["pid"] = 200, ["uid"] = 1001, ["gid"] = 1002,
                ["start_time"] = 1234, ["cgroup"] = "/system.slice/fixture.service", ["modules"] = handoffs
            } };
            Root["sdk_failure"] = new JsonObject
            {
                ["execution_verdict"] = "Incomplete", ["claim_kind"] = "None", ["eligibility"] = "None", ["cleanup_completed"] = true,
                ["producer_result"] = new JsonObject { ["outcome"] = "Failed", ["diagnostic"] = "The restricted coverage process exited with code 1.", ["artifact_count"] = 0, ["assertion_count"] = 0 },
                ["original_application_ready_guard"] = new JsonObject { ["schema"] = "evidence-sdk-producer-failure-v1", ["producer_exit_code"] = 1,
                    ["application_unit"] = "appsurface-evidence-application-" + new string('a', 32) + ".service", ["original_application_ready_guard"] = true, ["native_acceptance"] = false },
                ["worker"] = new JsonObject { ["worker_pid"] = 200, ["worker_uid"] = 1001, ["worker_gid"] = 1002, ["cgroup"] = "/system.slice/fixture.service" },
                ["account_binding"] = new JsonObject { ["worker_uid"] = 1001, ["worker_gid"] = 1002, ["subject_uid"] = 1003, ["subject_gid"] = 1004,
                    ["application_uid"] = 1005, ["application_gid"] = 1006, ["results_gid"] = 1007, ["resource_access_gid"] = 1008 },
                ["test_branch_marker"] = new JsonObject { ["sealed_root_owned"] = true, ["bytes"] = 12,
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData("test-failure"u8)) },
                ["producer"] = new JsonObject { ["normal_code"] = 1, ["normal_status"] = 1, ["captured_pid"] = 123, ["cleanup_failed"] = false,
                    ["native_acceptance"] = false, ["pending_failure"] = "None", ["physically_settled"] = true, ["group_empty_twice"] = true,
                    ["pump_joined"] = true, ["output_drained"] = true, ["tests"] = new JsonObject { ["failed"] = 1, ["passed"] = 5, ["skipped"] = 0, ["total"] = 6 } }
            };
        }

        internal void Validate()
        {
            var report = JsonSerializer.SerializeToUtf8Bytes(Report);
            Root["coverage_bytes"] = report.Length;
            Root["coverage_sha256"] = Convert.ToHexStringLower(SHA256.HashData(report));
            var receipt = JsonSerializer.SerializeToUtf8Bytes(Root);
            _binding["sdk_failure_receipt_sha256"] = Convert.ToHexStringLower(SHA256.HashData(receipt));
            _binding["sdk_failure_coverage_sha256"] = Convert.ToHexStringLower(SHA256.HashData(report));
            SdkFailureMeasurement.Validate(JsonSerializer.SerializeToElement(_binding), Head, receipt, report, Source);
        }
    }
}
