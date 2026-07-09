namespace ForgeTrust.RazorWire.Tests;

public sealed class StaticAuthProjectionDocsTests
{
    [Fact]
    public void RazorWireDocs_ShouldDocument_StaticAuthProjectionContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var docs = File.ReadAllText(Path.Join(repositoryRoot, "Web", "ForgeTrust.RazorWire", "Docs", "static-auth-projection.md"));
        var readme = File.ReadAllText(Path.Join(repositoryRoot, "Web", "ForgeTrust.RazorWire", "README.md"));
        var adapterReadme = File.ReadAllText(Path.Join(repositoryRoot, "Web", "ForgeTrust.RazorWire.Auth.AspNetCore", "README.md"));

        Assert.Contains("X-RazorWire-Static-Export: auth-anonymous-v1", docs, StringComparison.Ordinal);
        Assert.Contains("rw:auth-anonymous", docs, StringComparison.Ordinal);
        Assert.Contains("RWEXPORT010", docs, StringComparison.Ordinal);
        Assert.Contains("auth-missing-fallback", docs, StringComparison.Ordinal);
        Assert.Contains("auth-private-content", docs, StringComparison.Ordinal);
        Assert.Contains("hybrid", docs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Docs/static-auth-projection.md", readme, StringComparison.Ordinal);
        Assert.Contains("RWEXPORT010", readme, StringComparison.Ordinal);
        Assert.Contains("../ForgeTrust.RazorWire/Docs/static-auth-projection.md", adapterReadme, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "README.md"))
                && Directory.Exists(Path.Join(directory.FullName, "Web")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
