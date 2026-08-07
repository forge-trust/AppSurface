namespace NamedCanaryLab.Tests;

public sealed class NamedCanaryLabReadmeTests
{
    [Fact]
    public void Readme_ProvidesASafeOrderedAdoptionPath()
    {
        var readme = File.ReadAllText(Path.Join(FindRepositoryRoot(), "examples", "named-canary-lab", "README.md"));

        Assert.Contains("## First local proof (POSIX)", readme, StringComparison.Ordinal);
        Assert.Contains("bash examples/named-canary-lab/verify.sh pass", readme, StringComparison.Ordinal);
        Assert.Contains("## Manual walkthrough", readme, StringComparison.Ordinal);
        Assert.Contains("### PowerShell", readme, StringComparison.Ordinal);
        Assert.Contains("trap cleanup 0 INT TERM", readme, StringComparison.Ordinal);
        Assert.Contains("The named-canary lab did not become reachable before the local deadline.", readme, StringComparison.Ordinal);
        Assert.Contains("allows up to two minutes for the loopback bind after that build", readme, StringComparison.Ordinal);
        Assert.Contains("## Deterministic local scenarios", readme, StringComparison.Ordinal);
        Assert.Contains("ASCAN406", readme, StringComparison.Ordinal);
        Assert.Contains("ASCAN403", readme, StringComparison.Ordinal);
        Assert.Contains("ASCAN404", readme, StringComparison.Ordinal);
        Assert.Contains("## Copy the pattern, not the lab", readme, StringComparison.Ordinal);
        Assert.Contains("not health, readiness, traffic rollout analysis", readme, StringComparison.Ordinal);
        Assert.Contains("set +x", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("${{", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("runs-on:", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("markerFingerprint", readme, StringComparison.Ordinal);
        Assert.True(
            readme.IndexOf("## First local proof (POSIX)", StringComparison.Ordinal)
                < readme.IndexOf("## Manual walkthrough", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found.");
    }
}
