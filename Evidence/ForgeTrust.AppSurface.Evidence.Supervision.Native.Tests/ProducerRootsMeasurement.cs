using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Checks detached producer-root v2 packets before a supported collector merge; it never edits counters.</summary>
/// <remarks>
/// The caller authenticates the full current source capture, the current workflow run/attempt, and artifact bytes.
/// It must separately call AccountMeasurementLineage.Validate for C3 → C4Accounts → C4AccountRollback.
/// This check binds the immediate rollback bytes only. It cannot establish native execution, physical cleanup,
/// authority or acceptance. Source mappings are reviewed immutable hashes/spans, not filesystem observations.
/// Changing either mapped source requires an explicit mapping review. Ancillary digest claims whose original
/// bytes are not API inputs remain claims validated by the producing fixture and the artifact verifier.
/// Rollback must declare no C2 mutation method and no positive quota-arm contribution; missing quota data stays absent.
/// </remarks>
internal static class ProducerRootsMeasurement
{
    private const int Mib = 1024 * 1024;
    private const string Base = "416b30919e6c4d03b634d9d3fe558884b02f0b91";
    private const string Supervision = "ForgeTrust.AppSurface.Evidence.Supervision";
    private const string MutationDocument = "Evidence/" + Supervision + "/LinuxRetainedReportSet.cs";
    private const string QuotaDocument = "Evidence/" + Supervision + "/SupervisionRunOutputBudget.cs";
    private const string MutationHash = "95ca9977655b70408a238336390438d6db0fce4ae504bf95c2f006535a6105d4";
    private const string QuotaHash = "1603bed8d52f248cbca947dbe5f97827b2df6857727ecebc2482b1f5c6825f7d";
    private static readonly string[] Modules = ["ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Evidence.Contracts",
        "ForgeTrust.AppSurface.Evidence.Planner", "ForgeTrust.AppSurface.Evidence.Coverage",
        "ForgeTrust.AppSurface.Evidence.Aspire", Supervision, "ForgeTrust.AppSurface.Evidence.Cli"];
    private static readonly string[] Cases = ["normal", "test-failure", "cancel", "quota", "ignored-cancel",
        "artifact-mutation", "cleanup-failure"];
    private static readonly string[] Order = ["C3", "C4Accounts", "C4AccountRollback", "C2", "None"];

