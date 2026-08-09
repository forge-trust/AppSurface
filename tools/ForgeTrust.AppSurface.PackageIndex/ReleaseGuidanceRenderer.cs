using System.Text;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Renders the finite package README release-guidance variants declared by the package manifest.
/// </summary>
/// <remarks>
/// The renderer owns only the explicit marker region in a package README. It deliberately does not normalize or
/// infer package-specific documentation outside that region, so operational and adoption guidance stays authored by
/// the package owner. See <c>tools/ForgeTrust.AppSurface.PackageIndex/README.md#release-guidance</c> for the
/// maintainer workflow and recovery commands.
/// </remarks>
internal sealed class ReleaseGuidanceRenderer
{
    internal const string TemplateRelativePath = "tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template";
    internal const string MaintainerGuideRelativePath = "tools/ForgeTrust.AppSurface.PackageIndex/README.md";
    internal const string BeginMarker = "<!-- appsurface-release-guidance: begin -->";
    internal const string EndMarker = "<!-- appsurface-release-guidance: end -->";
    internal const string PackageChooserUrl = "https://github.com/forge-trust/AppSurface/blob/main/packages/README.md";
    internal const string ReleaseHubUrl = "https://github.com/forge-trust/AppSurface/blob/main/releases/README.md";

    private const string PackageChooserToken = "{{PackageChooserUrl}}";
    private const string ReleaseHubToken = "{{ReleaseHubUrl}}";
    private const string DocumentationReference = MaintainerGuideRelativePath + "#release-guidance";

