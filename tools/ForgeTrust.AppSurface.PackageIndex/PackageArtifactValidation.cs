using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Validates package artifacts against the resolved publish plan.
/// </summary>
internal sealed class PackageArtifactValidator
{
    private const string RequiredPackageProjectUrl = "https://appsurface.dev";
    private const string TailwindRuntimePackagePrefix = "ForgeTrust.AppSurface.Web.Tailwind.Runtime.";
    private const int MaxNoticeBytes = 256 * 1024;

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
    /// <param name="repositoryRoot">
    /// Optional repository root used to resolve source-path and version-source evidence when payload inventory validation is enabled.
    /// </param>
    /// <param name="payloadInventory">Optional redistributed payload inventory to validate against the produced package artifacts.</param>
    /// <returns>A validation report for the inspected artifacts.</returns>
    internal PackageArtifactValidationReport Validate(
        PackagePublishPlan plan,
        string artifactsDirectory,
        string packageVersion,
        string? repositoryRoot = null,
        PackagePayloadInventory? payloadInventory = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsDirectory);
        PackageVersionValidator.Require(packageVersion, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);

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
        if (payloadInventory is not null)
        {
            ValidatePayloadInventoryPackageIds(payloadInventory, expectedPackageIds);
        }

        var inspectedPackages = new List<InspectedPackage>(packages.Length);
        var payloadSummariesByPackageId = new Dictionary<string, PackagePayloadValidationSummary>(StringComparer.OrdinalIgnoreCase);

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

