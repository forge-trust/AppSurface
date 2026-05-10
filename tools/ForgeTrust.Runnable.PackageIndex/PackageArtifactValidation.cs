using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml.Linq;

namespace ForgeTrust.Runnable.PackageIndex;

/// <summary>
/// Validates prerelease package artifacts against the resolved publish plan.
/// </summary>
internal sealed class PackageArtifactValidator
{
    private const string TailwindRuntimePackagePrefix = "ForgeTrust.Runnable.Web.Tailwind.Runtime.";

    private static readonly IReadOnlyDictionary<string, string> TailwindRuntimeBinaryNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["linux-arm64"] = "tailwindcss-linux-arm64",
            ["linux-x64"] = "tailwindcss-linux-x64",
            ["osx-arm64"] = "tailwindcss-macos-arm64",
            ["osx-x64"] = "tailwindcss-macos-x64",
            ["win-x64"] = "tailwindcss-windows-x64.exe"
        };

    /// <summary>
    /// Validates the package output directory and returns a markdown-ready report.
    /// </summary>
    /// <param name="plan">Resolved package publish plan.</param>
    /// <param name="artifactsDirectory">Directory containing produced <c>.nupkg</c> files.</param>
    /// <param name="packageVersion">Exact package version expected in every artifact.</param>
    /// <returns>A validation report for the inspected artifacts.</returns>
    internal PackageArtifactValidationReport Validate(
        PackagePublishPlan plan,
        string artifactsDirectory,
        string packageVersion)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsDirectory);
        PackageVersionValidator.RequirePrerelease(packageVersion);

        if (!Directory.Exists(artifactsDirectory))
        {
            throw new PackageIndexException($"Package artifact directory '{artifactsDirectory}' does not exist.");
        }

        var packages = Directory.EnumerateFiles(artifactsDirectory, "*.nupkg", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var expectedPackageIds = plan.Entries
            .Select(entry => entry.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inspectedPackages = new List<InspectedPackage>(packages.Length);

        foreach (var packagePath in packages)
        {
            inspectedPackages.Add(InspectPackage(packagePath, expectedPackageIds));
        }

        foreach (var expected in plan.Entries)
        {
            var matches = inspectedPackages
                .Where(package => string.Equals(package.PackageId, expected.PackageId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 0)
            {
                throw new PackageIndexException($"Missing package artifact for '{expected.PackageId}' version '{packageVersion}'.");
            }

            if (matches.Length > 1)
            {
                throw new PackageIndexException($"Package artifact directory contains multiple artifacts for '{expected.PackageId}'.");
            }

            ValidatePackage(expected, matches[0], packageVersion);
        }

        foreach (var unexpected in inspectedPackages.Where(package => !expectedPackageIds.Contains(package.PackageId)))
        {
            throw new PackageIndexException($"Unexpected package artifact '{unexpected.PackageId}' at '{unexpected.PackagePath}'.");
        }

        return new PackageArtifactValidationReport(
            packageVersion,
            plan.Entries.Select(entry => new PackageArtifactValidationReportEntry(
                entry.PackageId,
                entry.ProjectPath,
                entry.Decision,
                entry.ExpectedDependencyPackageIds)).ToArray());
    }

    private static void ValidatePackage(
        PackagePublishPlanEntry expected,
        InspectedPackage inspected,
        string packageVersion)
    {
        if (!string.Equals(inspected.PackageVersion, packageVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageIndexException(
                $"Package '{expected.PackageId}' has version '{inspected.PackageVersion}', expected '{packageVersion}'.");
        }

        RequireMetadata(expected.PackageId, "authors", inspected.Authors);
        RequireMetadata(expected.PackageId, "description", inspected.Description);
        if (string.Equals(inspected.Description, "Package Description", StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageIndexException($"Package '{expected.PackageId}' still has the default NuGet package description.");
        }

        RequireMetadata(expected.PackageId, "license", inspected.License);
        RequireMetadata(expected.PackageId, "repository url", inspected.RepositoryUrl);
        RequireMetadata(expected.PackageId, "tags", inspected.Tags);
        RequireMetadata(expected.PackageId, "readme", inspected.Readme);

        var isDotnetToolPackage = inspected.PackageTypes.Contains("DotnetTool", StringComparer.OrdinalIgnoreCase);
        if (expected.IsTool && !isDotnetToolPackage)
        {
            throw new PackageIndexException($"Tool package '{expected.PackageId}' must declare package type 'DotnetTool'.");
        }

        if (!expected.IsTool && isDotnetToolPackage)
        {
            throw new PackageIndexException($"Package '{expected.PackageId}' must not declare package type 'DotnetTool'.");
        }

        var expectedPayloadPath = GetExpectedPayloadPath(expected.PackageId);
        if (expectedPayloadPath is not null
            && !inspected.EntryPaths.Contains(expectedPayloadPath, StringComparer.OrdinalIgnoreCase))
        {
            throw new PackageIndexException(
                $"Package '{expected.PackageId}' is missing required payload '{expectedPayloadPath}'.");
        }

        foreach (var expectedDependency in expected.ExpectedDependencyPackageIds)
        {
            if (!inspected.Dependencies.TryGetValue(expectedDependency, out var dependencyVersions))
            {
                throw new PackageIndexException(
                    $"Package '{expected.PackageId}' is missing dependency '{expectedDependency}'.");
            }

            var mismatchedVersions = dependencyVersions
                .Where(dependencyVersion => !DependencyVersionMatches(dependencyVersion, packageVersion))
                .ToArray();
            if (mismatchedVersions.Length > 0)
            {
                throw new PackageIndexException(
                    $"Package '{expected.PackageId}' dependency '{expectedDependency}' has version '{string.Join(", ", mismatchedVersions)}', expected same-version dependency '{packageVersion}'.");
            }
        }

        foreach (var assembly in inspected.FirstPartyAssemblyVersions)
        {
            if (!AssemblyInformationalVersionMatches(assembly.InformationalVersion, packageVersion))
            {
                throw new PackageIndexException(
                    $"Package '{expected.PackageId}' contains assembly '{assembly.EntryPath}' with informational version '{assembly.InformationalVersion}', expected '{packageVersion}' or '{packageVersion}+<metadata>'.");
            }
        }
    }

    private static InspectedPackage InspectPackage(string packagePath, IReadOnlySet<string> packageIds)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntry = archive.Entries.SingleOrDefault(
            entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (nuspecEntry is null)
        {
            throw new PackageIndexException($"Package artifact '{packagePath}' does not contain a .nuspec file.");
        }

        using var nuspecStream = nuspecEntry.Open();
        var nuspec = XDocument.Load(nuspecStream);
        var metadata = nuspec.Root?.Element(nuspec.Root.Name.Namespace + "metadata")
            ?? throw new PackageIndexException($"Package artifact '{packagePath}' does not contain nuspec metadata.");
        var ns = metadata.Name.Namespace;
        var repository = metadata.Element(ns + "repository");
        var packageTypes = metadata
            .Elements(ns + "packageTypes")
            .Elements(ns + "packageType")
            .Select(element => element.Attribute("name")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
        var dependencies = metadata
            .Descendants(ns + "dependency")
            .Select(element => new
            {
                Id = element.Attribute("id")?.Value,
                Version = element.Attribute("version")?.Value
            })
            .Where(dependency => !string.IsNullOrWhiteSpace(dependency.Id))
            .GroupBy(dependency => dependency.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(dependency => dependency.Version ?? string.Empty)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var firstPartyAssemblyVersions = archive.Entries
            .Where(entry => IsFirstPartyAssemblyEntry(entry, packageIds))
            .Select(ReadAssemblyVersion)
            .ToArray();
        var entryPaths = archive.Entries
            .Select(entry => NormalizePackagePath(entry.FullName))
            .ToArray();

        var packageId = GetElementValue(metadata, ns, "id");
        var packageVersion = GetElementValue(metadata, ns, "version");
        if (string.IsNullOrWhiteSpace(packageId))
        {
            throw new PackageIndexException($"Package artifact '{packagePath}' does not define nuspec metadata 'id'.");
        }

        if (string.IsNullOrWhiteSpace(packageVersion))
        {
            throw new PackageIndexException($"Package artifact '{packagePath}' does not define nuspec metadata 'version'.");
        }

        return new InspectedPackage(
            packagePath,
            packageId,
            packageVersion,
            GetElementValue(metadata, ns, "authors"),
            GetElementValue(metadata, ns, "description"),
            GetElementValue(metadata, ns, "license"),
            repository?.Attribute("url")?.Value,
            GetElementValue(metadata, ns, "tags"),
            GetElementValue(metadata, ns, "readme"),
            packageTypes,
            dependencies,
            entryPaths,
            firstPartyAssemblyVersions);
    }

    private static string? GetElementValue(XElement metadata, XNamespace ns, string elementName)
    {
        return metadata.Element(ns + elementName)?.Value;
    }

    private static void RequireMetadata(string packageId, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PackageIndexException($"Package '{packageId}' must define nuspec metadata '{name}'.");
        }
    }

    private static bool DependencyVersionMatches(string dependencyVersion, string packageVersion)
    {
        return string.Equals(dependencyVersion, packageVersion, StringComparison.OrdinalIgnoreCase)
            || string.Equals(dependencyVersion, $"[{packageVersion}]", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dependencyVersion, $"[{packageVersion}, )", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dependencyVersion, $"[{packageVersion},)", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFirstPartyAssemblyEntry(ZipArchiveEntry entry, IReadOnlySet<string> packageIds)
    {
        if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.StartsWith("ref/", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.StartsWith("refs/", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.Contains("/ref/", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.Contains("/refs/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var assemblyName = Path.GetFileNameWithoutExtension(entry.FullName);
        return packageIds.Contains(assemblyName);
    }

    private static string? GetExpectedPayloadPath(string packageId)
    {
        if (!packageId.StartsWith(TailwindRuntimePackagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rid = packageId[TailwindRuntimePackagePrefix.Length..];
        if (!TailwindRuntimeBinaryNames.TryGetValue(rid, out var binaryName))
        {
            throw new PackageIndexException($"Tailwind runtime package '{packageId}' uses unsupported runtime id '{rid}'.");
        }

        return $"runtimes/{rid}/native/{binaryName}";
    }

    private static string NormalizePackagePath(string entryPath)
    {
        return entryPath.Replace('\\', '/').TrimStart('/');
    }

    private static InspectedAssemblyVersion ReadAssemblyVersion(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        try
        {
            using var peReader = new PEReader(buffer);
            var metadata = peReader.GetMetadataReader();
            var informationalVersion = ReadAssemblyInformationalVersion(metadata);
            if (string.IsNullOrWhiteSpace(informationalVersion))
            {
                throw new PackageIndexException(
                    $"Package assembly '{entry.FullName}' must define AssemblyInformationalVersionAttribute.");
            }

            return new InspectedAssemblyVersion(entry.FullName, informationalVersion);
        }
        catch (BadImageFormatException ex)
        {
            throw new PackageIndexException(
                $"Package assembly '{entry.FullName}' could not be inspected for assembly version metadata: {ex.Message}");
        }
    }

    private static string? ReadAssemblyInformationalVersion(MetadataReader metadata)
    {
        foreach (var attributeHandle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(attributeHandle);
            if (!IsAssemblyInformationalVersionAttribute(metadata, attribute))
            {
                continue;
            }

            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1)
            {
                throw new PackageIndexException("AssemblyInformationalVersionAttribute has an invalid custom attribute prolog.");
            }

            return blob.ReadSerializedString();
        }

        return null;
    }

    private static bool IsAssemblyInformationalVersionAttribute(
        MetadataReader metadata,
        CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind != HandleKind.MemberReference)
        {
            return false;
        }

        var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        return metadata.StringComparer.Equals(type.Namespace, "System.Reflection")
            && metadata.StringComparer.Equals(type.Name, "AssemblyInformationalVersionAttribute");
    }

    private static bool AssemblyInformationalVersionMatches(string informationalVersion, string packageVersion)
    {
        return string.Equals(informationalVersion, packageVersion, StringComparison.OrdinalIgnoreCase)
            || informationalVersion.StartsWith($"{packageVersion}+", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Renders package artifact validation results for workflow artifacts.
/// </summary>
internal static class PackageArtifactReportRenderer
{
    /// <summary>
    /// Renders the validation report as markdown.
    /// </summary>
    /// <param name="report">Validation report to render.</param>
    /// <returns>Markdown report content.</returns>
    internal static string RenderMarkdown(PackageArtifactValidationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Package artifact validation");
        builder.AppendLine();
        builder.AppendLine($"Version: `{report.PackageVersion}`");
        builder.AppendLine();
        builder.AppendLine("| Package | Project | Decision | Expected package dependencies |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (var entry in report.Entries)
        {
            var dependencies = entry.ExpectedDependencyPackageIds.Count == 0
                ? "none"
                : string.Join(", ", entry.ExpectedDependencyPackageIds.Select(value => $"`{value}`"));
            builder.AppendLine($"| `{entry.PackageId}` | `{entry.ProjectPath}` | `{FormatDecision(entry.Decision)}` | {dependencies} |");
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string FormatDecision(PackagePublishDecision decision)
    {
        return decision switch
        {
            PackagePublishDecision.Publish => "publish",
            PackagePublishDecision.SupportPublish => "support-publish",
            PackagePublishDecision.DoNotPublish => "do-not-publish",
            _ => decision.ToString()
        };
    }
}

/// <summary>
/// Ensures package versions used by the prerelease workflow are safe for NuGet identity.
/// </summary>
internal static class PackageVersionValidator
{
    /// <summary>
    /// Validates that the package version is a prerelease SemVer identity without build metadata.
    /// </summary>
    /// <param name="packageVersion">Package version to validate.</param>
    internal static void RequirePrerelease(string packageVersion)
    {
        if (string.IsNullOrWhiteSpace(packageVersion))
        {
            throw new PackageIndexException("Package version must be provided.");
        }

        if (packageVersion.Contains('+', StringComparison.Ordinal))
        {
            throw new PackageIndexException("Package version must not include SemVer build metadata because NuGet strips build metadata from package identity.");
        }

        var parts = packageVersion.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new PackageIndexException($"Package version '{packageVersion}' must be a prerelease version with a SemVer suffix.");
        }

        var versionParts = parts[0].Split('.');
        if (versionParts.Length != 3 || versionParts.Any(part => !int.TryParse(part, out _)))
        {
            throw new PackageIndexException($"Package version '{packageVersion}' must use a major.minor.patch SemVer core.");
        }
    }
}

/// <summary>
/// Result of a successful package artifact validation run.
/// </summary>
/// <param name="PackageVersion">Exact package version inspected.</param>
/// <param name="Entries">Validated package report rows.</param>
internal sealed record PackageArtifactValidationReport(
    string PackageVersion,
    IReadOnlyList<PackageArtifactValidationReportEntry> Entries);

/// <summary>
/// One validated package row in the artifact report.
/// </summary>
/// <param name="PackageId">Validated package id.</param>
/// <param name="ProjectPath">Project that produced the package.</param>
/// <param name="Decision">Publish decision from the manifest.</param>
/// <param name="ExpectedDependencyPackageIds">Expected same-version package dependency ids.</param>
internal sealed record PackageArtifactValidationReportEntry(
    string PackageId,
    string ProjectPath,
    PackagePublishDecision Decision,
    IReadOnlyList<string> ExpectedDependencyPackageIds);

/// <summary>
/// Metadata and payload facts inspected from one NuGet package artifact.
/// </summary>
/// <param name="PackagePath">Absolute or caller-supplied path to the inspected <c>.nupkg</c> file.</param>
/// <param name="PackageId">Nuspec package id. Expected to be non-empty after inspection.</param>
/// <param name="PackageVersion">Nuspec package version. Expected to be non-empty after inspection.</param>
/// <param name="Authors">Nuspec authors metadata, or <c>null</c> when absent.</param>
/// <param name="Description">Nuspec description metadata, or <c>null</c> when absent.</param>
/// <param name="License">Nuspec license expression or value, or <c>null</c> when absent.</param>
/// <param name="RepositoryUrl">Nuspec repository URL, or <c>null</c> when absent.</param>
/// <param name="Tags">Nuspec package tags, or <c>null</c> when absent.</param>
/// <param name="Readme">Nuspec README path, or <c>null</c> when absent.</param>
/// <param name="PackageTypes">Declared nuspec package type names such as <c>DotnetTool</c>.</param>
/// <param name="Dependencies">Dependency ids mapped to all distinct nuspec versions observed across dependency groups.</param>
/// <param name="EntryPaths">Normalized archive entry paths contained in the package.</param>
/// <param name="FirstPartyAssemblyVersions">First-party implementation assemblies and their informational versions.</param>
internal sealed record InspectedPackage(
    string PackagePath,
    string PackageId,
    string PackageVersion,
    string? Authors,
    string? Description,
    string? License,
    string? RepositoryUrl,
    string? Tags,
    string? Readme,
    IReadOnlyList<string> PackageTypes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Dependencies,
    IReadOnlyList<string> EntryPaths,
    IReadOnlyList<InspectedAssemblyVersion> FirstPartyAssemblyVersions);

/// <summary>
/// Informational version metadata read from a first-party assembly inside a package artifact.
/// </summary>
/// <param name="EntryPath">Archive path for the inspected assembly entry.</param>
/// <param name="InformationalVersion">Assembly informational version value read from metadata.</param>
internal sealed record InspectedAssemblyVersion(
    string EntryPath,
    string InformationalVersion);
