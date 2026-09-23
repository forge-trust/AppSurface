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
        Assert.Contains("| tr -d ' ' || true", script, StringComparison.Ordinal);
        Assert.Contains("count_exact_discovered_test", script, StringComparison.Ordinal);
        Assert.Contains(
            "ci_remaining_test_filter=\"FullyQualifiedName!=$v2_release_test\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("ci_remaining_expected_test_count=", script, StringComparison.Ordinal);
        Assert.Contains("ci_remaining_release_count=", script, StringComparison.Ordinal);
        Assert.Contains("ci_remaining_test_log=", script, StringComparison.Ordinal);
        Assert.Contains("tee \"$ci_remaining_test_log\"", script, StringComparison.Ordinal);
        Assert.Matches(
            "verify_test_summary\\s+\\\\\\s+\"\\$ci_remaining_test_log\"\\s+\\\\\\s+\"\\$ci_remaining_expected_test_count\"",
            script);
        Assert.Contains(
            "Skipped:[[:space:]]+[1-9][0-9]*",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Script_fingerprints_transitive_source_and_root_build_inputs_without_generated_outputs()
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
        Assert.Contains("ForgeTrust.AppSurface.Core", script, StringComparison.Ordinal);
        Assert.Contains("Flow/ForgeTrust.AppSurface.Flow", script, StringComparison.Ordinal);
        Assert.Contains("Flow/ForgeTrust.AppSurface.Flow.Generators", script, StringComparison.Ordinal);
        Assert.Contains("Workers/ForgeTrust.AppSurface.Workers", script, StringComparison.Ordinal);
        Assert.Contains("tests/ForgeTrust.AppSurface.Testing", script, StringComparison.Ordinal);
        Assert.Contains("Durable/compatibility/V2WorkHarness", script, StringComparison.Ordinal);
        Assert.Contains("Durable/packed-consumers", script, StringComparison.Ordinal);
        Assert.Contains("Durable/verify-packed-consumers.sh", script, StringComparison.Ordinal);
        Assert.Contains("ForgeTrust.AppSurface.slnx", script, StringComparison.Ordinal);
        Assert.Contains("NuGet.package-gate.config", script, StringComparison.Ordinal);
        Assert.Contains("git -C \"$repo_root\" ls-files", script, StringComparison.Ordinal);
        Assert.Contains("--cached", script, StringComparison.Ordinal);
        Assert.Contains("--others", script, StringComparison.Ordinal);
        Assert.Contains("--exclude-standard", script, StringComparison.Ordinal);
        Assert.DoesNotContain("find \"$repo_root/examples/durable-postgresql.tests\"", script, StringComparison.Ordinal);
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