            payloadSummariesByPackageId[expected.PackageId] = ValidatePackage(
                expected,
                matches[0],
                expectedPackageIds,
                packageVersion,
                repositoryRoot,
                payloadInventory);
        }

        foreach (var unexpected in inspectedPackages.Where(package => !expectedPackageIds.Contains(package.PackageId)))
        {
            throw new PackageIndexException($"Unexpected package artifact '{unexpected.PackageId}' at '{unexpected.PackagePath}'.");
        }

        var inspectedByPackageId = inspectedPackages.ToDictionary(
            package => package.PackageId,
            package => package,
            StringComparer.OrdinalIgnoreCase);

        return new PackageArtifactValidationReport(
            packageVersion,
            plan.Entries.Select(entry =>
            {
                var inspected = inspectedByPackageId[entry.PackageId];
                return new PackageArtifactValidationReportEntry(
                    entry.PackageId,
                    entry.ProjectPath,
                    entry.Decision,
                    entry.ExpectedDependencyPackageIds,
                    inspected.PackagePath,
                    entry.IsTool,
                    entry.IsTool ? entry.ToolCommandName : string.Empty,
                    payloadSummariesByPackageId.TryGetValue(entry.PackageId, out var payloadSummary)
                        ? payloadSummary?.Results ?? []
                        : [],
                    payloadSummary?.SuspiciousEntryCount ?? 0,
                    payloadSummary?.CoveredSuspiciousEntryCount ?? 0);
            }).ToArray());
    }

    private static PackagePayloadValidationSummary ValidatePackage(
        PackagePublishPlanEntry expected,
        InspectedPackage inspected,
        IReadOnlySet<string> firstPartyPackageIds,
        string packageVersion,
        string? repositoryRoot,
        PackagePayloadInventory? payloadInventory)
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
        RequireMetadata(expected.PackageId, "project url", inspected.ProjectUrl);
        if (!IsRequiredPackageProjectUrl(inspected.ProjectUrl!))
        {
            throw new PackageIndexException(
                $"Package '{expected.PackageId}' project url must be '{RequiredPackageProjectUrl}', found '{inspected.ProjectUrl}'.");
        }

        RequireMetadata(expected.PackageId, "repository url", inspected.RepositoryUrl);
        RequireMetadata(expected.PackageId, "tags", inspected.Tags);
        RequireMetadata(expected.PackageId, "readme", inspected.Readme);
        var readmePath = NormalizePackagePath(inspected.Readme!);
        if (!inspected.EntryPaths.Contains(readmePath, StringComparer.OrdinalIgnoreCase))
        {
            throw new PackageIndexException(
                $"Package '{expected.PackageId}' is missing required README entry '{inspected.Readme}'.");
        }

        var isDotnetToolPackage = inspected.PackageTypes.Contains("DotnetTool", StringComparer.OrdinalIgnoreCase);
        if (expected.IsTool && !isDotnetToolPackage)
        {
            throw new PackageIndexException($"Tool package '{expected.PackageId}' must declare package type 'DotnetTool'.");
        }

        if (!expected.IsTool && isDotnetToolPackage)
        {
            throw new PackageIndexException($"Package '{expected.PackageId}' must not declare package type 'DotnetTool'.");
        }

        if (isDotnetToolPackage && inspected.ToolCommandNames.Count == 0)
        {
            throw new PackageIndexException($"Tool package '{expected.PackageId}' must include DotnetToolSettings.xml with at least one command.");
        }

        if (expected.IsTool)
        {
            var settingsFilesMissingExpectedCommand = inspected.ToolSettingsFiles
                .Where(settingsFile =>
                {
                    var distinctCommandNames = settingsFile.CommandNames
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    return distinctCommandNames.Length != 1
                        || !string.Equals(distinctCommandNames[0], expected.ToolCommandName, StringComparison.Ordinal);
                })
                .ToArray();
            if (settingsFilesMissingExpectedCommand.Length > 0)
            {
                var settingsFileDescriptions = settingsFilesMissingExpectedCommand
                    .Select(settingsFile =>
                    {
                        var commandNames = settingsFile.CommandNames.Count == 0
                            ? "none"
                            : string.Join(", ", settingsFile.CommandNames);
                        return $"{settingsFile.EntryPath} ({commandNames})";
                    });
                throw new PackageIndexException(
                    $"Tool package '{expected.PackageId}' settings file(s) [{string.Join("; ", settingsFileDescriptions)}] must each declare only expected command '{expected.ToolCommandName}'.");
            }
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

        var expectedFirstPartyDependencies = expected.ExpectedDependencyPackageIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unexpectedFirstPartyDependencies = inspected.Dependencies.Keys
            .Where(dependencyId => firstPartyPackageIds.Contains(dependencyId)
                && !expectedFirstPartyDependencies.Contains(dependencyId))
            .OrderBy(dependencyId => dependencyId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unexpectedFirstPartyDependencies.Length > 0)
        {
            throw new PackageIndexException(
                $"Package '{expected.PackageId}' has unexpected first-party dependencies: {string.Join(", ", unexpectedFirstPartyDependencies)}.");
        }

        foreach (var assembly in inspected.FirstPartyAssemblyVersions)
        {
            if (!AssemblyInformationalVersionMatches(assembly.InformationalVersion, packageVersion))
            {
                throw new PackageIndexException(
                    $"Package '{expected.PackageId}' contains assembly '{assembly.EntryPath}' with informational version '{assembly.InformationalVersion}', expected '{packageVersion}' or '{packageVersion}+<metadata>'.");
            }
        }

        if (payloadInventory is null)
        {
            return PackagePayloadValidationSummary.Empty;
        }

        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            throw new PackageIndexException(
                "Package payload inventory validation requires a repository root. Problem: source-path and version-source evidence cannot be resolved. Cause: verify-packages was invoked without repository context. Fix: pass the repository root into package artifact validation. Docs: packages/README.md#redistributed-payloads.");
        }

        return ValidatePackagePayloads(expected, inspected, firstPartyPackageIds, repositoryRoot, payloadInventory);
    }

    private static PackagePayloadValidationSummary ValidatePackagePayloads(
        PackagePublishPlanEntry expected,
        InspectedPackage inspected,
        IReadOnlySet<string> firstPartyPackageIds,
        string repositoryRoot,
        PackagePayloadInventory inventory)
    {
        var notices = inventory.Notices
            .Where(notice => string.Equals(notice.PackageId, expected.PackageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var audits = inventory.Audits
            .Where(audit => string.Equals(audit.PackageId, expected.PackageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var results = new List<PackagePayloadValidationResult>(notices.Length + audits.Length);
        var coveredPackageEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var noticeCoveredPackageEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var notice in notices)
        {
            var payloadPatterns = ValidatePackagePatterns(expected.PackageId, notice.Id, notice.PayloadPatterns);
            var matchedPayloads = MatchPackageEntries(inspected.EntryPaths, payloadPatterns);
            if (matchedPayloads.Count == 0)
            {
                throw new PackageIndexException(
                    $"ASPKG124 {expected.PackageId}: notice '{notice.Id}' matched no package payload entries. Problem: the payload inventory contains stale or incorrect payload_patterns. Cause: no artifact entry matched {FormatPatterns(payloadPatterns)}. Fix: update packages/third-party-payloads.yml or remove the stale notice record. Docs: packages/README.md#redistributed-payloads.");
            }

            foreach (var entryPath in matchedPayloads)
            {
                coveredPackageEntries.Add(entryPath);
                noticeCoveredPackageEntries.Add(entryPath);
            }

            var noticePaths = notice.NoticePaths
                .Select(path => NormalizePackagePathStrict(path, $"notice '{notice.Id}' notice_paths"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var noticePath in noticePaths.Where(path => !inspected.EntryPaths.Contains(path, StringComparer.OrdinalIgnoreCase)))
            {
                throw new PackageIndexException(
                    $"ASPKG125 {expected.PackageId}: notice '{notice.Id}' requires missing notice path '{noticePath}'. Problem: a redistributed payload lacks package-visible notice text. Cause: the package artifact does not contain the declared notice path. Fix: pack THIRD-PARTY-NOTICES.md at the package root or update notice_paths. Docs: packages/README.md#redistributed-payloads.");
            }

            var noticeText = string.Join(
                "\n",
                noticePaths.Select(path => ReadPackageTextEntry(inspected.PackagePath, path, expected.PackageId, notice.Id)));
            foreach (var marker in notice.Markers.Where(marker => !noticeText.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PackageIndexException(
                    $"ASPKG126 {expected.PackageId}: notice '{notice.Id}' is missing marker '{marker}'. Problem: the package notice does not prove the declared component/version/license text. Cause: the packaged notice text is incomplete or stale. Fix: update the package THIRD-PARTY-NOTICES.md entry so it includes the marker. Docs: packages/README.md#redistributed-payloads.");
            }

            ValidateRepositoryPaths(repositoryRoot, expected.PackageId, notice.Id, "source_paths", notice.SourcePaths);
            if (!string.IsNullOrWhiteSpace(notice.VersionSourcePath)
                || !string.IsNullOrWhiteSpace(notice.VersionSourceContains))
            {
                ValidateVersionSource(
                    repositoryRoot,
                    expected.PackageId,
                    notice.Id,
                    notice.VersionSourcePath,
                    notice.VersionSourceContains);
            }

            results.Add(new PackagePayloadValidationResult(
                expected.PackageId,
                notice.Id,
                notice.Component,
                "notice",
                "notice_enforced",
                matchedPayloads,
                noticePaths,
                notice.VersionSourcePath ?? string.Empty));
        }

        foreach (var audit in audits)
        {
            var appliesTo = ValidatePackagePatterns(expected.PackageId, audit.Id, audit.AppliesTo);
            var matchedPayloads = MatchPackageEntries(inspected.EntryPaths, appliesTo);
            if (matchedPayloads.Count == 0)
            {
                throw new PackageIndexException(
                    $"ASPKG127 {expected.PackageId}: audit '{audit.Id}' matched no package payload entries. Problem: the audit record is stale or too broad to prove package contents. Cause: no artifact entry matched {FormatPatterns(appliesTo)}. Fix: update applies_to or remove the stale audit. Docs: packages/README.md#redistributed-payloads.");
            }

            var noticeOverlaps = matchedPayloads
                .Where(noticeCoveredPackageEntries.Contains)
                .ToArray();
            if (noticeOverlaps.Length > 0)
            {
                throw new PackageIndexException(
                    $"ASPKG138 {expected.PackageId}: audit '{audit.Id}' overlaps notice-covered payload '{noticeOverlaps[0]}'. Problem: audit records must not mask notice-required redistributed payloads. Cause: applies_to includes entries already covered by notice records. Fix: narrow applies_to so audits cover only non-notice payloads, or keep the notice record as the sole coverage for those entries. Docs: packages/README.md#redistributed-payloads.");
            }

            foreach (var entryPath in matchedPayloads)
            {
                coveredPackageEntries.Add(entryPath);
            }

            ValidateRepositoryPaths(repositoryRoot, expected.PackageId, audit.Id, "source_paths", audit.SourcePaths);
            if (string.Equals(audit.EvidenceKind, "generated_first_party", StringComparison.OrdinalIgnoreCase)
                && audit.GeneratedPaths.Count == 0)
            {
                throw new PackageIndexException(
                    $"ASPKG137 {expected.PackageId}: audit '{audit.Id}' must define generated_paths for generated_first_party evidence. Problem: generated-first-party payloads need both source and output evidence. Cause: the audit record names generated evidence without naming generated repository outputs. Fix: add generated_paths for the emitted files or use a different evidence_kind. Docs: packages/README.md#redistributed-payloads.");
            }

            ValidateRepositoryPaths(repositoryRoot, expected.PackageId, audit.Id, "generated_paths", audit.GeneratedPaths);
            results.Add(new PackagePayloadValidationResult(
                expected.PackageId,
                audit.Id,
                audit.MatchedRule ?? audit.EvidenceKind,
                audit.EvidenceKind,
                "audit_enforced",
                matchedPayloads,
                [],
                audit.Source));
        }

        var suspiciousEntries = inspected.EntryPaths
            .Select(entryPath => new SuspiciousPackageEntry(entryPath, GetSuspiciousPayloadRule(entryPath, firstPartyPackageIds)))
            .Where(entry => entry.Rule is not null)
            .ToArray();
        foreach (var suspiciousEntry in suspiciousEntries.Where(entry => !coveredPackageEntries.Contains(entry.EntryPath)))
        {
            throw new PackageIndexException(
                $"ASPKG123 {expected.PackageId}: {suspiciousEntry.EntryPath} matched {suspiciousEntry.Rule} but has no notice or audit classification. Problem: package redistributes a suspicious payload without provenance evidence. Cause: no packages/third-party-payloads.yml notice or audit record covers the package entry. Fix: add a notice record with payload_patterns and notice_paths, or add a narrow generated-first-party audit record. Docs: packages/README.md#redistributed-payloads.");
        }

        var coveredSuspiciousEntryCount = suspiciousEntries.Count(entry => coveredPackageEntries.Contains(entry.EntryPath));
        return new PackagePayloadValidationSummary(results, suspiciousEntries.Length, coveredSuspiciousEntryCount);
    }

    private static void ValidatePayloadInventoryPackageIds(
        PackagePayloadInventory inventory,
        IReadOnlySet<string> expectedPackageIds)
    {
        var unknownPackageIds = inventory.Notices
            .Select(notice => notice.PackageId)
            .Concat(inventory.Audits.Select(audit => audit.PackageId))
            .Where(packageId => !string.IsNullOrWhiteSpace(packageId) && !expectedPackageIds.Contains(packageId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (unknownPackageIds.Length > 0)
        {
            throw new PackageIndexException(
                $"ASPKG136 packages/third-party-payloads.yml references package_id values that are not in packages/package-index.yml: {string.Join(", ", unknownPackageIds)}. Problem: stale payload provenance records can survive after a package is renamed or removed. Cause: the inventory package_id does not match any package publish-plan entry. Fix: update the package_id to the current package or remove the stale inventory record. Docs: packages/README.md#redistributed-payloads.");
        }
    }

    private static IReadOnlyList<string> ValidatePackagePatterns(
        string packageId,
        string recordId,
        IEnumerable<string> patterns)
    {
        return patterns
            .Select(pattern => NormalizePackagePattern(packageId, recordId, pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> MatchPackageEntries(
        IReadOnlyList<string> entryPaths,
        IReadOnlyList<string> patterns)
    {
        return entryPaths
            .Where(entryPath => patterns.Any(pattern => PackagePathPatternMatches(pattern, entryPath)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(entryPath => entryPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizePackagePattern(string packageId, string recordId, string pattern)
    {
        var normalized = NormalizePackagePathStrict(pattern, $"payload inventory record '{recordId}' pattern");
        if (normalized.Split('/').Any(segment =>
                segment.Contains("**", StringComparison.Ordinal)
                && !string.Equals(segment, "**", StringComparison.Ordinal)))
        {
            throw new PackageIndexException(
                $"ASPKG128 {packageId}: payload inventory record '{recordId}' has invalid pattern '{pattern}'. Problem: package payload patterns support only '*' inside a segment or '**' as a whole segment. Cause: the pattern contains an unsupported wildcard run. Fix: use segment globs such as tools/**/reportgenerator/** or runtimes/*/native/*. Docs: packages/README.md#redistributed-payloads.");
        }

        return normalized;
    }

    private static bool PackagePathPatternMatches(string pattern, string entryPath)
    {
        var patternSegments = pattern.Split('/');
        var pathSegments = entryPath.Split('/');
        return PackagePathPatternMatches(patternSegments, 0, pathSegments, 0);
    }

    private static bool PackagePathPatternMatches(
        IReadOnlyList<string> patternSegments,
        int patternIndex,
        IReadOnlyList<string> pathSegments,
        int pathIndex)
    {
        if (patternIndex == patternSegments.Count)
        {
            return pathIndex == pathSegments.Count;
        }

        var patternSegment = patternSegments[patternIndex];
        if (string.Equals(patternSegment, "**", StringComparison.Ordinal))
        {
            for (var nextPathIndex = pathIndex; nextPathIndex <= pathSegments.Count; nextPathIndex++)
            {
                if (PackagePathPatternMatches(patternSegments, patternIndex + 1, pathSegments, nextPathIndex))
                {
                    return true;
                }
            }

            return false;
        }

        return pathIndex < pathSegments.Count
            && WildcardSegmentMatches(patternSegment, pathSegments[pathIndex])
            && PackagePathPatternMatches(patternSegments, patternIndex + 1, pathSegments, pathIndex + 1);
    }

    private static bool WildcardSegmentMatches(string pattern, string value)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var retryValueIndex = 0;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex]))
            {
                patternIndex++;
                valueIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryValueIndex = valueIndex;
                continue;
            }

            if (starIndex != -1)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++retryValueIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private static string? GetSuspiciousPayloadRule(string entryPath, IReadOnlySet<string> firstPartyPackageIds)
    {
        if (entryPath.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entryPath, "README.md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entryPath, "THIRD-PARTY-NOTICES.md", StringComparison.OrdinalIgnoreCase)
            || IsFirstPartyAssemblyPath(entryPath, firstPartyPackageIds))
        {
            return null;
        }

        var fileName = Path.GetFileName(entryPath);
        if (entryPath.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)
            && entryPath.Contains("/native/", StringComparison.OrdinalIgnoreCase))
        {
            return "runtimes/*/native/**";
        }

        if (entryPath.StartsWith("tools/", StringComparison.OrdinalIgnoreCase)
            && entryPath.Contains("/reportgenerator/", StringComparison.OrdinalIgnoreCase))
        {
            return "tools/**/reportgenerator/**";
        }

        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return "*.exe";
        }

        if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return "*.dll";
        }

        if (fileName.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
        {
            return "*.min.js";
        }

        return null;
    }

    private static bool IsFirstPartyAssemblyPath(string entryPath, IReadOnlySet<string> firstPartyPackageIds)
    {
        if (!entryPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var assemblyName = Path.GetFileNameWithoutExtension(entryPath);
        return firstPartyPackageIds.Contains(assemblyName)
            || assemblyName.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadPackageTextEntry(
        string packagePath,
        string entryPath,
        string packageId,
        string recordId)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.Entries.SingleOrDefault(candidate =>
            string.Equals(NormalizePackagePath(candidate.FullName), entryPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new PackageIndexException(
                $"ASPKG125 {packageId}: notice '{recordId}' requires missing notice path '{entryPath}'. Problem: a redistributed payload lacks package-visible notice text. Cause: the package artifact does not contain the declared notice path. Fix: pack THIRD-PARTY-NOTICES.md at the package root or update notice_paths. Docs: packages/README.md#redistributed-payloads.");
        }

        if (entry.Length > MaxNoticeBytes)
        {
            throw new PackageIndexException(
                $"ASPKG129 {packageId}: notice '{recordId}' path '{entryPath}' is too large. Problem: verify-packages only reads bounded notice/evidence text. Cause: the notice file is {entry.Length} bytes, above the {MaxNoticeBytes} byte limit. Fix: keep package notice text focused or split non-notice artifacts out of notice_paths. Docs: packages/README.md#redistributed-payloads.");
        }

        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException ex)
        {
            throw new PackageIndexException(
                $"ASPKG130 {packageId}: notice '{recordId}' path '{entryPath}' is not valid UTF-8. Problem: package notices must be readable by humans and CI logs. Cause: UTF-8 decoding failed. Fix: save the notice file as UTF-8 text. Docs: packages/README.md#redistributed-payloads.",
                ex);
        }
    }

    private static void ValidateRepositoryPaths(
        string repositoryRoot,
        string packageId,
        string recordId,
        string fieldName,
        IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            var fullPath = ResolveRepositoryPath(repositoryRoot, packageId, recordId, fieldName, path);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                throw new PackageIndexException(
                    $"ASPKG131 {packageId}: record '{recordId}' references missing {fieldName} path '{path}'. Problem: payload evidence points at a file that no longer exists. Cause: source, generated, or audit evidence moved without updating packages/third-party-payloads.yml. Fix: update the evidence path or restore the referenced file. Docs: packages/README.md#redistributed-payloads.");
            }
        }
    }

    private static void ValidateVersionSource(
        string repositoryRoot,
        string packageId,
        string recordId,
        string? versionSourcePath,
        string? versionSourceContains)
    {
        if (string.IsNullOrWhiteSpace(versionSourcePath) || string.IsNullOrWhiteSpace(versionSourceContains))
        {
            throw new PackageIndexException(
                $"ASPKG132 {packageId}: notice '{recordId}' must define version_source_path and version_source_contains together. Problem: deterministic version evidence is incomplete. Cause: only one version-source field was supplied. Fix: set both fields or remove both for manual evidence. Docs: packages/README.md#redistributed-payloads.");
        }

        var fullPath = ResolveRepositoryPath(repositoryRoot, packageId, recordId, "version_source_path", versionSourcePath);
        if (!File.Exists(fullPath))
        {
            throw new PackageIndexException(
                $"ASPKG133 {packageId}: notice '{recordId}' version source '{versionSourcePath}' does not exist. Problem: deterministic version evidence cannot be checked. Cause: the source file moved or was not committed. Fix: update version_source_path. Docs: packages/README.md#redistributed-payloads.");
        }

        var content = File.ReadAllText(fullPath);
        if (!content.Contains(versionSourceContains, StringComparison.Ordinal))
        {
            throw new PackageIndexException(
                $"ASPKG134 {packageId}: notice '{recordId}' version source '{versionSourcePath}' does not contain '{versionSourceContains}'. Problem: payload version evidence drifted from the repository source of truth. Cause: package metadata changed without updating packages/third-party-payloads.yml or the notice text. Fix: update the inventory and notice to match the source version. Docs: packages/README.md#redistributed-payloads.");
        }
    }

    private static string ResolveRepositoryPath(
        string repositoryRoot,
        string packageId,
        string recordId,
        string fieldName,
        string relativePath)
    {
        if (IsRootedRepositoryPath(relativePath))
        {
            throw new PackageIndexException(
                $"ASPKG135 {packageId}: record '{recordId}' {fieldName} path '{relativePath}' must be repository-relative. Problem: payload evidence must be portable across checkouts. Cause: the path is absolute instead of repository-relative. Fix: use a repository-relative source path. Docs: packages/README.md#redistributed-payloads.");
        }

        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Join(normalizedRoot, normalizedRelativePath));
        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(fullPath, normalizedRoot, StringComparison.Ordinal)
            && !fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new PackageIndexException(
                $"ASPKG135 {packageId}: record '{recordId}' {fieldName} path '{relativePath}' escapes the repository root. Problem: payload evidence must be reviewable in this repository. Cause: the path resolves outside the checkout. Fix: use a repository-relative source path. Docs: packages/README.md#redistributed-payloads.");
        }

        return fullPath;
    }

    private static bool IsRootedRepositoryPath(string path)
    {
        return Path.IsPathRooted(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("\\", StringComparison.Ordinal)
            || (path.Length >= 3
                && char.IsAsciiLetter(path[0])
                && path[1] == ':'
                && (path[2] == '/' || path[2] == '\\'));
    }

    private static string FormatPatterns(IReadOnlyList<string> patterns)
    {
        return string.Join(", ", patterns.Select(pattern => $"'{pattern}'"));
    }

    private static InspectedPackage InspectPackage(string packagePath, IReadOnlySet<string> packageIds)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntries = archive.Entries
            .Where(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (nuspecEntries.Length == 0)
        {
            throw new PackageIndexException($"Package artifact '{packagePath}' does not contain a .nuspec file.");
        }

        if (nuspecEntries.Length > 1)
        {
            throw new PackageIndexException($"Package artifact '{packagePath}' contains multiple .nuspec files.");
        }

        XDocument nuspec;
        try
        {
            using var nuspecStream = nuspecEntries[0].Open();
            nuspec = XDocument.Load(nuspecStream);
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            throw new PackageIndexException(
                $"Package artifact '{packagePath}' contains invalid nuspec XML: {ex.Message}",
                ex);
        }

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
        var duplicateEntryPath = entryPaths
            .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (duplicateEntryPath is not null)
        {
            throw new PackageIndexException(
                $"Package artifact '{packagePath}' contains duplicate package entry path '{duplicateEntryPath}' after normalization. Problem: package payload validation must not depend on ambiguous ZIP entry casing or separators. Cause: the artifact contains entries that normalize to the same path. Fix: remove duplicate or case-colliding package entries. Docs: packages/README.md#redistributed-payloads.");
        }
        var toolSettingsFiles = ReadToolSettingsFiles(archive, packagePath);
        var toolCommandNames = toolSettingsFiles
            .SelectMany(settingsFile => settingsFile.CommandNames)
            .Distinct(StringComparer.Ordinal)
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
            GetElementValue(metadata, ns, "projectUrl"),
            repository?.Attribute("url")?.Value,
            GetElementValue(metadata, ns, "tags"),
            GetElementValue(metadata, ns, "readme"),
            packageTypes,
            dependencies,
            entryPaths,
            firstPartyAssemblyVersions,
            toolCommandNames,
            toolSettingsFiles);
    }

    private static IReadOnlyList<InspectedToolSettingsFile> ReadToolSettingsFiles(ZipArchive archive, string packagePath)
    {
        var settingsEntries = archive.Entries
            .Where(entry => string.Equals(Path.GetFileName(entry.FullName), "DotnetToolSettings.xml", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var settingsFiles = new List<InspectedToolSettingsFile>();
        foreach (var settingsEntry in settingsEntries)
        {
            XDocument settings;
            try
            {
                using var settingsStream = settingsEntry.Open();
                settings = XDocument.Load(settingsStream);
            }
            catch (Exception ex) when (ex is XmlException or IOException)
            {
                throw new PackageIndexException(
                    $"Package artifact '{packagePath}' contains invalid DotnetToolSettings.xml: {ex.Message}",
                    ex);
            }

            var settingsCommands = settings
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "Command", StringComparison.Ordinal))
                .Select(element => element.Attribute("Name")?.Value)
                .ToArray();
            var commandNames = new List<string>();
            foreach (var commandName in settingsCommands)
            {
                if (string.IsNullOrWhiteSpace(commandName))
                {
                    throw new PackageIndexException(
                        $"Package artifact '{packagePath}' contains DotnetToolSettings.xml with a command missing the Name attribute.");
                }

                PackageIndexGenerator.ValidateToolCommandNameValue(packagePath, commandName);
                commandNames.Add(commandName);
            }

            settingsFiles.Add(new InspectedToolSettingsFile(
                NormalizePackagePath(settingsEntry.FullName),
                commandNames.Distinct(StringComparer.Ordinal).ToArray()));
        }

        return settingsFiles;
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

    private static bool IsRequiredPackageProjectUrl(string projectUrl)
    {
        return string.Equals(
            projectUrl.TrimEnd('/'),
            RequiredPackageProjectUrl,
            StringComparison.OrdinalIgnoreCase);
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
        return NormalizePackagePathStrict(entryPath, "package entry");
    }

    private static string NormalizePackagePathStrict(string entryPath, string sourceDescription)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            throw new PackageIndexException(
                $"Invalid {sourceDescription} path. Problem: package payload paths must not be empty. Cause: an empty path was supplied. Fix: use repository-style package paths such as THIRD-PARTY-NOTICES.md or tools/net10.0/any/reportgenerator/**. Docs: packages/README.md#redistributed-payloads.");
        }

        var normalized = entryPath.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                || string.Equals(segment, ".", StringComparison.Ordinal)
                || string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            throw new PackageIndexException(
                $"Invalid {sourceDescription} path '{entryPath}'. Problem: package payload paths must be relative paths without traversal, empty segments, or current-directory segments. Cause: the path is outside the v1 package payload contract. Fix: use clean forward-slash package paths. Docs: packages/README.md#redistributed-payloads.");
        }

        return normalized;
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

    private sealed record SuspiciousPackageEntry(string EntryPath, string? Rule);

    private sealed record PackagePayloadValidationSummary(
        IReadOnlyList<PackagePayloadValidationResult> Results,
        int SuspiciousEntryCount,
        int CoveredSuspiciousEntryCount)
    {
        internal static readonly PackagePayloadValidationSummary Empty = new([], 0, 0);
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
    /// <param name="coverageProofReport">Optional packaged coverage CLI proof details to append.</param>
    /// <returns>Markdown report content.</returns>
    internal static string RenderMarkdown(
        PackageArtifactValidationReport report,
        CoverageCliConsumerProofReport? coverageProofReport = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Package artifact validation");
        builder.AppendLine();
        builder.AppendLine($"Version: `{report.PackageVersion}`");
        builder.AppendLine();
        builder.AppendLine("| Package | Project | Decision | ToolCommand | Expected package dependencies | Suspicious payloads |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var entry in report.Entries)
        {
            var dependencies = entry.ExpectedDependencyPackageIds.Count == 0
                ? "none"
                : string.Join(", ", entry.ExpectedDependencyPackageIds.Select(value => $"`{value}`"));
            var toolCommand = entry.IsTool && !string.IsNullOrWhiteSpace(entry.ToolCommandName)
                ? $"`{entry.ToolCommandName}`"
                : "-";
            var suspiciousPayloads = entry.SuspiciousPayloadCount == 0
                ? "0"
                : $"{entry.CoveredSuspiciousPayloadCount}/{entry.SuspiciousPayloadCount}";
            builder.AppendLine($"| `{entry.PackageId}` | `{entry.ProjectPath}` | `{FormatDecision(entry.Decision)}` | {toolCommand} | {dependencies} | {suspiciousPayloads} |");
        }

        var payloadResults = report.Entries
            .SelectMany(entry => entry.PayloadResults ?? [])
            .ToArray();
        if (payloadResults.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Redistributed payload coverage");
            builder.AppendLine();
            builder.AppendLine("| Package | Record | Component / rule | Evidence | Status | Payload entries | Notices | Version source |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var result in payloadResults.OrderBy(result => result.PackageId, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(result => result.RecordId, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine(
                    $"| `{result.PackageId}` | `{result.RecordId}` | {EscapeMarkdown(result.ComponentOrRule)} | `{result.EvidenceKind}` | `{result.Status}` | {FormatPathList(result.PayloadEntries)} | {FormatPathList(result.NoticePaths)} | {FormatOptionalPath(result.VersionSource)} |");
            }
        }

        if (coverageProofReport is not null)
        {
            builder.AppendLine();
            builder.AppendLine("## Coverage CLI consumer proof");
            builder.AppendLine();
            CoverageCliConsumerProofReportRenderer.RenderSection(builder, coverageProofReport);
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string FormatDecision(PackagePublishDecision decision)
    {
        return decision switch
        {
            PackagePublishDecision.Publish => "publish",
            PackagePublishDecision.SupportPublish => "support_publish",
            PackagePublishDecision.DoNotPublish => "do_not_publish",
            _ => decision.ToString()
        };
    }

    private static string FormatPathList(IReadOnlyList<string> values)
    {
        return values.Count == 0
            ? "-"
            : string.Join("<br />", values.Select(value => $"`{value}`"));
    }

    private static string FormatOptionalPath(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "-"
            : $"`{value}`";
    }

    private static string EscapeMarkdown(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }
}

/// <summary>
/// Package version policies used by release-classified package workflows.
/// </summary>
internal enum PackageVersionPolicy
{
    /// <summary>
    /// Accepts only prerelease SemVer identities without build metadata.
    /// </summary>
    PrereleaseOnly,

    /// <summary>
    /// Accepts only stable SemVer identities without build metadata.
    /// </summary>
    StableOnly,

    /// <summary>
    /// Accepts stable or prerelease SemVer identities without build metadata.
    /// </summary>
    StableOrPrereleaseNoBuildMetadata
}

/// <summary>
/// Ensures package versions used by release workflows are safe for NuGet identity.
/// </summary>
internal static class PackageVersionValidator
{
    /// <summary>
    /// Validates that the package version is a prerelease SemVer identity without build metadata.
    /// </summary>
    /// <param name="packageVersion">Package version to validate.</param>
    internal static void RequirePrerelease(string packageVersion)
    {
        Require(packageVersion, PackageVersionPolicy.PrereleaseOnly);
    }

    /// <summary>
    /// Validates a package version against the requested release classification policy.
    /// </summary>
    /// <param name="packageVersion">Package version to validate.</param>
    /// <param name="policy">Version policy required by the calling workflow.</param>
    internal static void Require(string packageVersion, PackageVersionPolicy policy)
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
        var versionParts = parts[0].Split('.');
        if (versionParts.Length != 3 || versionParts.Any(part => !int.TryParse(part, out _)))
        {
            throw new PackageIndexException($"Package version '{packageVersion}' must use a major.minor.patch SemVer core.");
        }

        var isPrerelease = parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]);
        if (policy == PackageVersionPolicy.PrereleaseOnly && !isPrerelease)
        {
            throw new PackageIndexException($"Package version '{packageVersion}' must be a prerelease version with a SemVer suffix.");
        }

        if (policy == PackageVersionPolicy.StableOnly && isPrerelease)
        {
            throw new PackageIndexException($"Package version '{packageVersion}' must be a stable version without a SemVer prerelease suffix.");
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
/// <param name="ArtifactPath">Validated <c>.nupkg</c> artifact path.</param>
/// <param name="IsTool">Whether the package is a .NET tool package.</param>
/// <param name="ToolCommandName">
/// Validated command shim token from <c>tool_command_name</c>. It is empty for non-tool packages and required for tool
/// packages so artifact reports show the exact command that publish smoke tests execute.
/// </param>
/// <param name="PayloadResults">Redistributed package payload evidence rows validated for this package.</param>
/// <param name="SuspiciousPayloadCount">Number of suspicious package entries found in this artifact.</param>
/// <param name="CoveredSuspiciousPayloadCount">Number of suspicious package entries covered by notice or audit evidence.</param>
internal sealed record PackageArtifactValidationReportEntry(
    string PackageId,
    string ProjectPath,
    PackagePublishDecision Decision,
    IReadOnlyList<string> ExpectedDependencyPackageIds,
    string ArtifactPath = "",
    bool IsTool = false,
    string ToolCommandName = "",
    IReadOnlyList<PackagePayloadValidationResult>? PayloadResults = null,
    int SuspiciousPayloadCount = 0,
    int CoveredSuspiciousPayloadCount = 0);

/// <summary>
/// One redistributed package payload evidence row rendered into the artifact validation report.
/// </summary>
/// <param name="PackageId">Package id whose artifact carried or embedded the payload evidence.</param>
/// <param name="RecordId">Inventory record id from <c>packages/third-party-payloads.yml</c>.</param>
/// <param name="ComponentOrRule">Third-party component name or audit rule shown to release reviewers.</param>
/// <param name="EvidenceKind">Evidence type, such as <c>notice</c> or <c>generated_first_party</c>.</param>
/// <param name="Status">Validation status rendered for release evidence.</param>
/// <param name="PayloadEntries">Package entries covered by this record.</param>
/// <param name="NoticePaths">Package notice paths checked by this record.</param>
/// <param name="VersionSource">Repository path or audit source that anchors the version/evidence.</param>
internal sealed record PackagePayloadValidationResult(
    string PackageId,
    string RecordId,
    string ComponentOrRule,
    string EvidenceKind,
    string Status,
    IReadOnlyList<string> PayloadEntries,
    IReadOnlyList<string> NoticePaths,
    string VersionSource);

/// <summary>
/// Metadata and payload facts inspected from one NuGet package artifact.
/// </summary>
/// <param name="PackagePath">Absolute or caller-supplied path to the inspected <c>.nupkg</c> file.</param>
/// <param name="PackageId">Nuspec package id. Expected to be non-empty after inspection.</param>
/// <param name="PackageVersion">Nuspec package version. Expected to be non-empty after inspection.</param>
/// <param name="Authors">Nuspec authors metadata, or <c>null</c> when absent.</param>
/// <param name="Description">Nuspec description metadata, or <c>null</c> when absent.</param>
/// <param name="License">Nuspec license expression or value, or <c>null</c> when absent.</param>
/// <param name="ProjectUrl">Nuspec project URL, or <c>null</c> when absent.</param>
/// <param name="RepositoryUrl">Nuspec repository URL, or <c>null</c> when absent.</param>
/// <param name="Tags">Nuspec package tags, or <c>null</c> when absent.</param>
/// <param name="Readme">Nuspec README path, or <c>null</c> when absent.</param>
/// <param name="PackageTypes">Declared nuspec package type names such as <c>DotnetTool</c>.</param>
/// <param name="Dependencies">Dependency ids mapped to all distinct nuspec versions observed across dependency groups.</param>
/// <param name="EntryPaths">Normalized archive entry paths contained in the package.</param>
/// <param name="FirstPartyAssemblyVersions">First-party implementation assemblies and their informational versions.</param>
/// <param name="ToolCommandNames">Command names declared by any <c>DotnetToolSettings.xml</c> files.</param>
/// <param name="ToolSettingsFiles">Tool command settings files with their package entry paths and declared commands.</param>
internal sealed record InspectedPackage(
    string PackagePath,
    string PackageId,
    string PackageVersion,
    string? Authors,
    string? Description,
    string? License,
    string? ProjectUrl,
    string? RepositoryUrl,
    string? Tags,
    string? Readme,
    IReadOnlyList<string> PackageTypes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Dependencies,
    IReadOnlyList<string> EntryPaths,
    IReadOnlyList<InspectedAssemblyVersion> FirstPartyAssemblyVersions,
    IReadOnlyList<string> ToolCommandNames,
    IReadOnlyList<InspectedToolSettingsFile> ToolSettingsFiles);

/// <summary>
/// Tool command declarations found in one packaged <c>DotnetToolSettings.xml</c> file.
/// </summary>
/// <param name="EntryPath">Normalized package entry path for the settings file.</param>
/// <param name="CommandNames">Distinct command names declared in the settings file.</param>
internal sealed record InspectedToolSettingsFile(
    string EntryPath,
    IReadOnlyList<string> CommandNames);

/// <summary>
/// Informational version metadata read from a first-party assembly inside a package artifact.
/// </summary>
/// <param name="EntryPath">Archive path for the inspected assembly entry.</param>
/// <param name="InformationalVersion">Assembly informational version value read from metadata.</param>
internal sealed record InspectedAssemblyVersion(
    string EntryPath,
    string InformationalVersion);
