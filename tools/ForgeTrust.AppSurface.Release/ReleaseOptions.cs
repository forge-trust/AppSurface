using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Markdig;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Parsed release CLI options shared by every command.
/// </summary>
internal sealed record ReleaseOptions(
    string Command,
    string RepositoryRoot,
    SemVer Version,
    string? Tag,
    DateOnly? Date,
    bool DryRun,
    string? ReportPath,
    string? GitHubOutputPath,
    bool FailOnWarnings,
    bool AllowExistingTargets);

/// <summary>
/// Minimal SemVer 2.0 model used by release automation.
/// </summary>
internal sealed partial record SemVer(int Major, int Minor, int Patch, string? Prerelease)
{
    /// <summary>
    /// Gets whether the version is a stable SemVer identity.
    /// </summary>
    internal bool IsStable => Prerelease is null;

    /// <summary>
    /// Gets the annotated git tag expected for this version.
    /// </summary>
    internal string TagName => $"v{this}";

    /// <summary>
    /// Parses a release version and rejects leading-v tags, build metadata, and invalid SemVer shapes.
    /// </summary>
    /// <param name="value">Version string supplied by the user.</param>
    /// <returns>The parsed version.</returns>
    internal static SemVer Parse(string value)
    {
        if (value.StartsWith('v'))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-version-leading-v",
                $"Release version '{value}' must not start with `v`.",
                "The CLI separates package version identity from git tag identity.",
                "Use `--version 0.1.0` and, for publish, `--tag v0.1.0`.",
                "tools/ForgeTrust.AppSurface.Release/README.md#quickstart"));
        }

        var match = SemVerRegex().Match(value);
        if (!match.Success)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-version-invalid",
                $"Release version '{value}' is not valid SemVer 2.0.",
                "Coordinated package and GitHub Release automation requires unambiguous version identity.",
                "Use `x.y.z` or `x.y.z-label.n` without build metadata.",
                "tools/ForgeTrust.AppSurface.Release/README.md#quickstart"));
        }

        return new SemVer(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value),
            match.Groups["pre"].Success ? match.Groups["pre"].Value : null);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Prerelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Prerelease}";
    }

    [GeneratedRegex(@"^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>(?:0|[1-9A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9A-Za-z-][0-9A-Za-z-]*))*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemVerRegex();
}
