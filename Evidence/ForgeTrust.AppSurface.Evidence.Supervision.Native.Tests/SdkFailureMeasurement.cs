using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Checks separate failed-SDK measurement data; this comparison creates no native execution or proof.</summary>
internal static class SdkFailureMeasurement
{
    private static readonly string[] Modules = ["ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Evidence.Contracts",
        "ForgeTrust.AppSurface.Evidence.Planner", "ForgeTrust.AppSurface.Evidence.Coverage", "ForgeTrust.AppSurface.Evidence.Aspire",
        "ForgeTrust.AppSurface.Evidence.Supervision"];

    /// <summary>Requires the finalized negative control, independent maps/handoffs and genuine failed-arm report hits.</summary>
    /// <param name="binding">The current original-matrix binding, including the two new input digests.</param>
    /// <param name="head">The independently observed current checkout commit.</param>
    /// <param name="receipt">The original root controller's finalized failed-SDK receipt.</param>
    /// <param name="coverage">The standard collector's separate failed-SDK report, before its supported merge.</param>
    /// <param name="producerSource">Current source bytes independently checked against the complete source capture.</param>
    internal static void Validate(JsonElement binding, string head, byte[] receipt, byte[] coverage, string producerSource)
    {
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(receipt)), binding.GetProperty("sdk_failure_receipt_sha256").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(coverage)), binding.GetProperty("sdk_failure_coverage_sha256").GetString());
        using var document = JsonDocument.Parse(receipt);
        var root = document.RootElement;
        Assert.Equal("issue779-sdk-failed-producer-v1", root.GetProperty("schema").GetString());
        Assert.Equal("sdk-test-failure", root.GetProperty("case").GetString());
        Assert.Equal(head, root.GetProperty("source").GetString());
        foreach (var flag in new[] { "passed", "finalized", "restored_originals", "groups_empty", "final_groups_empty" })
            Assert.True(root.GetProperty(flag).GetBoolean());
        Assert.False(root.GetProperty("native_acceptance").GetBoolean());
        Assert.Equal(1, root.GetProperty("root_exit").GetInt32());
        Assert.Equal(coverage.Length, root.GetProperty("coverage_bytes").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(coverage)), root.GetProperty("coverage_sha256").GetString());
        var info = root.GetProperty("sdk_failure");
        Assert.Equal("Incomplete", info.GetProperty("execution_verdict").GetString());
        Assert.Equal("None", info.GetProperty("claim_kind").GetString());
        Assert.Equal("None", info.GetProperty("eligibility").GetString());
        Assert.True(info.GetProperty("cleanup_completed").GetBoolean());
        var producer = info.GetProperty("producer_result");
        Assert.Equal("Failed", producer.GetProperty("outcome").GetString());
        Assert.Equal("The restricted coverage process exited with code 1.", producer.GetProperty("diagnostic").GetString());
        Assert.Equal(0, producer.GetProperty("artifact_count").GetInt32());
        Assert.Equal(0, producer.GetProperty("assertion_count").GetInt32());
        var guard = info.GetProperty("original_application_ready_guard");
        Assert.Equal("evidence-sdk-producer-failure-v1", guard.GetProperty("schema").GetString());
        Assert.Equal(1, guard.GetProperty("producer_exit_code").GetInt32());
        Assert.True(guard.GetProperty("original_application_ready_guard").GetBoolean());
        Assert.False(guard.GetProperty("native_acceptance").GetBoolean());
        Assert.Equal("appsurface-evidence-application-" + root.GetProperty("generation").GetString() + ".service",
            guard.GetProperty("application_unit").GetString());
        var state = info.GetProperty("producer");
        Assert.Equal(1, state.GetProperty("normal_code").GetInt32());
        Assert.Equal(1, state.GetProperty("normal_status").GetInt32());
        Assert.Equal(root.GetProperty("producer").GetProperty("pid").GetUInt32(), state.GetProperty("captured_pid").GetUInt32());
        Assert.False(state.GetProperty("cleanup_failed").GetBoolean());
        Assert.False(state.GetProperty("native_acceptance").GetBoolean());
        Assert.Equal("None", state.GetProperty("pending_failure").GetString());
        foreach (var flag in new[] { "physically_settled", "group_empty_twice", "pump_joined", "output_drained" })
            Assert.True(state.GetProperty(flag).GetBoolean());
        var tests = state.GetProperty("tests");
        foreach (var item in new[] { ("failed", 1), ("passed", 5), ("skipped", 0), ("total", 6) })
            Assert.Equal(item.Item2, tests.GetProperty(item.Item1).GetInt32());
        var accounts = root.GetProperty("accounts").EnumerateArray().ToArray();
        var numeric = root.GetProperty("numeric_accounts").EnumerateArray().ToArray();
        Assert.Equal(8, accounts.Length);
        Assert.Equal(8, numeric.Length);
        Assert.All(accounts, static row => Assert.False(row.GetProperty("present").GetBoolean()));
        Assert.All(numeric, static row => Assert.True(row.GetProperty("absent").GetBoolean()));
        var generation = root.GetProperty("generation").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", generation);
        var expectedNames = new[] { ("passwd", "evw"), ("passwd", "evs"), ("passwd", "eva"),
            ("group", "evw"), ("group", "evs"), ("group", "eva"), ("group", "evr"), ("group", "evg") }
            .Select(item => item.Item1 + ":" + item.Item2 + generation[..28]).Order(StringComparer.Ordinal);
        Assert.Equal(expectedNames, accounts.Select(static row => row.GetProperty("table").GetString()
            + ":" + row.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        var identities = info.GetProperty("account_binding");
        var uidKeys = new[] { "worker_uid", "subject_uid", "application_uid" };
        var gidKeys = new[] { "worker_gid", "subject_gid", "application_gid", "results_gid", "resource_access_gid" };
        Assert.Equal(uidKeys.Concat(gidKeys).Order(StringComparer.Ordinal), identities.EnumerateObject()
            .Select(static row => row.Name).Order(StringComparer.Ordinal));
        foreach (var keys in new[] { uidKeys, gidKeys })
        {
            var values = keys.Select(key => identities.GetProperty(key).GetUInt32()).ToArray();
            Assert.All(values, static value => Assert.InRange(value, 1u, uint.MaxValue - 1));
            Assert.Equal(values.Length, values.Distinct().Count());
        }
        var expectedNumbers = uidKeys.Select(key => "passwd:" + identities.GetProperty(key).GetUInt32().ToString(CultureInfo.InvariantCulture))
            .Concat(gidKeys.Select(key => "group:" + identities.GetProperty(key).GetUInt32().ToString(CultureInfo.InvariantCulture))).Order(StringComparer.Ordinal);
        Assert.Equal(expectedNumbers, numeric.Select(static row => row.GetProperty("table").GetString() + ":"
            + row.GetProperty("id").GetUInt32().ToString(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));
        foreach (var item in new[] { ("worker", "worker"), ("producer", "subject"), ("apphost", "application") })
        {
            var sample = item.Item1 == "worker" ? info.GetProperty("worker") : root.GetProperty(item.Item1);
            Assert.Equal(identities.GetProperty(item.Item2 + "_uid").GetUInt32(),
                sample.GetProperty(item.Item1 == "worker" ? "worker_uid" : "uid").GetUInt32());
            Assert.Equal(identities.GetProperty(item.Item2 + "_gid").GetUInt32(),
                sample.GetProperty(item.Item1 == "worker" ? "worker_gid" : "gid").GetUInt32());
        }
        var settled = Assert.Single(root.GetProperty("application_observations").EnumerateArray(),
            static row => row.GetProperty("phase").GetString() == "settled");
        Assert.Equal(1, settled.GetProperty("main_code").GetInt32());
        Assert.Equal(0, settled.GetProperty("main_status").GetInt32());
        Assert.False(settled.GetProperty("monitor_failed").GetBoolean());
        foreach (var flag in new[] { "group_empty_twice", "pumps_joined", "pending_start_joined" })
            Assert.True(settled.GetProperty(flag).GetBoolean());

        var names = Modules.SelectMany(static module => new[] { module + ".dll", module + ".pdb" }).Order(StringComparer.Ordinal).ToArray();
        foreach (var map in new[] { "sdk_original", "sdk_instrumented" })
            Assert.Equal(names, root.GetProperty(map).EnumerateObject().Select(static row => row.Name).Order(StringComparer.Ordinal));
        foreach (var name in names)
        {
            var original = root.GetProperty("sdk_original").GetProperty(name).GetString();
            var instrumented = root.GetProperty("sdk_instrumented").GetProperty(name).GetString();
            Assert.Matches("^[0-9a-f]{64}$", original!);
            Assert.Matches("^[0-9a-f]{64}$", instrumented!);
            Assert.Equal(binding.GetProperty("sdk_original").GetProperty(name).GetString(), original);
            if (name.EndsWith(".dll", StringComparison.Ordinal)) Assert.NotEqual(original, instrumented);
        }
        var worker = Assert.Single(root.GetProperty("worker_measurements").EnumerateArray());
        Assert.Equal("evidence-worker-measurement-v2", worker.GetProperty("schema").GetString());
        Assert.Equal(1, worker.GetProperty("normal_exit_code").GetInt32());
        Assert.True(worker.GetProperty("original_pidfd_exit").GetBoolean());
        Assert.False(worker.GetProperty("native_acceptance").GetBoolean());
        var expected = info.GetProperty("worker");
        foreach (var item in new[] { ("pid", "worker_pid"), ("uid", "worker_uid"), ("gid", "worker_gid") })
        {
            var value = worker.GetProperty(item.Item1).GetUInt32();
            Assert.InRange(value, 1u, uint.MaxValue - 1);
            Assert.Equal(expected.GetProperty(item.Item2).GetUInt32(), value);
        }
        Assert.True(worker.GetProperty("start_time").GetUInt64() > 0);
        Assert.Equal(expected.GetProperty("cgroup").GetString(), worker.GetProperty("cgroup").GetString());
        var rows = worker.GetProperty("modules").EnumerateArray().ToArray();
        Assert.Equal(Modules.Select(static name => name + ".dll").Order(StringComparer.Ordinal),
            rows.Select(static row => row.GetProperty("module").GetString()).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "ForgeTrust.AppSurface.Cli.dll", "ForgeTrust.AppSurface.Evidence.Contracts.dll",
            "ForgeTrust.AppSurface.Evidence.Coverage.dll", "ForgeTrust.AppSurface.Evidence.Aspire.dll" })
        {
            var row = Assert.Single(rows, item => item.GetProperty("module").GetString() == name);
            Assert.Equal("unchanged-worker-handoff", row.GetProperty("outcome").GetString());
            Assert.Equal(root.GetProperty("sdk_instrumented").GetProperty(name).GetString(), row.GetProperty("module_sha256").GetString());
            Assert.Matches("^[0-9a-f]{64}$", row.GetProperty("worker_sha256").GetString()!);
            Assert.Equal(row.GetProperty("worker_sha256").GetString(), row.GetProperty("host_sha256").GetString());
        }
        using var report = JsonDocument.Parse(coverage);
        Assert.Equal(Modules.Select(static name => name + ".dll").Order(StringComparer.Ordinal),
            report.RootElement.EnumerateObject().Select(static row => Path.GetFileName(row.Name)).Order(StringComparer.Ordinal));
        // Locate the current failure-only statement from source, not a mutable report label or a guessed line offset.
        var lines = producerSource.Split('\n');
        var marker = Assert.Single(lines.Select((text, index) => (text, index)), static line =>
            line.text.Contains("The restricted coverage process exited with code {process.ExitCode", StringComparison.Ordinal));
        Assert.Contains("result = CreateResult(producer, EvidenceProducerOutcome.Failed", lines[marker.index - 1]);
        var hit = report.RootElement.EnumerateObject().SelectMany(static module => module.Value.EnumerateObject())
            .Where(static doc => Path.GetFileName(doc.Name) == "EvidenceRestrictedCoverageProducer.cs")
            .SelectMany(static doc => doc.Value.EnumerateObject()).SelectMany(static type => type.Value.EnumerateObject())
            .Where(static method => method.Name.Contains("RunAsync", StringComparison.Ordinal))
            .SelectMany(static method => method.Value.GetProperty("Lines").EnumerateObject())
            .Any(line => (line.Name == marker.index.ToString(CultureInfo.InvariantCulture)
                || line.Name == (marker.index + 1).ToString(CultureInfo.InvariantCulture)) && line.Value.GetInt32() > 0);
        Assert.True(hit, "The separate failed-SDK report must contain the real nonzero-producer result arm.");
    }
}
