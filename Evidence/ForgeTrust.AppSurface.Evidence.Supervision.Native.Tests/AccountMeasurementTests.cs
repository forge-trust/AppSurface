using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Detached metadata comparisons only; these rows create no accounts, owner or physical-success evidence.</summary>
public sealed class AccountMeasurementTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>The explicit compiler-witness mapping must match its real source checkout before native work runs.</summary>
    [Fact]
    public void ReviewedWitnessSourcePinsMatchTheCurrentCheckout()
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var witness = File.ReadAllBytes(TestPathUtils.PathUnder(root, AccountMeasurement.WitnessSource));
        var accounts = File.ReadAllBytes(TestPathUtils.PathUnder(root,
            "Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxRunAccounts.cs"));

        Assert.Equal(AccountMeasurement.WitnessSourceSha256, Convert.ToHexStringLower(SHA256.HashData(witness)));
        Assert.Equal(AccountMeasurement.PartialSourceSha256, Convert.ToHexStringLower(SHA256.HashData(accounts)));
    }

    /// <summary>Rebinding outer packet digests cannot restore a historical compiler-witness partial.</summary>
    [Theory]
    [InlineData("C4Accounts")]
    [InlineData("C4AccountRollback")]
    public void HistoricalPartialFingerprintCannotReplaceTheCurrentWitness(string variant)
    {
        var fixture = new Fixture(variant);
        fixture.ConfigureMerged();
        fixture.ValidateMerged();
        const string historical = "25270ce8685d934fc35c65de82118354acabe4d65861355e9b9727d5512fc2b6";
        fixture.Source["files"]![Fixture.AccountPath]!["sha256"] = historical;
        fixture.Binding["current_witness"]!["partial_source_sha256"] = historical;
        Assert.ThrowsAny<Exception>(() => fixture.ValidateMerged());
    }

    /// <summary>Both variants accept their valid count boundaries, including permitted cross-domain numeric equality.</summary>
    [Theory]
    [InlineData("C4Accounts", 6)]
    [InlineData("C4Accounts", 8)]
    [InlineData("C4AccountRollback", 7)]
    [InlineData("C4AccountRollback", 9)]
    public void ConsistentDetachedPacketsAreOnlyData(string variant, int utilities)
    {
        var fixture = new Fixture(variant);
        fixture.Receipt["utilities"] = utilities;
        fixture.Receipt["settled_utilities"] = utilities;
        if (variant == "C4AccountRollback")
            fixture.Receipt["operation_failure"]!["exec_main_status"] = utilities == 9 ? 255 : 1;
        fixture.Validate();
        Assert.False(fixture.Native["native_acceptance"]!.GetValue<bool>());
        Assert.False(fixture.Receipt["consumer_started"]!.GetValue<bool>());
    }

    /// <summary>Each malformed observation is tested after a valid neighbor; outer hashes are rebound for semantic mutations.</summary>
    [Theory]
    [InlineData("source-head")]
    [InlineData("variant")]
    [InlineData("source-bytes")]
    [InlineData("native-bytes")]
    [InlineData("coverage-bytes")]
    [InlineData("original-map")]
    [InlineData("instrumentation")]
    [InlineData("native-source")]
    [InlineData("generation")]
    [InlineData("wrapper")]
    [InlineData("final-groups")]
    [InlineData("cleanup")]
    [InlineData("cleanup-identity-guards")]
    [InlineData("rollback-cleanup-identity-guards")]
    [InlineData("close-sharing")]
    [InlineData("acceptance")]
    [InlineData("uid-zero")]
    [InlineData("gid-sentinel")]
    [InlineData("duplicate-user")]
    [InlineData("duplicate-group")]
    [InlineData("utilities-unsettled")]
    [InlineData("rollback-error-kind")]
    [InlineData("rollback-stage")]
    [InlineData("rollback-status")]
    [InlineData("rollback-nullable")]
    [InlineData("account-hits")]
    [InlineData("owner-source")]
    [InlineData("foreign-module")]
    public void OneInvalidObservationCannotReplaceTheBoundAccountControl(string mutation)
    {
        var fixture = new Fixture(mutation.StartsWith("rollback-", StringComparison.Ordinal)
            ? "C4AccountRollback" : "C4Accounts");
        fixture.Validate();
        var receipt = fixture.Receipt;
        var ids = receipt["ids"]!;
        var failure = receipt["operation_failure"];
        var rebind = true;
        switch (mutation)
        {
            case "source-head": fixture.Source["source"] = new string('b', 40); break;
            case "variant": fixture.Binding["variant"] = "C4AccountRollback"; break;
            case "source-bytes": fixture.Binding["source_capture_sha256"] = new string('d', 64); rebind = false; break;
            case "native-bytes": fixture.Binding["native_receipt_sha256"] = new string('d', 64); rebind = false; break;
            case "coverage-bytes": fixture.Binding["coverage_sha256"] = new string('d', 64); rebind = false; break;
            case "original-map": fixture.Binding["original"]!.AsObject().Remove("ForgeTrust.AppSurface.Cli.pdb"); break;
            case "instrumentation": fixture.Binding["instrumented"]!["ForgeTrust.AppSurface.Cli.dll"] = new string('a', 64); break;
            case "native-source": fixture.Native["source"] = new string('b', 40); break;
            case "generation": receipt["generation"] = new string('b', 32); break;
            case "wrapper": fixture.Native["wrapper_exit"] = 1; break;
            case "final-groups": fixture.Native["final_groups_empty"] = false; break;
            case "cleanup": receipt["close_completed"] = false; break;
            case "cleanup-identity-guards":
            case "rollback-cleanup-identity-guards":
                receipt["cleanup_identity_guards_checked"] = mutation == "rollback-cleanup-identity-guards"; break;
            case "close-sharing": receipt["shared_close_task"] = false; break;
            case "acceptance": fixture.Native["native_acceptance"] = true; break;
            case "uid-zero": ids["worker_uid"] = 0; break;
            case "gid-sentinel": ids["results_gid"] = uint.MaxValue; break;
            case "duplicate-user": ids["subject_uid"] = 1001; break;
            case "duplicate-group": ids["results_gid"] = 1001; break;
            case "utilities-unsettled": receipt["settled_utilities"] = 0; break;
            case "rollback-error-kind": failure!["error_kind"] = "Io"; break;
            case "rollback-stage": failure!["utility_stage"] = "FinalOwnerCheck"; break;
            case "rollback-status": failure!["exec_main_status"] = 0; break;
            case "rollback-nullable": receipt["shared_close_task"] = false; break;
            case "account-hits": fixture.AccountLines["10"] = 0; break;
            case "owner-source": fixture.Supervision.Remove(Fixture.OwnerPath); fixture.Supervision["foreign/LinuxOwnerActivation.cs"] = Fixture.Document(); break;
            case "foreign-module": fixture.Report.Remove("ForgeTrust.AppSurface.Evidence.Aspire.dll"); fixture.Report["foreign.dll"] = new JsonObject(); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.Validate(rebind));
    }

    /// <summary>Both account variants reject projected seven-module maps with rebound serialization hashes.</summary>
    [Theory]
    [InlineData("C4Accounts", "original-dll")]
    [InlineData("C4Accounts", "original-pdb")]
    [InlineData("C4Accounts", "instrumented-dll")]
    [InlineData("C4Accounts", "instrumented-pdb")]
    [InlineData("C4Accounts", "unchanged-dll")]
    [InlineData("C4Accounts", "bad-hash")]
    [InlineData("C4Accounts", "missing-report")]
    [InlineData("C4Accounts", "duplicate-report")]
    [InlineData("C4Accounts", "missing-positive-document")]
    [InlineData("C4AccountRollback", "original-dll")]
    [InlineData("C4AccountRollback", "original-pdb")]
    [InlineData("C4AccountRollback", "instrumented-dll")]
    [InlineData("C4AccountRollback", "instrumented-pdb")]
    [InlineData("C4AccountRollback", "unchanged-dll")]
    [InlineData("C4AccountRollback", "bad-hash")]
    [InlineData("C4AccountRollback", "missing-report")]
    [InlineData("C4AccountRollback", "duplicate-report")]
    [InlineData("C4AccountRollback", "missing-positive-document")]
    public void SeventhModuleAndExistingPositiveDocumentsRemainRequired(string variant, string mutation)
    {
        const string module = "ForgeTrust.AppSurface.Evidence.Cli.dll";
        var fixture = new Fixture(variant);
        fixture.Validate();
        switch (mutation)
        {
            case "original-dll": fixture.Binding["original"]!.AsObject().Remove(module); break;
            case "original-pdb": fixture.Binding["original"]!.AsObject().Remove("ForgeTrust.AppSurface.Evidence.Cli.pdb"); break;
            case "instrumented-dll": fixture.Binding["instrumented"]!.AsObject().Remove(module); break;
            case "instrumented-pdb": fixture.Binding["instrumented"]!.AsObject().Remove("ForgeTrust.AppSurface.Evidence.Cli.pdb"); break;
            case "unchanged-dll": fixture.Binding["instrumented"]![module] = new string('a', 64); break;
            case "bad-hash": fixture.Binding["original"]![module] = new string('B', 64); break;
            case "missing-report": fixture.Report.Remove(module); break;
            case "duplicate-report":
                fixture.Report.Remove(module);
                fixture.Report["/duplicate/ForgeTrust.AppSurface.Cli.dll"] = new JsonObject();
                break;
            case "missing-positive-document": fixture.Supervision.Remove(Fixture.OwnerPath); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.Validate());
    }

    /// <summary>Reusable detached packet builder for data-only lineage controls; no native holders are constructed.</summary>
    internal sealed class Fixture
    {
        internal const string AccountPath = "Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxRunAccounts.cs";
        internal const string OwnerPath = "Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxOwnerActivation.cs";
        private readonly string _variant;
        internal JsonObject Source { get; } = new();
        internal JsonObject Binding { get; } = new();
        internal JsonObject Native { get; } = new();
        internal JsonObject Receipt { get; } = new();
        internal JsonObject Report { get; } = new();
        internal JsonObject Supervision => Report["ForgeTrust.AppSurface.Evidence.Supervision.dll"]!.AsObject();
        internal JsonObject AccountLines => Supervision[AccountPath]!["DataOnlyType"]!["DataOnlyMethod"]!["Lines"]!.AsObject();

        internal Fixture(string variant)
        {
            _variant = variant;
            var rollback = variant == "C4AccountRollback";
            var original = new JsonObject();
            var instrumented = new JsonObject();
            foreach (var module in new[] { "ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Evidence.Contracts",
                "ForgeTrust.AppSurface.Evidence.Planner", "ForgeTrust.AppSurface.Evidence.Coverage",
                "ForgeTrust.AppSurface.Evidence.Aspire", "ForgeTrust.AppSurface.Evidence.Supervision", "ForgeTrust.AppSurface.Evidence.Cli" })
            {
                foreach (var suffix in new[] { ".dll", ".pdb" })
                {
                    original[module + suffix] = new string('a', 64);
                    instrumented[module + suffix] = new string(suffix == ".dll" ? 'b' : 'a', 64);
                }
                Report[module + ".dll"] = new JsonObject();
            }
            Supervision[AccountPath] = Document();
            Supervision[OwnerPath] = Document();
            var sourceFiles = new JsonObject();
            foreach (var path in new[] { AccountPath, OwnerPath,
                ".github/issue779-checkpoint4/measurement/LinuxAccountMeasurement.cs", ".github/issue779-checkpoint4/run-accounts.py" })
                sourceFiles[path] = new JsonObject { ["sha256"] = new string('c', 64),
                    ["git_blob"] = new string('c', 40), ["mode"] = "0644" };
            // This deliberately small synthetic capture tests shape only; an integration caller must bind the full real capture.
            Source["schema"] = "issue779-checkpoint2-source-v1";
            Source["source"] = Head; Source["tree"] = new string('c', 40);
            Source["count"] = sourceFiles.Count; Source["files"] = sourceFiles;
            Binding["schema"] = "issue779-c4-account-measurement-v1";
            Binding["source"] = Head; Binding["variant"] = variant;
            Binding["collector"] = "coverlet.console/10.0.1";
            Binding["original"] = original; Binding["instrumented"] = instrumented;
            Binding["native_acceptance"] = false;
            // Future optional lineage belongs to the caller; unknown binding metadata must not alter this comparison.
            Binding["future_lineage_metadata"] = "data-only";
            Native["schema"] = "issue779-c4-account-driver-v1";
            Native["source"] = Head; Native["variant"] = variant; Native["generation"] = new string('a', 32);
            Native["passed"] = true; Native["native_acceptance"] = false; Native["wrapper_exit"] = 0;
            Native["groups_empty"] = true; Native["final_groups_empty"] = true;
            foreach (var key in new[] { "request_sha256", "policy_sha256", "owner_stdout_sha256" })
                Native[key] = new string('c', 64);
            Native["receipt"] = Receipt;
            Receipt["schema"] = "issue779-c4-accounts-v1";
            Receipt["variant"] = variant; Receipt["generation"] = new string('a', 32);
            Receipt["managed_entry_sha256"] = new string('b', 64);
            Receipt["native_acceptance"] = false; Receipt["consumer_started"] = false;
            Receipt["cleanup_identity_guards_checked"] = rollback ? null : JsonValue.Create(true);
            Receipt["shared_close_task"] = rollback ? null : JsonValue.Create(true);
            Receipt["closed_holder_rejected"] = rollback ? null : JsonValue.Create(true);
            foreach (var key in new[] { "names_absent", "numeric_ids_absent", "close_completed", "original_handles_closed" })
                Receipt[key] = true;
            Receipt["utilities"] = rollback ? 7 : 6; Receipt["settled_utilities"] = rollback ? 7 : 6;
            Receipt["failed_utilities"] = rollback ? 1 : 0; Receipt["quarantined"] = false;
            Receipt["reservation_failed"] = rollback;
            Receipt["creation_succeeded"] = !rollback; Receipt["identities_published"] = !rollback;
            // UID and GID domains are intentionally allowed to overlap; each domain is internally distinct.
            Receipt["ids"] = new JsonObject { ["worker_uid"] = 1001, ["subject_uid"] = 1002,
                ["worker_gid"] = 1001, ["subject_gid"] = 1002, ["results_gid"] = 1003 };
            Receipt["operation_failure"] = rollback ? new JsonObject
            {
                ["account_failure"] = "OperationFailed", ["preparation_stage"] = "UtilityExecute",
                ["utility_stage"] = "TerminalCheck", ["operation"] = "CreateResultsGroup", ["error_kind"] = "Admission",
                ["diagnostic_code"] = "ASEVD410", ["exec_main_code"] = 1, ["exec_main_status"] = 1, ["dbus_category"] = null
            } : null;
        }

        internal static JsonObject Document() => new()
        {
            ["DataOnlyType"] = new JsonObject { ["DataOnlyMethod"] = new JsonObject
                { ["Lines"] = new JsonObject { ["10"] = 1 } } }
        };

        internal void Validate(bool rebind = true)
        {
            var source = JsonSerializer.SerializeToUtf8Bytes(Source);
            var native = JsonSerializer.SerializeToUtf8Bytes(Native);
            var coverage = JsonSerializer.SerializeToUtf8Bytes(Report);
            if (rebind)
                foreach (var item in new[] { ("source_capture", source), ("native_receipt", native), ("coverage", coverage) })
                    Binding[item.Item1 + "_sha256"] = Convert.ToHexStringLower(SHA256.HashData(item.Item2));
            AccountMeasurement.Validate(Head, _variant, source, JsonSerializer.SerializeToUtf8Bytes(Binding), native, coverage);
        }

        internal void ConfigureMerged()
        {
            Binding["schema"] = "issue779-c4-account-measurement-v2";
            Source["files"]![AccountMeasurement.WitnessSource]!["sha256"] = AccountMeasurement.WitnessSourceSha256;
            Source["files"]![AccountPath]!["sha256"] = AccountMeasurement.PartialSourceSha256;
            var creation = _variant == "C4Accounts";
            var declaring = creation
                ? "ForgeTrust.AppSurface.Evidence.Supervision.LinuxAccountMeasurement/<RunAsync>d__2"
                : "ForgeTrust.AppSurface.Evidence.Supervision.LinuxRunAccounts/<PrepareAccountMeasurementCollisionAsync>d__56";
            var method = "System.Void " + declaring + "::MoveNext()";
            Binding["current_witness"] = new JsonObject { ["document"] = AccountMeasurement.WitnessSource,
                ["declaring_type"] = declaring, ["method"] = method,
                ["source_sha256"] = AccountMeasurement.WitnessSourceSha256,
                ["partial_source_sha256"] = AccountMeasurement.PartialSourceSha256 };
            Supervision[AccountMeasurement.WitnessSource] = new JsonObject
            {
                [declaring] = new JsonObject { [method] = new JsonObject
                    { ["Lines"] = new JsonObject { [creation ? "60" : "290"] = 1 } } }
            };
        }

        internal (byte[] Source, byte[] Binding, byte[] Native, byte[] Coverage) Packets()
        {
            var source = JsonSerializer.SerializeToUtf8Bytes(Source);
            var native = JsonSerializer.SerializeToUtf8Bytes(Native);
            var coverage = JsonSerializer.SerializeToUtf8Bytes(Report);
            foreach (var item in new[] { ("source_capture", source), ("native_receipt", native), ("coverage", coverage) })
                Binding[item.Item1 + "_sha256"] = Convert.ToHexStringLower(SHA256.HashData(item.Item2));
            return (source, JsonSerializer.SerializeToUtf8Bytes(Binding), native, coverage);
        }

        internal void ValidateMerged()
        {
            var packet = Packets();
            AccountMeasurement.ValidateMerged(Head, _variant, packet.Source, packet.Binding, packet.Native, packet.Coverage);
        }
    }

    /// <summary>Existing prior shipping-source hits cannot stand in for this variant's source-bound witness.</summary>
    [Theory]
    [InlineData("C4Accounts", "valid")]
    [InlineData("C4AccountRollback", "valid")]
    [InlineData("C4Accounts", "zero")]
    [InlineData("C4AccountRollback", "zero")]
    [InlineData("C4Accounts", "source")]
    [InlineData("C4AccountRollback", "partial")]
    [InlineData("C4Accounts", "method")]
    [InlineData("C4AccountRollback", "span")]
    [InlineData("C4Accounts", "schema")]
    public void MergedPacketsRequireTheirOwnExactWitness(string variant, string mutation)
    {
        var fixture = new Fixture(variant);
        fixture.ConfigureMerged();
        fixture.ValidateMerged();
        if (mutation == "valid") return;
        var witness = fixture.Binding["current_witness"]!;
        var declaring = witness["declaring_type"]!.GetValue<string>();
        var method = witness["method"]!.GetValue<string>();
        var lines = fixture.Supervision[AccountMeasurement.WitnessSource]![declaring]![method]!["Lines"]!.AsObject();
        switch (mutation)
        {
            case "zero": foreach (var key in lines.Select(static row => row.Key).ToArray()) lines[key] = 0; break;
            case "source": witness["source_sha256"] = new string('d', 64); break;
            case "partial": witness["partial_source_sha256"] = new string('d', 64); break;
            case "method": witness["method"] = "System.Void Other::MoveNext()"; break;
            case "span": lines.Clear(); lines["1"] = 1; break;
            case "schema": fixture.Binding["schema"] = "issue779-c4-account-measurement-v1"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.ThrowsAny<Exception>(() => fixture.ValidateMerged());
    }
}
