using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Writes the stable binding between one coverage-run project directory and the project that owns it.
/// </summary>
/// <remarks>
/// <para>
/// The manifest lets package-level proof select a known project report without reconstructing the runner's private
/// slug allocation algorithm. It is emitted before the project's test process starts so a produced raw report is
/// always adjacent to the identity that authorized the directory.
/// </para>
/// <para>
/// Schema version 1 records a normalized solution-relative project path and the slug allocated by
/// <see cref="CoverageRunWorkflow"/>. Consumers must reject unknown schema versions and must not infer a project
/// identity from the directory name alone.
/// </para>
/// </remarks>
internal static class CoverageProjectManifest
{
    /// <summary>
    /// Gets the fixed per-project manifest file name.
    /// </summary>
    internal const string FileName = "coverage-project.json";

    private const int SchemaVersion = 1;
    private const int MaximumLinkResolutions = 40;

    /// <summary>
    /// Writes the project-to-artifact-directory binding as a complete UTF-8 JSON document.
    /// </summary>
    /// <param name="projectOutputDirectory">Existing project artifact directory.</param>
    /// <param name="solutionDirectory">Directory used to resolve and run the selected project.</param>
    /// <param name="project">Selected project with its allocated artifact slug.</param>
    /// <param name="cancellationToken">Cancellation token for the staged write.</param>
    /// <returns>A task that completes after the manifest has been atomically promoted.</returns>
    internal static async Task WriteAsync(
        string projectOutputDirectory,
        string solutionDirectory,
        CoverageRunProject project,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectOutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionDirectory);
        ArgumentNullException.ThrowIfNull(project);

        var projectPath = Path.GetRelativePath(
                ResolveExistingDirectoryPath(solutionDirectory),
                ResolveExistingProjectPath(project.FullPath))
            .Replace('\\', '/');
        var manifestPath = Path.Join(projectOutputDirectory, FileName);
        var stagedPath = Path.Join(projectOutputDirectory, $".coverage-project.{Guid.NewGuid():N}.tmp");
        var contents = JsonSerializer.Serialize(
            new CoverageProjectManifestDocument(SchemaVersion, projectPath, project.Slug),
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }) + "\n";

        try
        {
            await File.WriteAllTextAsync(stagedPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            File.Move(stagedPath, manifestPath, overwrite: true);
        }
        finally
        {
            File.Delete(stagedPath);
        }
    }

    private static string ResolveExistingProjectPath(string projectPath)
    {
        var fullProjectPath = Path.GetFullPath(projectPath);
        var projectDirectory = Path.GetDirectoryName(fullProjectPath)
            ?? throw new IOException($"Project path does not have a directory: {projectPath}");
        return Path.Join(ResolveExistingDirectoryPath(projectDirectory), Path.GetFileName(fullProjectPath));
    }

    private static string ResolveExistingDirectoryPath(string directoryPath)
    {
        var fullDirectoryPath = Path.GetFullPath(directoryPath);
        var root = Path.GetPathRoot(fullDirectoryPath)
            ?? throw new IOException($"Directory path does not have a root: {directoryPath}");
        var current = root;
        var linkResolutions = 0;
        var remainingSegments = new Queue<string>(GetPathSegments(fullDirectoryPath, root));
        while (remainingSegments.TryDequeue(out var segment))
        {
            current = Path.Join(current, segment);
            var directory = new DirectoryInfo(current);
            var linkTarget = directory.LinkTarget;
            if (linkTarget is null)
            {
                if (!directory.Exists)
                {
                    throw new IOException($"Directory does not exist: {current}");
                }

                continue;
            }

            if (++linkResolutions > MaximumLinkResolutions)
            {
                throw new IOException($"Directory path exceeds the {MaximumLinkResolutions}-link resolution limit: {directoryPath}");
            }

            var targetPath = Path.GetFullPath(linkTarget, Path.GetDirectoryName(current)!);
            var targetRoot = Path.GetPathRoot(targetPath)!;
            current = targetRoot;
            remainingSegments = new Queue<string>(GetPathSegments(targetPath, targetRoot).Concat(remainingSegments));
        }

        return current;
    }

    private static IEnumerable<string> GetPathSegments(string path, string root) =>
        path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    private sealed record CoverageProjectManifestDocument(int SchemaVersion, string ProjectPath, string Slug);
}