    /// <summary>Requires a v2 root-only packet and its exact source, raw reports, native receipt and rollback predecessor.</summary>
    /// <param name="currentSource">Full independently verified current source.json bytes, not a projected source map.</param>
    /// <param name="binding">Final issue779-producer-roots-measurement-v2 binding bytes; v1 is ineligible.</param>
    /// <param name="combinedReport">Unmodified standard native-producer-roots.json bytes.</param>
    /// <param name="innerReport">Unmodified standard native-producer-roots-c2.json bytes.</param>
    /// <param name="native">Original seven-case native-receipt.json bytes.</param>
    /// <param name="rollbackBinding">Final C4AccountRollback v2 binding bytes also passed to the earlier lineage validator.</param>
    /// <param name="rollbackReport">Exact unmodified rollback report bytes also passed to that validator.</param>
    /// <param name="rollbackNative">Exact rollback native receipt bytes also passed to that validator.</param>
    /// <remarks>
    /// Reports allow 1 byte–32 MiB, other inputs 1 byte–8 MiB, total at most 160 MiB and JSON depth 64.
    /// Metadata duplicate names are rejected ordinally ignoring case; standard report names use ordinal comparison.
    /// Successful return means data consistency only. The caller still binds artifact run/attempt to its own run,
    /// verifies ancillary raw files and stages these exact raw report bytes for Coverlet's standard merge.
    /// </remarks>
    internal static void Validate(byte[] currentSource, byte[] binding, byte[] combinedReport, byte[] innerReport,
        byte[] native, byte[] rollbackBinding, byte[] rollbackReport, byte[] rollbackNative)
    {
        var reports = new[] { combinedReport, innerReport, rollbackReport };
        var data = new[] { currentSource, binding, native, rollbackBinding, rollbackNative };
        foreach (var bytes in reports) Bounded(bytes, 32 * Mib);
        foreach (var bytes in data) Bounded(bytes, 8 * Mib);
        Require(reports.Concat(data).Sum(static bytes => (long)bytes.Length) <= 160L * Mib);
        using var sourceJson = Parse(currentSource);
        using var bindingJson = Parse(binding);
        using var nativeJson = Parse(native);
        using var rollbackJson = Parse(rollbackBinding);
        using var combinedJson = Parse(combinedReport, report: true);
        using var innerJson = Parse(innerReport, report: true);
        using var rollbackReportJson = Parse(rollbackReport, report: true);
        var source = sourceJson.RootElement;
        var root = bindingJson.RootElement;
        var files = Source(source);
        var head = Text(source.GetProperty("source"));
        Properties(root, ["schema", "context", "original", "instrumented", "restored", "original_binding_sha256",
            "instrumented_binding_sha256", "inner_exit_sha256", "driver_exit_sha256", "outer_collector_exit",
            "inner_collector_exit", "driver_exit", "native_receipt_sha256", "native_receipt_bytes",
            "case_receipt_sha256", "input_inventory_sha256", "reports", "current_witness", "external_predecessor",
            "root_only", "worker_imported", "native_acceptance", "ready_for_gate"]);
        Equal("issue779-producer-roots-measurement-v2", root.GetProperty("schema"));
        Boolean(root, "ready_for_gate", true);
        Boolean(root, "root_only", true);
        Boolean(root, "worker_imported", false);
        Boolean(root, "native_acceptance", false);
        Context(root.GetProperty("context"), head, currentSource);
        Maps(root);
        foreach (var key in new[] { "original_binding_sha256", "instrumented_binding_sha256", "inner_exit_sha256",
            "driver_exit_sha256", "input_inventory_sha256" }) Hash(root.GetProperty(key));
        foreach (var key in new[] { "outer_collector_exit", "inner_collector_exit", "driver_exit" })
            Require(Integer(root.GetProperty(key)) == 0);
        Equal(Digest(native), root.GetProperty("native_receipt_sha256"));
        Require(Integer(root.GetProperty("native_receipt_bytes")) == native.Length);
        Properties(root.GetProperty("case_receipt_sha256"), Cases);
        foreach (var row in root.GetProperty("case_receipt_sha256").EnumerateObject()) Hash(row.Value);
        Native(nativeJson.RootElement, head, Text(root.GetProperty("input_inventory_sha256")));
        var reportBindings = root.GetProperty("reports");
        Properties(reportBindings, ["native-producer-roots-c2.json", "native-producer-roots.json"]);
        ReportBinding(reportBindings.GetProperty("native-producer-roots-c2.json"), innerReport);
        ReportBinding(reportBindings.GetProperty("native-producer-roots.json"), combinedReport);
        Report(combinedJson.RootElement);
        Report(innerJson.RootElement);
        Report(rollbackReportJson.RootElement);
        PriorContribution(rollbackReportJson.RootElement);
        var witnesses = root.GetProperty("current_witness");
        Properties(witnesses, ["C2", "None"]);
        Witness(witnesses.GetProperty("C2"), files, innerJson.RootElement, mutation: true);
        Witness(witnesses.GetProperty("C2"), files, combinedJson.RootElement, mutation: true);
        Witness(witnesses.GetProperty("None"), files, combinedJson.RootElement, mutation: false);
        // Existing account checks validate the immediate rollback packet's own current-variant witness and driver.
        AccountMeasurement.ValidateMerged(head, "C4AccountRollback", currentSource, rollbackBinding, rollbackNative, rollbackReport);
        Predecessor(root.GetProperty("external_predecessor"), rollbackJson.RootElement,
            currentSource, rollbackBinding, rollbackReport, rollbackNative);
        SamePredecessor(root.GetProperty("external_predecessor"), root.GetProperty("context").GetProperty("external_predecessor"));
    }