    private static readonly string[] VariantNames = ["default", "apphost", "experimental"];
    private static readonly Regex ReleaseGuidanceHeading = new(
        "^## Release Guidance\\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex TemplateToken = new(
        "\\{\\{[^{}]+}}",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Builds the complete, validated set of package README replacements without writing any files.
    /// </summary>
    /// <param name="repositoryRoot">Repository root that contains the manifest-selected package README files.</param>
    /// <param name="entries">Resolved manifest entries whose documentation targets have already passed path validation.</param>
    /// <param name="cancellationToken">Cancellation token used while reading the template and README files.</param>
    /// <returns>All managed README updates, including no-op updates used by verification.</returns>
    /// <exception cref="PackageIndexException">Thrown when the finite variant, template, marker, or README contract is invalid.</exception>
    internal async Task<IReadOnlyList<ReleaseGuidanceUpdate>> CreateUpdatesAsync(
        string repositoryRoot,
        IReadOnlyList<ResolvedPackageEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(entries);

        var managedReadmes = new List<ManagedReadme>();

        foreach (var entry in entries.OrderBy(entry => entry.Manifest.StartHerePath, StringComparer.Ordinal))
        {
            var documentationPath = entry.Manifest.StartHerePath;
            if (string.IsNullOrWhiteSpace(documentationPath))
            {
                continue;
            }

            var readmePath = ResolveRepositoryReadmePath(repositoryRoot, entry.Manifest.Project, documentationPath);
            var current = await File.ReadAllTextAsync(readmePath, cancellationToken);
            var hasMarkers = current.Contains(BeginMarker, StringComparison.Ordinal)
                || current.Contains(EndMarker, StringComparison.Ordinal);
            var headingCount = ReleaseGuidanceHeading.Matches(current).Count;
            var requiresManagedGuidance = hasMarkers || headingCount > 0;

            if (!requiresManagedGuidance)
            {
                if (!string.IsNullOrWhiteSpace(entry.Manifest.ReleaseGuidanceVariant))
                {
                    throw new PackageIndexException(
                        $"Manifest entry '{entry.Manifest.Project}' defines 'release_guidance_variant', but README '{documentationPath}' has no '{BeginMarker}' region or '## Release Guidance' heading. Problem: the manifest declares generated policy without a managed target. Cause: the README was moved or the field was added to the wrong package. Fix: add the marked release-guidance region or remove the field. Docs: {DocumentationReference}.");
                }

                continue;
            }

            managedReadmes.Add(new ManagedReadme(entry.Manifest, readmePath, documentationPath, current));
        }

        if (managedReadmes.Count == 0)
        {
            return [];
        }

        var templates = await LoadTemplatesAsync(repositoryRoot, cancellationToken);
        var updates = new List<ReleaseGuidanceUpdate>(managedReadmes.Count);
        foreach (var managedReadme in managedReadmes)
        {
            var variant = RequireVariant(managedReadme.Manifest, managedReadme.DisplayPath);
            var expected = ReplaceManagedRegion(managedReadme.CurrentContent, managedReadme.DisplayPath, templates[variant]);
            updates.Add(new ReleaseGuidanceUpdate(
                Path.GetFullPath(repositoryRoot),
                managedReadme.FullPath,
                managedReadme.DisplayPath,
                managedReadme.CurrentContent,
                expected,
                variant,
                TargetExisted: true));
        }

        return updates;
    }

    /// <summary>
    /// Replaces validated generated documents and managed README regions using sibling staging files and ordinary-failure rollback.
    /// </summary>
    /// <param name="updates">Fully rendered generated-file and README updates created before the first target write.</param>
    /// <param name="cancellationToken">Cancellation token used while staging replacement content.</param>
    /// <returns>The number of target files changed.</returns>
    /// <remarks>
    /// Staging makes malformed input fail before target replacement begins. A rollback journal restores targets if a
    /// normal replacement fails. Before each replacement, the target is re-read and must match the initial snapshot.
    /// A process or machine crash cannot be made globally atomic; <c>verify</c> detects resulting drift and reports the
    /// repository command that repairs it.
    /// </remarks>
    internal async Task<int> ApplyUpdatesAsync(
        IReadOnlyList<ReleaseGuidanceUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var changes = updates
            .Where(update => !string.Equals(update.CurrentContent, update.ExpectedContent, StringComparison.Ordinal))
            .ToArray();
        if (changes.Length == 0)
        {
            return 0;
        }

        var staged = new List<StagedReleaseGuidanceUpdate>(changes.Length);
        var applied = new List<StagedReleaseGuidanceUpdate>(changes.Length);
        try
        {
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(change.FullPath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw new PackageIndexException(
                        $"Target '{change.DisplayPath}' has no parent directory. Problem: package-index generation cannot stage a same-directory replacement. Cause: the output or manifest target is malformed. Fix: repair the target path and run 'generate'. Docs: {DocumentationReference}.");
                }

                Directory.CreateDirectory(directory);
                ValidateWriteTarget(change);
                var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(change.FullPath)}.{Guid.NewGuid():N}.release-guidance.tmp");
                var backupPath = Path.Combine(directory, $".{Path.GetFileName(change.FullPath)}.{Guid.NewGuid():N}.release-guidance.bak");
                await File.WriteAllTextAsync(temporaryPath, change.ExpectedContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
                staged.Add(new StagedReleaseGuidanceUpdate(change, temporaryPath, backupPath));
            }

            foreach (var replacement in staged)
            {
                await VerifyTargetMatchesSnapshotAsync(replacement.Update, cancellationToken);
                if (replacement.Update.TargetExisted)
                {
                    File.Copy(replacement.Update.FullPath, replacement.BackupPath, overwrite: false);
                }

                File.Move(replacement.TemporaryPath, replacement.Update.FullPath, overwrite: true);
                applied.Add(replacement);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or PackageIndexException)
        {
            var rollbackFailures = new List<string>();
            foreach (var replacement in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (replacement.Update.TargetExisted)
                    {
                        File.Copy(replacement.BackupPath, replacement.Update.FullPath, overwrite: true);
                    }
                    else if (File.Exists(replacement.Update.FullPath))
                    {
                        File.Delete(replacement.Update.FullPath);
                    }
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    rollbackFailures.Add(replacement.Update.DisplayPath);
                }
            }

            var recovery = rollbackFailures.Count == 0
                ? "Rollback restored every already-replaced target."
                : $"Rollback could not restore: {string.Join(", ", rollbackFailures)}. Run 'dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- generate', inspect the diff, then run 'verify'.";
            throw new PackageIndexException(
                $"Package-index generation reconciliation failed. Problem: no partial generated-document or README update may be left after a normal write failure. Cause: {ex.Message}. Fix: {recovery} Docs: {DocumentationReference}.",
                ex);
        }
        finally
        {
            foreach (var replacement in staged)
            {
                DeleteIfPresent(replacement.TemporaryPath);
                DeleteIfPresent(replacement.BackupPath);
            }
        }

        return changes.Length;
    }

