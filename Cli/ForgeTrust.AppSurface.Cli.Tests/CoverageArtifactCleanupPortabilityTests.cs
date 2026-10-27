namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactCleanupPortabilityTests
{
    [Fact]
    public void OwnedFixtureCleanupRemovesNestedReadOnlyGitObjects()
    {
        var temp = TestDirectory.Create();
        var root = temp.Path;
        try
        {
            var file = temp.WriteFile(".git/objects/aa/object", "owned fixture");
            File.SetAttributes(file, FileAttributes.ReadOnly);
            temp.Dispose();
            Assert.False(Directory.Exists(root));
        }
        finally { temp.Dispose(); }
    }
}