    private static JsonElement Source(JsonElement source)
    {
        Equal("issue779-checkpoint2-source-v1", source.GetProperty("schema"));
        Hex(source.GetProperty("source"), 40);
        Hex(source.GetProperty("tree"), 40);
        var files = source.GetProperty("files");
        var rows = files.EnumerateObject().ToArray();
        Require(rows.Length is > 0 and <= 10000 && Integer(source.GetProperty("count")) == rows.Length);
        foreach (var row in rows)
        {
            Require(!row.Name.StartsWith("/", StringComparison.Ordinal) && !row.Name.Contains('\\')
                && row.Name.Split('/').All(static part => part is not ("" or "." or "..")));
            Hash(row.Value.GetProperty("sha256"));
            Hex(row.Value.GetProperty("git_blob"), 40);
            Require(Regex.IsMatch(Text(row.Value.GetProperty("mode")), "\\A[0-7]{4}\\z", RegexOptions.CultureInvariant));
        }
        foreach (var path in new[] { ".github/issue779-checkpoint4/producer_roots.py", ".github/issue779-checkpoint2/run-native.py",
            MutationDocument, QuotaDocument }) Hash(files.GetProperty(path).GetProperty("sha256"));
        return files;
    }

    private static void Context(JsonElement context, string head, byte[] source)
    {
        Properties(context, ["source", "base", "source_capture_sha256", "package_smoke_sha256", "paths", "external_predecessor"]);
        Equal(head, context.GetProperty("source"));
        Equal(Base, context.GetProperty("base"));
        Equal(Digest(source), context.GetProperty("source_capture_sha256"));
        Hash(context.GetProperty("package_smoke_sha256"));
        var paths = context.GetProperty("paths");
        Properties(paths, ["tool", "mutation_tool", "runtime", "subject", "evidence"]);
        foreach (var path in paths.EnumerateObject())
        {
            var text = Text(path.Value);
            Require(text.StartsWith("/", StringComparison.Ordinal) && !text.Contains('\0') && !text.Contains('\\')
                && text[1..].Split('/').All(static part => part is not ("" or "." or "..")));
        }
        Require(Text(paths.GetProperty("tool")) != Text(paths.GetProperty("mutation_tool")));
    }

    private static void Maps(JsonElement binding)
    {
        var names = Modules.SelectMany(static module => new[] { module + ".dll", module + ".pdb" }).ToArray();
        foreach (var kind in new[] { "original", "instrumented", "restored" })
        {
            Properties(binding.GetProperty(kind), ["None", "C2"]);
            foreach (var variant in new[] { "None", "C2" })
            {
                var map = binding.GetProperty(kind).GetProperty(variant);
                Properties(map, names);
                foreach (var row in map.EnumerateObject()) Hash(row.Value);
            }
        }
        foreach (var variant in new[] { "None", "C2" })
            foreach (var name in names)
            {
                var original = Text(binding.GetProperty("original").GetProperty(variant).GetProperty(name));
                Equal(original, binding.GetProperty("restored").GetProperty(variant).GetProperty(name));
                if (name.EndsWith(".dll", StringComparison.Ordinal))
                    Require(original != Text(binding.GetProperty("instrumented").GetProperty(variant).GetProperty(name)));
            }
    }