    /// <summary>
    /// Verifies every managed README is already identical to the current rendered release-guidance variant.
    /// </summary>
    /// <param name="updates">Fully rendered README updates created without writing files.</param>
    /// <exception cref="PackageIndexException">Thrown when any managed README is stale.</exception>
    internal static void VerifyUpdates(IReadOnlyList<ReleaseGuidanceUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var stalePaths = updates
            .Where(update => !string.Equals(update.CurrentContent, update.ExpectedContent, StringComparison.Ordinal))
            .Select(update => update.DisplayPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (stalePaths.Length > 0)
        {
            throw new PackageIndexException(
                $"Managed package README release guidance is stale: {string.Join(", ", stalePaths)}. Problem: checked-in package README policy does not match packages/package-index.yml and {TemplateRelativePath}. Cause: run output was not committed or a managed marker region was edited manually. Fix: run 'dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- generate', inspect the README diff, commit it, then run 'verify'. Docs: {DocumentationReference}.");
        }
    }

    private static string RequireVariant(PackageManifestEntry entry, string documentationPath)
    {
        if (string.IsNullOrWhiteSpace(entry.ReleaseGuidanceVariant))
        {
            throw new PackageIndexException(
                $"Manifest entry '{entry.Project}' manages README '{documentationPath}' but does not define 'release_guidance_variant'. Problem: generated release policy needs an explicit finite variant. Cause: the package was added before its reader-facing release posture was classified. Fix: choose one of {string.Join(", ", VariantNames)} in packages/package-index.yml and run 'generate'. Docs: {DocumentationReference}.");
        }

        if (!VariantNames.Contains(entry.ReleaseGuidanceVariant, StringComparer.Ordinal))
        {
            throw new PackageIndexException(
                $"Manifest entry '{entry.Project}' has release_guidance_variant '{entry.ReleaseGuidanceVariant}'. Problem: the renderer supports only finite release-policy variants. Cause: package-specific prose was encoded as a variant. Fix: use one of {string.Join(", ", VariantNames)} and keep package-specific content outside the marker region. Docs: {DocumentationReference}.");
        }

        return entry.ReleaseGuidanceVariant;
    }

    private static string ResolveRepositoryReadmePath(string repositoryRoot, string projectPath, string documentationPath)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, documentationPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSafeRepositoryReadmePath(root, candidate))
        {
            throw new PackageIndexException(
                $"Manifest entry '{projectPath}' release-guidance README '{documentationPath}' is unavailable. Problem: the renderer can only modify a tracked non-symlink README beneath this repository. Cause: start_here_path moved, escaped the repository, crossed a symbolic link, or no longer exists. Fix: repair start_here_path and run 'generate'. Docs: {DocumentationReference}.");
        }

