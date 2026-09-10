using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class DurableVerificationScriptContractTests
{
    [Fact]
    public void Script_partitions_ci_discovery_around_the_single_release_proof_and_validates_both_summaries()
    {
        var script = ReadScript();

        Assert.Contains("ci_all_list_log=", script, StringComparison.Ordinal);
        Assert.Contains("count_discovered_tests", script, StringComparison.Ordinal);
        Assert.Contains("count_exact_discovered_test", script, StringComparison.Ordinal);
        Assert.Contains(
            "ci_remaining_test_filter=\"FullyQualifiedName!=$v2_release_test\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("ci_remaining_expected_test_count=", script, StringComparison.Ordinal);
        Assert.Contains("ci_remaining_release_count=", script, StringComparison.Ordinal);
        Assert.Contains("ci_remaining_test_log=", script, StringComparison.Ordinal);
        Assert.Contains("tee \"$ci_remaining_test_log\"", script, StringComparison.Ordinal);
        Assert.Contains("verify_test_summary \\\n      \"$ci_remaining_test_log\" \\\n      \"$ci_remaining_expected_test_count\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "Skipped:[[:space:]]+[1-9][0-9]*",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Script_fingerprints_root_build_restore_inputs_without_generated_outputs_and_uses_portable_sha256()
    {
        var script = ReadScript();

        Assert.Contains("sha256_file()", script, StringComparison.Ordinal);
        Assert.Contains("command -v sha256sum", script, StringComparison.Ordinal);
        Assert.Contains("shasum -a 256", script, StringComparison.Ordinal);
        Assert.Contains("Directory.Build.props", script, StringComparison.Ordinal);
        Assert.Contains("Directory.Build.targets", script, StringComparison.Ordinal);
        Assert.Contains("Directory.Packages.props", script, StringComparison.Ordinal);
        Assert.Contains("global*.json", script, StringComparison.Ordinal);
        Assert.Contains("*nuget*.config", script, StringComparison.Ordinal);
        Assert.Contains("packages.lock.json", script, StringComparison.Ordinal);
        Assert.Contains("Durable/compatibility/V2WorkHarness", script, StringComparison.Ordinal);
        Assert.Contains("ForgeTrust.AppSurface.slnx", script, StringComparison.Ordinal);
        Assert.Contains("NuGet.package-gate.config", script, StringComparison.Ordinal);
        Assert.Contains("! -path '*/bin/*'", script, StringComparison.Ordinal);
        Assert.Contains("! -path '*/obj/*'", script, StringComparison.Ordinal);
        Assert.Contains("source_fingerprint=\"$(sha256_file", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$(shasum -a 256", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$(sha256sum", script, StringComparison.Ordinal);
    }

    private static string ReadScript()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        return File.ReadAllText(TestPathUtils.PathUnder(repositoryRoot, "Durable", "verify-postgresql.sh"));
    }
}
