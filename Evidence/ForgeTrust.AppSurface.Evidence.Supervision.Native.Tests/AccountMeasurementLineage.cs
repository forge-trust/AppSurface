using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Checks the exact C3 → creation → rollback byte lineage without merging or editing any counters.</summary>
/// <remarks>
/// The caller supplies its independently authenticated full current source capture and preserves original raw
/// C3 inputs. It must also compare both staged account source captures byte-for-byte with that current capture.
/// This API establishes consistency only, never native ownership, cleanup, admission, execution success or proof.
/// Reports are bounded at 32 MiB, other inputs at 8 MiB, and the eleven inputs total at 160 MiB. The existing
/// root-TRX validator additionally retains its 1 MiB bound. Invoke before a collector consumes rollback data.
/// </remarks>
internal static class AccountMeasurementLineage
{
    private const int Mib = 1024 * 1024;
    private static readonly string[] Modules = ["ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Evidence.Contracts",
        "ForgeTrust.AppSurface.Evidence.Planner", "ForgeTrust.AppSurface.Evidence.Coverage",
        "ForgeTrust.AppSurface.Evidence.Aspire", "ForgeTrust.AppSurface.Evidence.Supervision", "ForgeTrust.AppSurface.Evidence.Cli"];

    /// <summary>Validates both immediate links and their common original C3 ancestor using actual supplied bytes.</summary>
    /// <param name="currentSourceBytes">Independently verified full parent source capture, not a refreshed or projected map.</param>
    /// <param name="c3BindingBytes">Original raw C3 measurement-binding.json bytes.</param>
    /// <param name="c3CoverageBytes">Original raw C3 native-supervision.json bytes.</param>
    /// <param name="c3NativeBytes">Original C3 native-receipt.json bytes.</param>
    /// <param name="c3RootTrxBytes">Original hash-bound C3 root-files.trx bytes.</param>
    /// <param name="creationBindingBytes">Final C4Accounts v2 binding bytes.</param>
    /// <param name="creationReportBytes">Standard C4Accounts merged report bytes.</param>
    /// <param name="creationNativeBytes">Original C4Accounts driver receipt bytes.</param>
    /// <param name="rollbackBindingBytes">Final C4AccountRollback v2 binding bytes.</param>
    /// <param name="rollbackReportBytes">Standard C4AccountRollback merged report bytes.</param>
    /// <param name="rollbackNativeBytes">Original C4AccountRollback driver receipt bytes.</param>
    internal static void Validate(byte[] currentSourceBytes, byte[] c3BindingBytes, byte[] c3CoverageBytes,
        byte[] c3NativeBytes, byte[] c3RootTrxBytes, byte[] creationBindingBytes, byte[] creationReportBytes,
        byte[] creationNativeBytes, byte[] rollbackBindingBytes, byte[] rollbackReportBytes, byte[] rollbackNativeBytes)
    {
        var reports = new[] { c3CoverageBytes, creationReportBytes, rollbackReportBytes };
        var data = new[] { currentSourceBytes, c3BindingBytes, c3NativeBytes, c3RootTrxBytes,
            creationBindingBytes, creationNativeBytes, rollbackBindingBytes, rollbackNativeBytes };
        Assert.All(reports, static bytes => Assert.InRange(bytes.Length, 1, 32 * Mib));
        Assert.All(data, static bytes => Assert.InRange(bytes.Length, 1, 8 * Mib));
        Assert.InRange(reports.Concat(data).Sum(static bytes => (long)bytes.Length), 1L, 160L * Mib);
        using var source = Parse(currentSourceBytes);
        var head = source.RootElement.GetProperty("source").GetString()!;
        Assert.Matches("^[0-9a-f]{40}$", head);
        AccountMeasurement.ValidateMerged(head, "C4Accounts", currentSourceBytes, creationBindingBytes, creationNativeBytes, creationReportBytes);
        AccountMeasurement.ValidateMerged(head, "C4AccountRollback", currentSourceBytes, rollbackBindingBytes, rollbackNativeBytes, rollbackReportBytes);
        using var c3 = Parse(c3BindingBytes);
        using var c3Native = Parse(c3NativeBytes);
        using var c3Report = Parse(c3CoverageBytes, StringComparer.Ordinal);
        using var creation = Parse(creationBindingBytes);
        using var rollback = Parse(rollbackBindingBytes);
        var binding = c3.RootElement;
        Assert.Equal("issue779-native-instrumentation-v1", binding.GetProperty("schema").GetString());
        Assert.Equal(head, binding.GetProperty("source").GetString());
        Assert.Equal("coverlet.console/10.0.1", binding.GetProperty("collector").GetString());
        foreach (var item in new[] { ("source_capture", currentSourceBytes), ("coverage", c3CoverageBytes), ("native_receipt", c3NativeBytes) })
            Assert.Equal(Digest(item.Item2), binding.GetProperty(item.Item1 + "_sha256").GetString());
        var names = Modules.SelectMany(static module => new[] { module + ".dll", module + ".pdb" }).ToArray();
        foreach (var key in new[] { "original", "instrumented" })
        {
            var map = binding.GetProperty(key);
            Assert.Equal(names.Order(StringComparer.Ordinal), map.EnumerateObject().Select(static row => row.Name).Order(StringComparer.Ordinal));
            foreach (var row in map.EnumerateObject()) Assert.Matches("^[0-9a-f]{64}$", row.Value.GetString()!);
        }
        foreach (var name in names.Where(static name => name.EndsWith(".dll", StringComparison.Ordinal)))
            Assert.NotEqual(binding.GetProperty("original").GetProperty(name).GetString(), binding.GetProperty("instrumented").GetProperty(name).GetString());
        Assert.Equal(Modules.Select(static name => name + ".dll").Order(StringComparer.Ordinal),
            c3Report.RootElement.EnumerateObject().Select(static row => row.Name.Replace('\\', '/').Split('/')[^1]).Order(StringComparer.Ordinal));
        var native = c3Native.RootElement;
        Assert.Equal("issue779-csharp-checkpoint3-native-v1", native.GetProperty("schema").GetString());
        Assert.Equal(head, native.GetProperty("source").GetString());
        Assert.Equal("416b30919e6c4d03b634d9d3fe558884b02f0b91", native.GetProperty("base").GetString());
        Assert.Equal(0, native.GetProperty("exit").GetInt32());
        Assert.False(native.GetProperty("trusted_eligibility").GetBoolean());
        Cases(native.GetProperty("cases"), ["normal", "sdk-normal", "readiness", "factory-stall", "cancel", "stuck-descendant", "disposal-failure"]);
        Cases(native.GetProperty("workspace_guards"), ["bundle-extra-directory", "bundle-input-bytes", "bundle-input-mode", "scratch-fifo", "scratch-hardlink", "scratch-oversize"]);
        var rootTests = binding.GetProperty("root_file_tests");
        Assert.Equal(39, rootTests.GetProperty("executed").GetInt32());
        Assert.Equal(39, rootTests.GetProperty("passed").GetInt32());
        RootFilesystemMeasurement.Validate(rootTests, c3RootTrxBytes);
        var first = creation.RootElement;
        var second = rollback.RootElement;
        Assert.Equal("c3", first.GetProperty("prior_kind").GetString());
        Assert.Equal("C3", first.GetProperty("prior_variant").GetString());
        Assert.Equal("accounts", second.GetProperty("prior_kind").GetString());
        Assert.Equal("C4Accounts", second.GetProperty("prior_variant").GetString());
        var firstArtifact = Artifact(first, "issue779-checkpoint3-application-");
        var secondArtifact = Artifact(second, "issue779-c4-accounts-C4Accounts-");
        Assert.Equal(firstArtifact, secondArtifact); // Same workflow run and attempt, not merely plausible names.
        Link(first, currentSourceBytes, c3BindingBytes, c3CoverageBytes, c3NativeBytes);
        Assert.Equal(Digest(c3RootTrxBytes), first.GetProperty("prior_root_trx_sha256").GetString());
        Link(second, currentSourceBytes, creationBindingBytes, creationReportBytes, creationNativeBytes);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("prior_root_trx_sha256").ValueKind);
    }

    private static void Cases(JsonElement value, string[] expected)
    {
        var rows = value.EnumerateArray().ToArray();
        Assert.Equal(expected, rows.Select(static row => row.GetProperty("case").GetString()));
        foreach (var row in rows)
        {
            foreach (var key in new[] { "passed", "groups_empty", "final_groups_empty" }) Assert.True(row.GetProperty(key).GetBoolean());
            Assert.False(row.GetProperty("native_acceptance").GetBoolean());
        }
    }

    private static string[] Artifact(JsonElement binding, string prefix)
    {
        var match = Regex.Match(binding.GetProperty("prior_artifact_name").GetString()!,
            "\\A" + Regex.Escape(prefix) + "([1-9][0-9]*)-([1-9][0-9]*)\\z", RegexOptions.CultureInvariant);
        Assert.True(match.Success);
        return [match.Groups[1].Value, match.Groups[2].Value];
    }

    private static void Link(JsonElement binding, byte[] source, byte[] priorBinding, byte[] priorCoverage, byte[] priorNative)
    {
        foreach (var item in new[] { ("prior_source_capture", source), ("prior_input_binding", priorBinding),
            ("prior_coverage", priorCoverage), ("prior_native_receipt", priorNative) })
            Assert.Equal(Digest(item.Item2), binding.GetProperty(item.Item1 + "_sha256").GetString());
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static JsonDocument Parse(byte[] bytes, StringComparer? comparer = null)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        try { Unique(document.RootElement, comparer ?? StringComparer.OrdinalIgnoreCase); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void Unique(JsonElement value, StringComparer comparer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(comparer);
            foreach (var row in value.EnumerateObject()) { Assert.True(names.Add(row.Name)); Unique(row.Value, comparer); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var row in value.EnumerateArray()) Unique(row, comparer);
    }
}