        return candidate;
    }

    private static void ValidateWriteTarget(ReleaseGuidanceUpdate update)
    {
        if (update.RepositoryRoot is not null
            && !IsSafeRepositoryReadmePath(update.RepositoryRoot, update.FullPath))
        {
            throw new PackageIndexException(
                $"README '{update.DisplayPath}' is no longer a writable tracked non-symlink path beneath the repository. Problem: release guidance must not follow a path outside the checkout. Cause: the target moved, was removed, or crossed a symbolic link after rendering. Fix: restore a regular README path and rerun 'generate'. Docs: {DocumentationReference}.");
        }
    }

    private static async Task VerifyTargetMatchesSnapshotAsync(
        ReleaseGuidanceUpdate update,
        CancellationToken cancellationToken)
    {
        ValidateWriteTarget(update);
        if (update.TargetExisted)
        {
            if (!File.Exists(update.FullPath))
            {
                throw new PackageIndexException(
                    $"Target '{update.DisplayPath}' disappeared after staging. Problem: generated content must not overwrite a missing or replaced file. Cause: another process modified the target during generation. Fix: restore the target and rerun 'generate'. Docs: {DocumentationReference}.");
            }

            var currentContent = await File.ReadAllTextAsync(update.FullPath, cancellationToken);
            if (!string.Equals(currentContent, update.CurrentContent, StringComparison.Ordinal))
            {
                throw new PackageIndexException(
                    $"Target '{update.DisplayPath}' changed after generation started. Problem: generated content must not overwrite a concurrent edit. Cause: another process modified the target during generation. Fix: keep the concurrent edit, rerun 'generate', and inspect the resulting diff. Docs: {DocumentationReference}.");
            }

            return;
        }

        if (File.Exists(update.FullPath))
        {
            throw new PackageIndexException(
                $"Target '{update.DisplayPath}' appeared after generation started. Problem: generated content must not overwrite a concurrent new file. Cause: another process created the output during generation. Fix: inspect the new file and rerun 'generate'. Docs: {DocumentationReference}.");
        }
    }

    private static bool IsSafeRepositoryReadmePath(string repositoryRoot, string candidate)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, PackageIndexGenerator.RepositoryPathComparison)
            || !File.Exists(candidate))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(root, candidate).Replace('\\', '/');
        return !ContainsReparsePointSegment(root, relativePath);
    }

    private static bool ContainsReparsePointSegment(string repositoryRoot, string relativePath)
    {
        var currentPath = repositoryRoot;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            try
            {
                if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadTemplatesAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var templatePath = Path.Combine(repositoryRoot, TemplateRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(templatePath))
        {
            throw new PackageIndexException(
                $"Release-guidance template '{TemplateRelativePath}' does not exist. Problem: generated package README policy has no canonical source. Cause: the template was moved or not committed. Fix: restore the template and run 'generate'. Docs: {DocumentationReference}.");
        }

        var source = await File.ReadAllTextAsync(templatePath, cancellationToken);
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variant in VariantNames)
        {
            templates.Add(variant, ExtractTemplate(source, variant));
        }

        return templates;
    }

    private static string ExtractTemplate(string source, string variant)
    {
        var beginMarker = $"<!-- appsurface-release-guidance-template: {variant} begin -->";
        var endMarker = $"<!-- appsurface-release-guidance-template: {variant} end -->";
        var begin = FindExactMarkerLines(source, beginMarker, TemplateRelativePath);
        var end = FindExactMarkerLines(source, endMarker, TemplateRelativePath);
        if (begin.Count != 1 || end.Count != 1 || begin[0].LineStart >= end[0].LineStart)
        {
            throw new PackageIndexException(
                $"Release-guidance template variant '{variant}' is malformed. Problem: every finite variant requires one ordered begin/end marker pair. Cause: a template marker is missing, duplicated, or reversed. Fix: repair {TemplateRelativePath} and run 'verify'. Docs: {DocumentationReference}.");
        }

        var content = source[begin[0].NextLineStart..end[0].LineStart].Trim('\r', '\n');
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new PackageIndexException(
                $"Release-guidance template variant '{variant}' is empty. Problem: package consumers would receive no release policy. Cause: the template contains only markers. Fix: restore the variant body in {TemplateRelativePath}. Docs: {DocumentationReference}.");
        }

        ValidateTemplateTokens(variant, content);
        return content;
    }

    private static void ValidateTemplateTokens(string variant, string content)
    {
        var tokens = TemplateToken.Matches(content)
            .Select(match => match.Value)
            .ToArray();
        if (tokens.Any(token => token is not PackageChooserToken and not ReleaseHubToken)
            || tokens.Count(token => string.Equals(token, PackageChooserToken, StringComparison.Ordinal)) != 1
            || tokens.Count(token => string.Equals(token, ReleaseHubToken, StringComparison.Ordinal)) != 1)
        {
            throw new PackageIndexException(
                $"Release-guidance template variant '{variant}' has invalid URL tokens. Problem: each variant must expand exactly one package chooser and one release hub URL. Cause: a token is missing, repeated, or unknown. Fix: use only {PackageChooserToken} and {ReleaseHubToken} once each in {TemplateRelativePath}. Docs: {DocumentationReference}.");
        }
    }

    private static string ReplaceManagedRegion(string current, string documentationPath, string template)
    {
        var begin = FindExactMarkerLines(current, BeginMarker, documentationPath);
        var end = FindExactMarkerLines(current, EndMarker, documentationPath);
        var lineEnding = current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var rendered = template
            .Replace(PackageChooserToken, PackageChooserUrl, StringComparison.Ordinal)
            .Replace(ReleaseHubToken, ReleaseHubUrl, StringComparison.Ordinal)
            .Replace("\n", lineEnding, StringComparison.Ordinal);

        if (begin.Count != 0 || end.Count != 0)
        {
            if (begin.Count != 1 || end.Count != 1 || begin[0].LineStart >= end[0].LineStart)
            {
                throw new PackageIndexException(
                    $"README '{documentationPath}' has malformed release-guidance markers. Problem: the renderer can replace only one ordered managed region. Cause: the begin/end marker is missing, duplicated, or reversed. Fix: keep exactly one '{BeginMarker}' and '{EndMarker}' pair around the generated policy, then run 'generate'. Docs: {DocumentationReference}.");
            }

            return string.Concat(
                current[..begin[0].NextLineStart],
                rendered,
                lineEnding,
                current[end[0].LineStart..]);
        }

        var headings = ReleaseGuidanceHeading.Matches(current);
        if (headings.Count != 1)
        {
            throw new PackageIndexException(
                $"README '{documentationPath}' has {headings.Count} '## Release Guidance' headings. Problem: the one-time migration needs one unambiguous legacy section. Cause: duplicated or removed heading text prevents a safe marker insertion. Fix: keep one heading or add one valid marker pair, then run 'generate'. Docs: {DocumentationReference}.");
        }

        var heading = headings[0];
        var sectionEnd = FindLegacySectionEnd(current, heading.Index + heading.Length);
        return string.Concat(
            current[..heading.Index],
            BeginMarker,
            lineEnding,
            rendered,
            lineEnding,
            EndMarker,
            lineEnding,
            current[sectionEnd..]);
    }

    private static int FindLegacySectionEnd(string content, int startIndex)
    {
        var firstLineBreak = content.IndexOf('\n', startIndex);
        if (firstLineBreak < 0)
        {
            return content.Length;
        }

        char? activeFence = null;
        var lineStart = firstLineBreak + 1;
        while (lineStart < content.Length)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            var effectiveLineEnd = lineEnd < 0 ? content.Length : lineEnd;
            var line = content[lineStart..effectiveLineEnd].TrimEnd('\r');
            var trimmedStart = line.TrimStart();
            if (activeFence is { } fence)
            {
                if (IsFenceLine(trimmedStart, fence))
                {
                    activeFence = null;
                }
            }
            else if (TryGetFenceMarker(trimmedStart, out var openingFence))
            {
                activeFence = openingFence;
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal)
                || string.Equals(line.Trim(), "---", StringComparison.Ordinal)
                || string.Equals(line.Trim(), "***", StringComparison.Ordinal)
                || string.Equals(line.Trim(), "___", StringComparison.Ordinal))
            {
                return lineStart;
            }

            if (lineEnd < 0)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        return content.Length;
    }

    private static bool TryGetFenceMarker(string line, out char fence)
    {
        fence = default;
        if (line.Length < 3 || (line[0] is not '`' and not '~'))
        {
            return false;
        }

        fence = line[0];
        return IsFenceLine(line, fence);
    }

    private static bool IsFenceLine(string line, char fence)
    {
        return line.Length >= 3
            && line[0] == fence
            && line[1] == fence
            && line[2] == fence;
    }

    private static IReadOnlyList<MarkerLine> FindExactMarkerLines(string content, string marker, string displayPath)
    {
        var matches = new List<MarkerLine>();
        var lineStart = 0;
        while (lineStart <= content.Length)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            var nextLineStart = lineEnd < 0 ? content.Length : lineEnd + 1;
            var lineLength = (lineEnd < 0 ? content.Length : lineEnd) - lineStart;
            if (lineLength > 0 && content[lineStart + lineLength - 1] == '\r')
            {
                lineLength--;
            }

            if (string.Equals(content.Substring(lineStart, lineLength), marker, StringComparison.Ordinal))
            {
                matches.Add(new MarkerLine(lineStart, nextLineStart));
            }

            if (lineEnd < 0)
            {
                break;
            }

            lineStart = nextLineStart;
        }

        return matches;
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A failed cleanup leaves only an ignored same-directory recovery artifact; never hide the primary result.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup leaves only an ignored same-directory recovery artifact; never hide the primary result.
        }
    }

    private sealed record MarkerLine(int LineStart, int NextLineStart);

    private sealed record ManagedReadme(
        PackageManifestEntry Manifest,
        string FullPath,
        string DisplayPath,
        string CurrentContent);

    private sealed record StagedReleaseGuidanceUpdate(
        ReleaseGuidanceUpdate Update,
        string TemporaryPath,
        string BackupPath);
}

/// <summary>
/// Describes one rendered package README policy region without changing the checked-in file.
/// </summary>
internal sealed record ReleaseGuidanceUpdate(
    string? RepositoryRoot,
    string FullPath,
    string DisplayPath,
    string CurrentContent,
    string ExpectedContent,
    string Variant,
    bool TargetExisted = true);