    private static void Native(JsonElement native, string head, string inventory)
    {
        Equal("issue779-csharp-checkpoint2-native-v1", native.GetProperty("schema"));
        Equal(head, native.GetProperty("source"));
        Equal(Base, native.GetProperty("base"));
        Require(Integer(native.GetProperty("exit")) == 0);
        Boolean(native, "native_acceptance", false);
        Boolean(native, "trusted_eligibility", false);
        var rows = native.GetProperty("cases").EnumerateArray().ToArray();
        Require(rows.Select(static row => Text(row.GetProperty("case"))).SequenceEqual(Cases, StringComparer.Ordinal));
        var generations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            Hex(row.GetProperty("generation"), 32);
            Require(generations.Add(Text(row.GetProperty("generation"))));
            var name = Text(row.GetProperty("case"));
            var cleanup = name == "cleanup-failure";
            foreach (var key in new[] { "passed", "groups_empty", "final_groups_empty" }) Boolean(row, key, true);
            Boolean(row, "native_acceptance", false);
            Require(!row.TryGetProperty("failure_category", out _));
            Boolean(row, "accounts_absent", !cleanup);
            Boolean(row, "accounts_quarantined", cleanup);
            var exit = Integer(row.GetProperty("owner_terminal").GetProperty("wrapper_exit"));
            Require(name == "normal" ? exit == 0 : exit != 0);
            Equal(inventory, row.GetProperty("input_inventory_sha256"));
            foreach (var channel in new[] { "stdout", "stderr" }) Hash(row.GetProperty(channel + "_sha256"));
            if (cleanup) Boolean(row, "quarantined", true);
            if (name is "cancel" or "ignored-cancel") Boolean(row, "cancel_sent", true);
            if (name == "ignored-cancel") Boolean(row, "descendant_term_observed", true);
            if (name == "quota")
            {
                var observed = row.GetProperty("output_observation");
                Boolean(observed, "quota_exceeded", true);
                Require(Integer(observed.GetProperty("received_byte_limit")) == 16 * Mib
                    && Integer(observed.GetProperty("received_bytes")) > 16 * Mib);
            }
            if (name == "artifact-mutation") Mutation(row.GetProperty("artifact_mutation"));
        }
    }

    private static void Mutation(JsonElement events)
    {
        var rows = events.EnumerateArray().ToArray();
        Require(rows.Length == 2);
        var mutated = rows[0];
        Equal("evidence-c2-artifact-mutation-v1", mutated.GetProperty("schema"));
        Equal("mutated", mutated.GetProperty("phase"));
        foreach (var key in new[] { "original_sha256", "changed_sha256" }) Hash(mutated.GetProperty(key));
        Require(Text(mutated.GetProperty("original_sha256")) != Text(mutated.GetProperty("changed_sha256")));
        Require(Integer(mutated.GetProperty("length")) is > 0 and <= Mib);
        Require(Integer(mutated.GetProperty("device_major")) >= 0 && Integer(mutated.GetProperty("device_minor")) >= 0
            && Integer(mutated.GetProperty("inode")) > 0);
        var rejected = rows[1];
        Properties(rejected, ["schema", "phase", "returned_bytes"]);
        Equal("evidence-c2-artifact-mutation-v1", rejected.GetProperty("schema"));
        Equal("read-rejected", rejected.GetProperty("phase"));
        Require(Integer(rejected.GetProperty("returned_bytes")) == 0);
    }

    private static void ReportBinding(JsonElement binding, byte[] bytes)
    {
        Properties(binding, ["sha256", "bytes"]);
        Equal(Digest(bytes), binding.GetProperty("sha256"));
        Require(Integer(binding.GetProperty("bytes")) == bytes.Length);
    }

    private static void Report(JsonElement report)
    {
        var modules = report.EnumerateObject().ToArray();
        Require(modules.Select(static row => Leaf(row.Name)).Order(StringComparer.Ordinal)
            .SequenceEqual(Modules.Select(static name => name + ".dll").Order(StringComparer.Ordinal), StringComparer.Ordinal));
        foreach (var module in modules)
            foreach (var document in module.Value.EnumerateObject())
                foreach (var type in document.Value.EnumerateObject())
                    foreach (var method in type.Value.EnumerateObject())
                    {
                        foreach (var line in method.Value.GetProperty("Lines").EnumerateObject())
                            Require(long.TryParse(line.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                                && number > 0 && Integer(line.Value) >= 0);
                        foreach (var branch in method.Value.GetProperty("Branches").EnumerateArray())
                        {
                            Properties(branch, ["Line", "Offset", "EndOffset", "Path", "Ordinal", "Hits"]);
                            foreach (var field in branch.EnumerateObject()) Require(Integer(field.Value) >= 0);
                        }
                    }
    }

    private static void PriorContribution(JsonElement rollbackReport)
    {
        var supervision = rollbackReport.EnumerateObject().Single(static row => Leaf(row.Name) == Supervision + ".dll").Value;
        var mutationType = Supervision + ".LinuxRetainedReportSet";
        var mutationMethod = "System.Byte[] " + mutationType + "::MutateRetainedReportForC2(System.Threading.CancellationToken)";
        var quotaType = Supervision + ".SupervisionRunOutputBudget";
        var quotaMethod = "System.Boolean " + quotaType + "::Charge(System.Int32)";
        var quotaCandidates = new List<JsonElement>();
        foreach (var document in supervision.EnumerateObject())
        {
            // A declaration is disallowed even at zero hits or under an unrelated document path.
            Require(!document.Value.TryGetProperty(mutationType, out var mutation)
                || !mutation.TryGetProperty(mutationMethod, out _));
            var name = document.Name.Replace('\\', '/');
            if ((name == QuotaDocument || name.EndsWith("/" + QuotaDocument, StringComparison.Ordinal))
                && document.Value.TryGetProperty(quotaType, out var quota) && quota.TryGetProperty(quotaMethod, out var method))
                quotaCandidates.Add(method);
        }
        Require(quotaCandidates.Count <= 1);
        if (quotaCandidates.Count == 0) return;
        // Report already checked integer line keys/hits. Numeric aliases still name the same point and may not duplicate it.
        var points = quotaCandidates[0].GetProperty("Lines").EnumerateObject().Where(static line =>
            long.Parse(line.Name, NumberStyles.None, CultureInfo.InvariantCulture) == 173).ToArray();
        Require(points.Length <= 1 && points.All(static line => Integer(line.Value) == 0));
    }

    private static void Witness(JsonElement witness, JsonElement files, JsonElement report, bool mutation)
    {
        Properties(witness, ["document", "type", "method", "start", "end", "source_sha256"]);
        var document = mutation ? MutationDocument : QuotaDocument;
        var hash = mutation ? MutationHash : QuotaHash;
        var type = Supervision + (mutation ? ".LinuxRetainedReportSet" : ".SupervisionRunOutputBudget");
        var method = mutation ? "System.Byte[] " + type + "::MutateRetainedReportForC2(System.Threading.CancellationToken)"
            : "System.Boolean " + type + "::Charge(System.Int32)";
        var start = mutation ? 148 : 173;
        var end = mutation ? 199 : 173;
        Equal(document, witness.GetProperty("document"));
        Equal(type, witness.GetProperty("type"));
        Equal(method, witness.GetProperty("method"));
        Equal(hash, witness.GetProperty("source_sha256"));
        Equal(hash, files.GetProperty(document).GetProperty("sha256"));
        Require(Integer(witness.GetProperty("start")) == start && Integer(witness.GetProperty("end")) == end);
        var supervision = report.EnumerateObject().Single(static row => Leaf(row.Name) == Supervision + ".dll").Value;
        var matches = supervision.EnumerateObject().Where(row => row.Name.Replace('\\', '/')
            .EndsWith("/" + document, StringComparison.Ordinal)).ToArray();
        Require(matches.Length == 1);
        var lines = matches[0].Value.GetProperty(type).GetProperty(method).GetProperty("Lines");
        Require(lines.EnumerateObject().Any(line => long.TryParse(line.Name, NumberStyles.None,
            CultureInfo.InvariantCulture, out var number) && number >= start && number <= end && Integer(line.Value) > 0));
    }

    private static void Predecessor(JsonElement predecessor, JsonElement rollback,
        byte[] source, byte[] binding, byte[] report, byte[] native)
    {
        Properties(predecessor, ["kind", "variant", "artifact_name", "merge_order", "source_capture_sha256",
            "input_binding_sha256", "coverage_sha256", "native_receipt_sha256"]);
        Equal("accounts-rollback", predecessor.GetProperty("kind"));
        Equal("C4AccountRollback", predecessor.GetProperty("variant"));
        Require(predecessor.GetProperty("merge_order").EnumerateArray().Select(Text).SequenceEqual(Order, StringComparer.Ordinal));
        foreach (var item in new[] { ("source_capture_sha256", source), ("input_binding_sha256", binding),
            ("coverage_sha256", report), ("native_receipt_sha256", native) }) Equal(Digest(item.Item2), predecessor.GetProperty(item.Item1));
        Equal("accounts", rollback.GetProperty("prior_kind"));
        Equal("C4Accounts", rollback.GetProperty("prior_variant"));
        Require(rollback.GetProperty("prior_root_trx_sha256").ValueKind == JsonValueKind.Null);
        foreach (var key in new[] { "prior_source_capture_sha256", "prior_input_binding_sha256", "prior_coverage_sha256",
            "prior_native_receipt_sha256" }) Hash(rollback.GetProperty(key));
        Equal(Digest(source), rollback.GetProperty("prior_source_capture_sha256"));
        var artifact = Artifact(predecessor.GetProperty("artifact_name"), "issue779-c4-accounts-C4AccountRollback-");
        var prior = Artifact(rollback.GetProperty("prior_artifact_name"), "issue779-c4-accounts-C4Accounts-");
        Require(artifact.SequenceEqual(prior, StringComparer.Ordinal));
    }

    private static string[] Artifact(JsonElement value, string prefix)
    {
        var match = Regex.Match(Text(value), "\\A" + Regex.Escape(prefix) + "([1-9][0-9]*)-([1-9][0-9]*)\\z", RegexOptions.CultureInvariant);
        Require(match.Success);
        return [match.Groups[1].Value, match.Groups[2].Value];
    }

    // The top-level predecessor is already validated; compare values without depending on JSON object member order.
    private static void SamePredecessor(JsonElement predecessor, JsonElement contextPredecessor)
    {
        Properties(contextPredecessor, predecessor.EnumerateObject().Select(static row => row.Name));
        foreach (var row in predecessor.EnumerateObject())
        {
            var other = contextPredecessor.GetProperty(row.Name);
            if (row.Name == "merge_order")
                Require(row.Value.EnumerateArray().Select(Text).SequenceEqual(other.EnumerateArray().Select(Text), StringComparer.Ordinal));
            else Equal(Text(row.Value), other);
        }
    }

    private static string Leaf(string value) => value.Replace('\\', '/').Split('/')[^1];
    private static string Text(JsonElement value) => value.GetString() ?? throw new InvalidDataException("Missing measurement text.");
    private static string Digest(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static void Equal(string expected, JsonElement actual) => Require(Text(actual) == expected);
    private static void Boolean(JsonElement value, string key, bool expected) => Require(value.GetProperty(key).GetBoolean() == expected);
    private static void Hash(JsonElement value) => Hex(value, 64);
    private static void Hex(JsonElement value, int length)
    {
        var text = Text(value);
        Require(text.Length == length && text.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }
    private static long Integer(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _));
        return value.GetInt64();
    }
    private static void Properties(JsonElement value, IEnumerable<string> names) => Require(names.Order(StringComparer.Ordinal)
        .SequenceEqual(value.EnumerateObject().Select(static row => row.Name).Order(StringComparer.Ordinal), StringComparer.Ordinal));
    private static void Bounded(byte[] bytes, int maximum) => Require(bytes is { Length: > 0 } && bytes.Length <= maximum);
    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidDataException("Producer-root measurement data does not match its contract.");
    }
    private static JsonDocument Parse(byte[] bytes, bool report = false)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        try { Unique(document.RootElement, report ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void Unique(JsonElement value, StringComparer comparer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(comparer);
            foreach (var row in value.EnumerateObject()) { Require(names.Add(row.Name)); Unique(row.Value, comparer); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var row in value.EnumerateArray()) Unique(row, comparer);
    }
}
