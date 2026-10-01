using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Checks that release evidence is produced by the exact, clean source and stamped verifier build.</summary>
internal static class TailwindSourceIdentity
{
    internal static async Task RequireAsync(string repositoryRoot, string expectedCommit, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(expectedCommit, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant))
            throw new PackageIndexException("--source-commit must be a full 40-character commit ID.");
        var head = await RunGitAsync(repositoryRoot, ["rev-parse", "HEAD"], cancellationToken);
        if (!string.Equals(head, expectedCommit, StringComparison.OrdinalIgnoreCase))
            throw new PackageIndexException($"Exact-source check failed: checked-out HEAD '{head}' differs from --source-commit '{expectedCommit}'.");
        var status = await RunGitAsync(repositoryRoot,
        [
            "status", "--porcelain", "--untracked-files=all", "--",
            "tools/ForgeTrust.AppSurface.PackageIndex",
            "tools/ForgeTrust.AppSurface.ReleaseContracts",
            "Web/ForgeTrust.AppSurface.Web.Tailwind",
            "packages/package-index.yml",
            "scripts/verify-tailwind-package-consumer.sh",
            "Directory.Build.props",
            "Directory.Build.targets",
            "Directory.Packages.props",
            "NuGet.package-gate.config",
            "NuGet.config",
            "global.json"
        ], cancellationToken);
        if (!string.IsNullOrWhiteSpace(status))
            throw new PackageIndexException("Exact-source check failed: relevant release inputs in the checkout are modified, staged, or untracked.");

        var information = typeof(TailwindSourceIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new PackageIndexException("Exact-source check failed: verifier assembly has no informational-version source stamp.");
        var stampedCommit = information.Split('+', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (stampedCommit is null || !string.Equals(stampedCommit, expectedCommit, StringComparison.OrdinalIgnoreCase))
            throw new PackageIndexException($"Exact-source check failed: verifier assembly SourceRevisionId stamp '{stampedCommit ?? "missing"}' differs from expected commit '{expectedCommit}'. Build the verifier with /p:SourceRevisionId=<full-commit>.");
    }

    private static async Task<string> RunGitAsync(string repositoryRoot, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = repositoryRoot, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new PackageIndexException("Could not start git for exact-source validation.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await stderr;
        if (process.ExitCode != 0) throw new PackageIndexException($"Exact-source git check failed: {error.Trim()}");
        return (await stdout).Trim();
    }
}
