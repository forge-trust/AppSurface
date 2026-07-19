using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ForgeTrust.RazorWire.Tests;

[CollectionDefinition(RazorWirePackageContractCollection.Name, DisableParallelization = true)]
public sealed class RazorWirePackageContractCollection
{
    public const string Name = "RazorWire package contract";
}

[Collection(RazorWirePackageContractCollection.Name)]
public sealed class RazorWirePackageContractTests
{
#if DEBUG
    private const string CurrentConfiguration = "Debug";
#else
    private const string CurrentConfiguration = "Release";
#endif
    private const string PackageVersion = "0.0.0-packagecontract";
    private const string TurboPackagePath = "staticwebassets/razorwire/turbo.es2017-umd.js";
    private const string TurboSha256 = "f9e09e3a3093874fe56d5341ca3594ac959f8b097c9b6171a5b37838da3aec81";
    private static readonly string[] FirstPartyBrowserPackagePaths =
    [
        "staticwebassets/razorwire/razorwire.js",
        "staticwebassets/razorwire/razorwire.islands.js",
        "staticwebassets/razorwire/behavior-kit.js",
        "staticwebassets/razorwire/page-navigation.js",
        "staticwebassets/razorwire/section-copy.js",
        "staticwebassets/razorwire/form-interactions.js"
    ];

    [Fact]
    [Trait("Category", "PackageVerification")]
    public void Package_Should_Ship_Exact_Turbo_Runtime_And_Notice()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var packageDirectory = TestPathUtils.PathUnder(
            Path.GetTempPath(),
            "razorwire-package-contract",
            Guid.NewGuid().ToString("N"));
        Exception? primaryFailure = null;
        Directory.CreateDirectory(packageDirectory);

        try
        {
            var result = RunDotNetPack(repositoryRoot, packageDirectory);
            Assert.True(result == 0, "Expected RazorWire package contract pack to succeed. See the captured test output for dotnet pack diagnostics.");

            var packagePath = TestPathUtils.PathUnder(packageDirectory, $"ForgeTrust.RazorWire.{PackageVersion}.nupkg");
            Assert.True(File.Exists(packagePath), $"Expected package file to exist at {packagePath}.");

            using var archive = ZipFile.OpenRead(packagePath);
            var turboEntry = Assert.Single(archive.Entries, entry => entry.FullName == TurboPackagePath);
            using var turboStream = turboEntry.Open();
            var digest = Convert.ToHexStringLower(SHA256.HashData(turboStream));

            Assert.Equal(TurboSha256, digest);
            Assert.Contains(archive.Entries, entry => entry.FullName == "THIRD-PARTY-NOTICES.md");
            Assert.All(
                FirstPartyBrowserPackagePaths,
                expectedPath => Assert.Contains(archive.Entries, entry => entry.FullName == expectedPath));
            Assert.DoesNotContain(
                archive.Entries,
                entry => entry.FullName.StartsWith("staticwebassets/razorwire/turbo", StringComparison.Ordinal)
                    && entry.FullName.EndsWith(".map", StringComparison.Ordinal));
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            DeletePackageDirectory(packageDirectory, primaryFailure);
        }
    }

    private static int RunDotNetPack(string repositoryRoot, string packageDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("pack");
        startInfo.ArgumentList.Add(TestPathUtils.RelativePath("Web", "ForgeTrust.RazorWire", "ForgeTrust.RazorWire.csproj"));
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(CurrentConfiguration);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--disable-build-servers");
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(packageDirectory);
        startInfo.ArgumentList.Add($"/p:PackageVersion={PackageVersion}");
        startInfo.ArgumentList.Add("/p:VerifyRazorWireGeneratedAssetsBeforePack=false");
        startInfo.ArgumentList.Add("/m:1");
        startInfo.ArgumentList.Add("/nodeReuse:false");
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(3).TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds))
            {
                throw new TimeoutException("RazorWire package contract pack timed out and did not exit within ten seconds after termination was requested.");
            }

            throw new TimeoutException("RazorWire package contract pack did not finish within three minutes.");
        }

        return process.ExitCode;
    }

    private static void DeletePackageDirectory(string packageDirectory, Exception? primaryFailure)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Directory.Delete(packageDirectory, recursive: true);
                return;
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                if (attempt < 3)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(100));
                    continue;
                }

                if (primaryFailure is not null)
                {
                    // Preserve the pack or timeout failure; a still-exiting process can briefly retain its temp files.
                    return;
                }

                throw;
            }
        }
    }

}
