using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Produces a deterministic, read-only description of generated package documentation for release-preparation review.
/// </summary>
/// <remarks>
/// The witness is deliberately a statement of repository truth at one merge base and HEAD. It never calls
/// <c>generate</c> and never changes package documentation; the Release tool validates this JSON before accepting a
/// generated package surface in an otherwise narrowly-scoped release preparation pull request.
/// </remarks>
internal sealed class ReleasePreparationWitnessBuilder
{
    internal const string Schema = "forge-trust.appsurface.release-prep-witness/v1";
    internal const string ManifestPath = "packages/package-index.yml";
    internal const string TemplatePath = ReleaseGuidanceRenderer.TemplateRelativePath;

    private readonly PackageIndexGenerator _generator;

    /// <summary>
    /// Creates the witness builder around the canonical package-index renderer.
    /// </summary>
    /// <param name="generator">Canonical renderer used to calculate expected outputs.</param>
    internal ReleasePreparationWitnessBuilder(PackageIndexGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        _generator = generator;
    }

    /// <summary>
    /// Calculates a JSON witness for package documentation that is generated at HEAD.
    /// </summary>
    /// <param name="request">Resolved package-index request rooted at the checked-out repository.</param>
    /// <param name="baseRef">Fetched base ref or immutable base commit used to derive the merge base.</param>
    /// <param name="cancellationToken">Cancellation token for file, renderer, and Git work.</param>
    /// <returns>A strictly ordered witness with identity, semantic source inputs, and expected generated output hashes.</returns>
    internal async Task<ReleasePreparationWitness> CreateAsync(
        PackageIndexRequest request,
        string baseRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        var baseTipCommit = await RequireGitOutputAsync(request.RepositoryRoot, ["rev-parse", "--verify", baseRef], cancellationToken);
        var mergeBases = (await RequireGitOutputAsync(
                request.RepositoryRoot,
                ["merge-base", "--all", baseTipCommit, "HEAD"],
                cancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (mergeBases.Length != 1)
        {
            throw new PackageIndexException(
                $"Release-preparation witness requires exactly one merge base for '{baseRef}'. Problem: package provenance cannot be bound to an ambiguous history. Cause: Git returned {mergeBases.Length} merge-base commits. Fix: update the branch from its base and rerun verify-prep-diff.");
        }

        var headCommit = await RequireGitOutputAsync(request.RepositoryRoot, ["rev-parse", "HEAD"], cancellationToken);
        var changedPaths = (await RequireGitOutputAsync(
                request.RepositoryRoot,
                ["diff", "--name-only", "--no-renames", $"{mergeBases[0]}..{headCommit}", "--", ManifestPath, TemplatePath],
                cancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var documents = await _generator.GenerateDocumentsAsync(request, cancellationToken);
        var entries = await CreateReleaseGuidanceUpdatesAsync(request, cancellationToken);
        var surfaces = new List<ReleasePreparationWitnessSurface>
        {
            new("chooser", "packages/README.md", ComputeSha256(documents.ChooserMarkdown)),
            new("readiness", "packages/readiness.md", ComputeSha256(documents.ReadinessMarkdown))
        };
        surfaces.AddRange(entries.Select(update => new ReleasePreparationWitnessSurface(
            "managed-readme",
            update.DisplayPath,
            ComputeSha256(ReleaseGuidanceRenderer.ExtractManagedRegionBody(update.ExpectedContent, update.DisplayPath)))));

        var sourceInputs = new List<ReleasePreparationWitnessInput>();
        if (changedPaths.Contains(ManifestPath))
        {
            sourceInputs.Add(new ReleasePreparationWitnessInput(
                "package-index-manifest",
                ManifestPath,
                surfaces.Select(surface => surface.Path).Order(StringComparer.Ordinal).ToArray()));
        }

        if (changedPaths.Contains(TemplatePath))
        {
            sourceInputs.Add(new ReleasePreparationWitnessInput(
                "release-guidance-template",
                TemplatePath,
                surfaces
                    .Where(surface => string.Equals(surface.Kind, "managed-readme", StringComparison.Ordinal))
                    .Select(surface => surface.Path)
                    .Order(StringComparer.Ordinal)
                    .ToArray()));
        }

        return new ReleasePreparationWitness(
            Schema,
            baseRef,
            baseTipCommit,
            mergeBases[0],
            headCommit,
            "verified",
            sourceInputs.OrderBy(input => input.Path, StringComparer.Ordinal).ToArray(),
            surfaces.OrderBy(surface => surface.Path, StringComparer.Ordinal).ThenBy(surface => surface.Kind, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Writes a witness as UTF-8 JSON without modifying any repository-owned package surface.
    /// </summary>
    /// <param name="witness">Witness to serialize.</param>
    /// <param name="path">Explicit destination, normally a CI temporary path.</param>
    /// <param name="cancellationToken">Cancellation token for the write.</param>
    internal static async Task WriteAsync(ReleasePreparationWitness witness, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(witness);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new PackageIndexException("Release-preparation witness path must have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(witness, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(path, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    private async Task<IReadOnlyList<ReleaseGuidanceUpdate>> CreateReleaseGuidanceUpdatesAsync(
        PackageIndexRequest request,
        CancellationToken cancellationToken)
    {
        // Re-rendering managed README regions must use the generator's renderer. Calling VerifyAsync would reject a
        // deliberate generated diff, so resolve the same validated generation entries without performing writes.
        var renderer = new ReleaseGuidanceRenderer();
        var resolvedEntries = await _generator.ResolveGenerationEntriesAsync(request, cancellationToken);
        return await renderer.CreateUpdatesAsync(request.RepositoryRoot, resolvedEntries, cancellationToken);
    }

    private static async Task<string> RequireGitOutputAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new PackageIndexException("Release-preparation witness could not start Git.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new PackageIndexException(
                $"Release-preparation witness Git command failed. Problem: repository identity and changed inputs must be inspected before package provenance can be trusted. Cause: git {string.Join(' ', arguments)} exited {process.ExitCode}: {error.Trim()}. Fix: fetch the base ref, repair the local checkout, and rerun verify-prep-diff.");
        }

        return output.Trim();
    }

    private static string ComputeSha256(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Immutable JSON contract emitted by <see cref="ReleasePreparationWitnessBuilder"/>.
/// </summary>
internal sealed record ReleasePreparationWitness(
    string Schema,
    string BaseRef,
    string BaseTipCommit,
    string MergeBaseCommit,
    string HeadCommit,
    string Verification,
    IReadOnlyList<ReleasePreparationWitnessInput> ChangedInputs,
    IReadOnlyList<ReleasePreparationWitnessSurface> Surfaces);

/// <summary>
/// One changed semantic source that authorizes specific generated package surfaces.
/// </summary>
internal sealed record ReleasePreparationWitnessInput(string Kind, string Path, IReadOnlyList<string> Surfaces);

/// <summary>
/// One generated package surface and the SHA-256 digest the canonical renderer expects at HEAD.
/// </summary>
internal sealed record ReleasePreparationWitnessSurface(string Kind, string Path, string Sha256);
