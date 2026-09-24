using System.Diagnostics;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindSourceIdentityTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder(TailwindTestPaths.TemporaryRoot, "tailwind-source-identity", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExactCommittedCheckoutAndVerifierStamp_AreAccepted()
    {
        var (checkout, commit) = await CloneHeadAsync();

        await TailwindSourceIdentity.RequireAsync(checkout, commit, CancellationToken.None);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("0000000000000000000000000000000000000000")]
    public async Task MalformedOrDifferentCommit_IsRejected(string commit)
    {
        var (checkout, _) = await CloneHeadAsync();

        await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindSourceIdentity.RequireAsync(checkout, commit, CancellationToken.None));
    }

    [Theory]
    [InlineData("modified")]
    [InlineData("untracked")]
    public async Task DirtyReleaseInput_IsRejected(string mutation)
    {
        var (checkout, commit) = await CloneHeadAsync();
        var sourceDirectory = TestPathUtils.PathUnder(checkout, "tools", "ForgeTrust.AppSurface.PackageIndex");
        if (mutation == "modified")
            await File.AppendAllTextAsync(TestPathUtils.PathUnder(sourceDirectory, "Program.cs"), "\n// changed verifier\n");
        else
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(sourceDirectory, "untracked-release-input.txt"), "extra source");

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindSourceIdentity.RequireAsync(checkout, commit, CancellationToken.None));

        Assert.Contains("modified, staged, or untracked", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OlderCleanCheckout_IsRejectedWhenVerifierWasBuiltFromAnotherCommit()
    {
        var source = FindRepositoryRoot();
        var olderCommit = await RunGitAsync(source, "rev-parse", "origin/main");
        var (checkout, currentCommit) = await CloneHeadAsync();
        Assert.NotEqual(currentCommit, olderCommit);
        await RunGitAsync(checkout, "checkout", "--detach", olderCommit);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindSourceIdentity.RequireAsync(checkout, olderCommit, CancellationToken.None));

        Assert.Contains("verifier assembly SourceRevisionId stamp", error.Message, StringComparison.Ordinal);
    }

    private async Task<(string Checkout, string Commit)> CloneHeadAsync()
    {
        var source = FindRepositoryRoot();
        var checkout = TestPathUtils.PathUnder(_root, Guid.NewGuid().ToString("N"), "checkout");
        Directory.CreateDirectory(Path.GetDirectoryName(checkout)!);
        await RunGitAsync(source, "clone", "--local", "--no-hardlinks", "--quiet", source, checkout);
        return (checkout, await RunGitAsync(checkout, "rev-parse", "HEAD"));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null && !File.Exists(TestPathUtils.PathUnder(current.FullName, "ForgeTrust.AppSurface.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate repository root for source identity test.");
    }

    private static async Task<string> RunGitAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git for source identity test.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {await stderr}");
        return (await stdout).Trim();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
