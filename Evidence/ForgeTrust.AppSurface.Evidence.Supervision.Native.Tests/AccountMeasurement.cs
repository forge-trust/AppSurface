using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Compares detached C4 account measurements; it creates no account, owner, admission or proof.</summary>
/// <remarks>
/// This is a data check before a caller considers a standard collector merge. The caller must also require
/// sourceCaptureBytes to equal its independently verified full parent capture. No Git or filesystem refresh
/// occurs here. Restoration is the driver's finalization precondition: the current binding has original and
/// instrumented maps, not an invented restored map. A valid comparison does not establish physical cleanup.
/// Raw API inputs and all nonreport inputs are limited to 1 byte–8 MiB and JSON depth 64; only ValidateMerged's
/// report allows 32 MiB. Optional binding lineage metadata is left to the lineage comparison/caller;
/// the native driver, nested receipt, identity and module-map shapes remain closed to their current contracts.
/// </remarks>
internal static class AccountMeasurement
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    internal const string WitnessSource = ".github/issue779-checkpoint4/measurement/LinuxAccountMeasurement.cs";
    internal const string WitnessSourceSha256 = "6810182cb7a93c8928f7aca9a8e55d575b7d4e2ebc0a85f3d0cb64c4ed4135fa";
    internal const string PartialSourceSha256 = "3629b2c09d59d63d5f31df6d207b53806646110b422ac6bffca8121b0153afb3";
    private const string AccountsSource = "Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxRunAccounts.cs";
    private const string OwnerSource = "Evidence/ForgeTrust.AppSurface.Evidence.Supervision/LinuxOwnerActivation.cs";
    private static readonly string[] Modules = ["ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Evidence.Contracts",
        "ForgeTrust.AppSurface.Evidence.Planner", "ForgeTrust.AppSurface.Evidence.Coverage",
        "ForgeTrust.AppSurface.Evidence.Aspire", "ForgeTrust.AppSurface.Evidence.Supervision", "ForgeTrust.AppSurface.Evidence.Cli"];
    private static readonly string[] ReceiptFields = ["schema", "variant", "creation_succeeded", "generation",
        "managed_entry_sha256", "native_acceptance", "consumer_started", "shared_close_task", "closed_holder_rejected",
        "names_absent", "numeric_ids_absent", "utilities", "settled_utilities", "failed_utilities", "close_completed",
        "quarantined", "reservation_failed", "identities_published", "original_handles_closed", "ids", "operation_failure"];

    /// <summary>Requires the two supported variants' exact current driver/receipt contracts and bound report bytes.</summary>
    /// <param name="expectedHead">Independently obtained full lowercase immutable source commit.</param>
    /// <param name="variant">Exactly C4Accounts or C4AccountRollback, selected independently of the receipt.</param>
    /// <param name="sourceCaptureBytes">Full original capture bytes, also matched byte-for-byte to the parent capture by the caller.</param>
    /// <param name="bindingBytes">Final issue779-c4-account-measurement-v1 binding; no merge lineage is assumed.</param>
    /// <param name="nativeReceiptBytes">Original native-accounts-receipt.json bytes, including the nested C# data receipt.</param>
    /// <param name="coverageBytes">Unmodified standard native-accounts.json report, before any future supported merge.</param>
    internal static void Validate(string expectedHead, string variant, byte[] sourceCaptureBytes, byte[] bindingBytes,
        byte[] nativeReceiptBytes, byte[] coverageBytes) =>
        ValidateCore(expectedHead, variant, sourceCaptureBytes, bindingBytes, nativeReceiptBytes, coverageBytes, merged: false);

    /// <summary>Checks a v2 merged packet and its current-variant-only witness; only coverage may use 32 MiB.</summary>
    /// <remarks>
    /// Arguments have the same meaning as Validate. This does not verify predecessor lineage: call
    /// AccountMeasurementLineage.Validate before consuming a merged report. The source hashes, generated
    /// names and method spans below are the reviewed immutable mapping, not a filesystem lookup. Changes
    /// to either partial require an explicit mapping review. All nonreport inputs retain the 8 MiB bound.
    /// </remarks>
    internal static void ValidateMerged(string expectedHead, string variant, byte[] sourceCaptureBytes, byte[] bindingBytes,
        byte[] nativeReceiptBytes, byte[] coverageBytes) =>
        ValidateCore(expectedHead, variant, sourceCaptureBytes, bindingBytes, nativeReceiptBytes, coverageBytes, merged: true);

    private static void ValidateCore(string expectedHead, string variant, byte[] sourceCaptureBytes, byte[] bindingBytes,
        byte[] nativeReceiptBytes, byte[] coverageBytes, bool merged)
    {
        Assert.Matches("^[0-9a-f]{40}$", expectedHead);
        Assert.Contains(variant, new[] { "C4Accounts", "C4AccountRollback" });
        using var sourceDocument = Parse(sourceCaptureBytes);
        using var bindingDocument = Parse(bindingBytes);
        using var nativeDocument = Parse(nativeReceiptBytes);
        // Standard report paths/type/method names are case-sensitive, unlike the closed state data.
        using var reportDocument = Parse(coverageBytes, StringComparer.Ordinal, merged ? 32 * 1024 * 1024 : MaximumBytes);
        var source = sourceDocument.RootElement;
        var binding = bindingDocument.RootElement;
        var native = nativeDocument.RootElement;
        Assert.Equal("issue779-checkpoint2-source-v1", source.GetProperty("schema").GetString());
        Assert.Equal(expectedHead, source.GetProperty("source").GetString());
        Assert.Matches("^[0-9a-f]{40}$", source.GetProperty("tree").GetString()!);
        var files = source.GetProperty("files");
        var sourceRows = files.EnumerateObject().ToArray();
        Assert.InRange(sourceRows.Length, 1, MaximumBytes);
        Assert.Equal(sourceRows.Length, source.GetProperty("count").GetInt32());
        foreach (var row in sourceRows)
        {
            Assert.False(row.Name.StartsWith("/", StringComparison.Ordinal) || row.Name.Contains('\\')
                || row.Name.Split('/').Any(static part => part is "" or "." or ".."));
            Hash(row.Value.GetProperty("sha256"));
            Assert.Matches("^[0-7]{4}$", row.Value.GetProperty("mode").GetString()!);
            Assert.Matches("^[0-9a-f]{40}$", row.Value.GetProperty("git_blob").GetString()!);
        }
        foreach (var path in new[] { AccountsSource, OwnerSource,
            ".github/issue779-checkpoint4/measurement/LinuxAccountMeasurement.cs", ".github/issue779-checkpoint4/run-accounts.py" })
            Hash(files.GetProperty(path).GetProperty("sha256"));

        Assert.Equal(merged ? "issue779-c4-account-measurement-v2" : "issue779-c4-account-measurement-v1", binding.GetProperty("schema").GetString());
        Assert.Equal(expectedHead, binding.GetProperty("source").GetString());
        Assert.Equal(variant, binding.GetProperty("variant").GetString());
        Assert.Equal("coverlet.console/10.0.1", binding.GetProperty("collector").GetString());
        Assert.False(binding.GetProperty("native_acceptance").GetBoolean());
        foreach (var item in new[] { ("source_capture", sourceCaptureBytes), ("native_receipt", nativeReceiptBytes), ("coverage", coverageBytes) })
            Assert.Equal(Digest(item.Item2), binding.GetProperty(item.Item1 + "_sha256").GetString());
        var names = Modules.SelectMany(static module => new[] { module + ".dll", module + ".pdb" }).ToArray();
        foreach (var key in new[] { "original", "instrumented" })
        {
            var map = binding.GetProperty(key);
            Properties(map, names);
            foreach (var name in names) Hash(map.GetProperty(name));
        }
        foreach (var name in names.Where(static name => name.EndsWith(".dll", StringComparison.Ordinal)))
            Assert.NotEqual(binding.GetProperty("original").GetProperty(name).GetString(),
                binding.GetProperty("instrumented").GetProperty(name).GetString());

        Properties(native, ["schema", "source", "variant", "generation", "passed", "native_acceptance", "wrapper_exit",
            "groups_empty", "final_groups_empty", "receipt", "request_sha256", "policy_sha256", "owner_stdout_sha256"]);
        Assert.Equal("issue779-c4-account-driver-v1", native.GetProperty("schema").GetString());
        Assert.Equal(expectedHead, native.GetProperty("source").GetString());
        Assert.Equal(variant, native.GetProperty("variant").GetString());
        var generation = native.GetProperty("generation").GetString();
        Assert.Matches("^[0-9a-f]{32}$", generation!);
        Assert.Equal(0, native.GetProperty("wrapper_exit").GetInt32());
        foreach (var key in new[] { "passed", "groups_empty", "final_groups_empty" })
            Assert.True(native.GetProperty(key).GetBoolean());
        Assert.False(native.GetProperty("native_acceptance").GetBoolean());
        // These are retained digest claims; stdout/request/policy bytes are not inputs to this API.
        foreach (var key in new[] { "request_sha256", "policy_sha256", "owner_stdout_sha256" }) Hash(native.GetProperty(key));
        var receipt = native.GetProperty("receipt");
        Assert.InRange(Encoding.UTF8.GetByteCount(receipt.GetRawText()), 1, 4096);
        Properties(receipt, ReceiptFields);
        Assert.Equal("issue779-c4-accounts-v1", receipt.GetProperty("schema").GetString());
        Assert.Equal(variant, receipt.GetProperty("variant").GetString());
        Assert.Equal(generation, receipt.GetProperty("generation").GetString());
        Assert.Equal(binding.GetProperty("instrumented").GetProperty("ForgeTrust.AppSurface.Cli.dll").GetString(),
            receipt.GetProperty("managed_entry_sha256").GetString());
        foreach (var key in new[] { "names_absent", "numeric_ids_absent", "close_completed", "original_handles_closed" })
            Assert.True(receipt.GetProperty(key).GetBoolean());
        foreach (var key in new[] { "native_acceptance", "consumer_started", "quarantined" })
            Assert.False(receipt.GetProperty(key).GetBoolean());
        var ids = receipt.GetProperty("ids");
        var users = new[] { "worker_uid", "subject_uid" };
        var groups = new[] { "worker_gid", "subject_gid", "results_gid" };
        Properties(ids, users.Concat(groups));
        foreach (var domain in new[] { users, groups })
        {
            var values = domain.Select(key => ids.GetProperty(key).GetUInt32()).ToArray();
            Assert.All(values, static value => Assert.InRange(value, 1u, uint.MaxValue - 1));
            Assert.Equal(values.Length, values.Distinct().Count());
        }
        var rollback = variant == "C4AccountRollback";
        foreach (var key in new[] { "creation_succeeded", "identities_published" })
            Assert.Equal(!rollback, receipt.GetProperty(key).GetBoolean());
        Assert.Equal(rollback, receipt.GetProperty("reservation_failed").GetBoolean());
        var utilities = receipt.GetProperty("utilities").GetInt32();
        Assert.InRange(utilities, rollback ? 7 : 6, rollback ? 9 : 8);
        Assert.Equal(utilities, receipt.GetProperty("settled_utilities").GetInt32());
        Assert.Equal(rollback ? 1 : 0, receipt.GetProperty("failed_utilities").GetInt32());
        if (!rollback)
        {
            Assert.True(receipt.GetProperty("shared_close_task").GetBoolean());
            Assert.True(receipt.GetProperty("closed_holder_rejected").GetBoolean());
            Assert.Equal(JsonValueKind.Null, receipt.GetProperty("operation_failure").ValueKind);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, receipt.GetProperty("shared_close_task").ValueKind);
            Assert.Equal(JsonValueKind.Null, receipt.GetProperty("closed_holder_rejected").ValueKind);
            var failure = receipt.GetProperty("operation_failure");
            Properties(failure, ["account_failure", "preparation_stage", "utility_stage", "operation", "error_kind",
                "diagnostic_code", "exec_main_code", "exec_main_status", "dbus_category"]);
            foreach (var item in new[] { ("account_failure", "OperationFailed"), ("preparation_stage", "UtilityExecute"),
                ("utility_stage", "TerminalCheck"), ("operation", "CreateResultsGroup"), ("error_kind", "Admission"),
                ("diagnostic_code", "ASEVD410") })
                Assert.Equal(item.Item2, failure.GetProperty(item.Item1).GetString());
            Assert.Equal(1, failure.GetProperty("exec_main_code").GetInt32());
            Assert.InRange(failure.GetProperty("exec_main_status").GetInt32(), 1, 255);
            Assert.Equal(JsonValueKind.Null, failure.GetProperty("dbus_category").ValueKind);
        }

        var report = reportDocument.RootElement;
        Assert.Equal(Modules.Select(static name => name + ".dll").Order(StringComparer.Ordinal),
            report.EnumerateObject().Select(static module => Leaf(module.Name)).Order(StringComparer.Ordinal));
        var supervision = Assert.Single(report.EnumerateObject(), static module => Leaf(module.Name)
            == "ForgeTrust.AppSurface.Evidence.Supervision.dll").Value;
        if (merged) ValidateWitness(binding.GetProperty("current_witness"), files, supervision, variant);
        foreach (var path in new[] { AccountsSource, OwnerSource })
        {
            var documents = supervision.EnumerateObject().Where(row => row.Name.Replace('\\', '/') == path
                || row.Name.Replace('\\', '/').EndsWith("/" + path, StringComparison.Ordinal));
            Assert.True(documents.SelectMany(static row => row.Value.EnumerateObject())
                .SelectMany(static type => type.Value.EnumerateObject())
                .SelectMany(static method => method.Value.GetProperty("Lines").EnumerateObject())
                .Any(static line => int.TryParse(line.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    && number > 0 && line.Value.TryGetInt64(out var hits) && hits > 0),
                "The standard Supervision report must hit the captured account and owner source documents.");
        }
    }

    private static void ValidateWitness(JsonElement witness, JsonElement files, JsonElement supervision, string variant)
    {
        Properties(witness, ["document", "declaring_type", "method", "source_sha256", "partial_source_sha256"]);
        Assert.Equal(WitnessSource, witness.GetProperty("document").GetString());
        Assert.Equal(WitnessSourceSha256, files.GetProperty(WitnessSource).GetProperty("sha256").GetString());
        Assert.Equal(PartialSourceSha256, files.GetProperty(AccountsSource).GetProperty("sha256").GetString());
        Assert.Equal(WitnessSourceSha256, witness.GetProperty("source_sha256").GetString());
        Assert.Equal(PartialSourceSha256, witness.GetProperty("partial_source_sha256").GetString());
        var creation = variant == "C4Accounts";
        var declaring = creation
            ? "ForgeTrust.AppSurface.Evidence.Supervision.LinuxAccountMeasurement/<RunAsync>d__2"
            : "ForgeTrust.AppSurface.Evidence.Supervision.LinuxRunAccounts/<PrepareAccountMeasurementCollisionAsync>d__56";
        var method = "System.Void " + declaring + "::MoveNext()";
        Assert.Equal(declaring, witness.GetProperty("declaring_type").GetString());
        Assert.Equal(method, witness.GetProperty("method").GetString());
        var document = Assert.Single(supervision.EnumerateObject(), static row => row.Name.Replace('\\', '/') == WitnessSource
            || row.Name.Replace('\\', '/').EndsWith("/" + WitnessSource, StringComparison.Ordinal));
        var lines = document.Value.GetProperty(declaring).GetProperty(method).GetProperty("Lines");
        Assert.True(lines.EnumerateObject().Any(line => int.TryParse(line.Name, NumberStyles.None,
            CultureInfo.InvariantCulture, out var number) && number >= (creation ? 28 : 284)
            && number <= (creation ? 219 : 295) && line.Name == number.ToString(CultureInfo.InvariantCulture)
            && line.Value.TryGetInt64(out var hits) && hits > 0),
            "Prior account/owner hits cannot replace the current compiled variant's method witness.");
    }

    private static string Leaf(string path) => path.Replace('\\', '/').Split('/')[^1];
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Hash(JsonElement value) => Assert.Matches("^[0-9a-f]{64}$", value.GetString()!);
    private static void Properties(JsonElement value, IEnumerable<string> names) =>
        Assert.Equal(names.Order(StringComparer.Ordinal), value.EnumerateObject().Select(static row => row.Name).Order(StringComparer.Ordinal));

    private static JsonDocument Parse(byte[] bytes, StringComparer? comparer = null, int maximum = MaximumBytes)
    {
        Assert.InRange(bytes.Length, 1, maximum);
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        try { Unique(document.RootElement, comparer ?? StringComparer.OrdinalIgnoreCase); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void Unique(JsonElement value, StringComparer comparer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(comparer);
            foreach (var row in value.EnumerateObject())
            {
                Assert.True(names.Add(row.Name), "Duplicate JSON members cannot replace an observation.");
                Unique(row.Value, comparer);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var row in value.EnumerateArray()) Unique(row, comparer);
    }
}
