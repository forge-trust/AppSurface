using System.Text.Json;
using ForgeTrust.AppSurface.Testing;

public sealed class PackedProofVersionTests
{
    [Theory]
    [InlineData("051737661154d0910e96032c74effa524b13367f", "00123456-7890-1234-5678-901234567890")]
    [InlineData("abcdef1234567890abcdef1234567890abcdef12", "abcdef12-3456-7890-abcd-ef1234567890")]
    [InlineData("075780b64dfc89bc85897bd70499c53b3222445c", "00000000-0000-0000-0000-000000000000")]
    public async Task GeneratedVersion_RestoresNumericCommitAndRunIdentifiers(string sourceCommit, string runId)
    {
        var directory = TestPathUtils.PathUnder(Path.GetTempPath(), $"appsurface-numeric-version-&-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var version = PackedPackageConsumerTests.CreatePackageVersion(sourceCommit, Guid.Parse(runId));
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(directory, "Proof.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(directory, "NuGet.Config"),
                "<configuration><packageSources><clear/></packageSources></configuration>");
            await PackedPackageConsumerTests.RunDotNetAsync(
                directory,
                ["restore", "Proof.csproj", "--configfile", "NuGet.Config", "--nologo", $"-p:Version={version}", $"-p:PackageVersion={version}"],
                "numeric package-version restore regression",
                timeout: TimeSpan.FromSeconds(30));
            using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(directory, "obj", "project.assets.json")));
            Assert.Equal("net10.0", Assert.Single(assets.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject()).Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
