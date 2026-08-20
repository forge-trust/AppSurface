using System.Diagnostics.CodeAnalysis;
using System.Text;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Composes independently authored release-note entries into a Markdown document.
/// </summary>
/// <remarks>
/// Entries live in a flat, filename-sorted directory, so feature branches do not contend for the same changelog or
/// living-note lines. The command validates every entry and template marker, rebases relative Markdown links for the
/// selected output, previews by default, and writes only when both <c>--output</c> and <c>--apply</c> are supplied.
/// It composes a note; release-specific versioning, changelog rollover, entry consumption, and publication remain the
/// responsibility of the caller's release workflow.
/// </remarks>
[Command("release compose", Description = "Preview or write a deterministic release note from isolated append-only entries.")]
internal sealed partial class ReleaseComposeCommand(Func<string>? getCurrentDirectory = null) : ICommand
{
    private const string DefaultEntriesDirectory = "releases/unreleased.entries";
    private const string DefaultTemplatePath = "releases/unreleased.md";
    private const string DocumentationPath = "Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-release-compose";
    private readonly Func<string> _getCurrentDirectory = getCurrentDirectory ?? Directory.GetCurrentDirectory;

    /// <summary>
    /// Gets or sets the project root that bounds the template, entries, and optional output paths.
    /// </summary>
    [CommandOption("root", Description = "Existing project root. Defaults to the current directory; all selected paths must stay below it.")]
    public string? RootDirectory { get; set; }

    /// <summary>
    /// Gets or sets the flat directory containing append-only entry files.
    /// </summary>
    [CommandOption("entries", Description = "Entry directory relative to --root. Defaults to releases/unreleased.entries.")]
    public string? EntriesDirectory { get; set; }

    /// <summary>
    /// Gets or sets the Markdown template that declares the entry sections.
    /// </summary>
    [CommandOption("template", Description = "Living-note template relative to --root. Defaults to releases/unreleased.md.")]
    public string? TemplatePath { get; set; }

    /// <summary>
    /// Gets or sets the destination for a composed document.
    /// </summary>
    /// <remarks>
    /// The destination must differ from the template so that the template keeps its composition markers for the next
    /// release cycle. Supplying this option without <c>--apply</c> previews the exact write without changing files.
    /// </remarks>
    [CommandOption("output", Description = "Composed Markdown path relative to --root. Required with --apply and must differ from --template.")]
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the composed document may be written.
    /// </summary>
    [CommandOption("apply", Description = "Write --output. Omit to validate and preview without changing files.")]
    public bool Apply { get; set; }

    /// <inheritdoc />
    [ExcludeFromCodeCoverage(Justification = "The cancellation-registration adapter delegates to the token-aware overload covered by tests.")]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await ExecuteAsync(console, console.RegisterCancellationHandler());
    }

    /// <summary>
    /// Executes composition with an explicit cancellation token.
    /// </summary>
    /// <param name="console">Console receiving the validation summary and composed Markdown.</param>
    /// <param name="cancellationToken">Token observed while reading entry files and writing output.</param>
    /// <returns>A task that completes after the preview or write result is reported.</returns>
    internal async ValueTask ExecuteAsync(IConsole console, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        if (Apply && string.IsNullOrWhiteSpace(OutputPath))
        {
            throw InvalidUsage("--apply requires --output so the stable template is never overwritten.");
        }

        try
        {
            var rootDirectory = ResolveRootDirectory();
            var templatePath = ResolvePath(rootDirectory, TemplatePath ?? DefaultTemplatePath, "--template");
            var entriesDirectory = ResolvePath(rootDirectory, EntriesDirectory ?? DefaultEntriesDirectory, "--entries");
            var outputPath = string.IsNullOrWhiteSpace(OutputPath)
                ? null
                : ResolvePath(rootDirectory, OutputPath, "--output");
            EnsureExistingPathComponentsArePhysical(rootDirectory, templatePath, "--template");
            EnsureExistingPathComponentsArePhysical(rootDirectory, entriesDirectory, "--entries");
            if (outputPath is not null)
            {
                EnsureExistingPathComponentsArePhysical(rootDirectory, outputPath, "--output");
            }

            if (!File.Exists(templatePath))
            {
                throw InvalidUsage($"The release-note template '{DisplayPath(rootDirectory, templatePath)}' does not exist.");
            }

            if (outputPath is not null && PathsEqual(outputPath, templatePath))
            {
                throw InvalidUsage("--output must differ from --template so the stable template retains its composition markers.");
            }

            var template = await File.ReadAllTextAsync(templatePath, cancellationToken);
            var entries = await UnreleasedEntryComposer.LoadAsync(entriesDirectory, cancellationToken);
            if (outputPath is not null && entries.Paths.Any(path => PathsEqual(path, outputPath)))
            {
                throw InvalidUsage("--output must not overwrite an append-only entry source.");
            }

            var destinationPath = outputPath ?? templatePath;
            var composed = UnreleasedEntryComposer.Compose(template, entries.Entries, destinationPath);
            await console.Output.WriteLineAsync(
                $"Validated {entries.Entries.Count} append-only release-note {Pluralize("entry", entries.Entries.Count)} from {DisplayPath(rootDirectory, entriesDirectory)}.");

            if (!Apply)
            {
                if (outputPath is null)
                {
                    await console.Output.WriteLineAsync("Preview follows. Pass --output <path> --apply to write a composed document without changing the template.");
                    await console.Output.WriteLineAsync();
                    await console.Output.WriteLineAsync(composed);
                }
                else
                {
                    await console.Output.WriteLineAsync($"Preview only. Would write {DisplayPath(rootDirectory, outputPath)}; re-run with --apply to make that change.");
                }

                return;
            }

            var outputDirectory = Path.GetDirectoryName(outputPath!);
            if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
            {
                throw InvalidUsage($"The output directory for '{DisplayPath(rootDirectory, outputPath!)}' does not exist.");
            }

            // Recheck immediately before the write so a pre-existing linked ancestor cannot redirect the output after validation.
            EnsureExistingPathComponentsArePhysical(rootDirectory, outputPath!, "--output");
            await File.WriteAllTextAsync(outputPath!, composed, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            await console.Output.WriteLineAsync($"Wrote composed release note to {DisplayPath(rootDirectory, outputPath!)}.");
        }
        catch (UnreleasedEntryException exception)
        {
            throw InvalidUsage(exception.Message);
        }
        catch (IOException exception)
        {
            throw InvalidUsage($"Could not read or write the selected release-note files: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw InvalidUsage($"Could not access the selected release-note files: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            throw InvalidUsage($"One of the selected release-note paths is invalid: {exception.Message}");
        }
    }

    private string ResolveRootDirectory()
    {
        var value = string.IsNullOrWhiteSpace(RootDirectory) ? _getCurrentDirectory() : RootDirectory;
        var rootDirectory = Path.GetFullPath(value);
        if (!Directory.Exists(rootDirectory))
        {
            throw InvalidUsage($"The project root '{rootDirectory}' does not exist or is not a directory.");
        }

        if ((File.GetAttributes(rootDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidUsage("--root must not be a symbolic link, junction, or other reparse point. Select the physical project directory instead.");
        }

        return rootDirectory;
    }

    private static string ResolvePath(string rootDirectory, string candidate, string optionName)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw InvalidUsage($"{optionName} must name a path below --root.");
        }

        var path = Path.GetFullPath(Path.IsPathFullyQualified(candidate) ? candidate : Path.Join(rootDirectory, candidate));
        var relativePath = Path.GetRelativePath(rootDirectory, path);
        if (relativePath == ".."
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relativePath))
        {
            throw InvalidUsage($"{optionName} must stay below --root.");
        }

        return path;
    }

    private static void EnsureExistingPathComponentsArePhysical(string rootDirectory, string path, string optionName)
    {
        var candidate = rootDirectory;
        var relativePath = Path.GetRelativePath(rootDirectory, path);
        foreach (var component in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            candidate = Path.Join(candidate, component);
            if (!TryGetAttributes(candidate, out var attributes))
            {
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw InvalidUsage($"{optionName} must not traverse a symbolic link, junction, or other reparse point below --root.");
            }
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static string DisplayPath(string rootDirectory, string path)
    {
        var relativePath = Path.GetRelativePath(rootDirectory, path);
        return relativePath == "." ? "." : relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string Pluralize(string singular, int count) => count == 1 ? singular : "entries";

    private static CommandException InvalidUsage(string message) =>
        new($"{message}{Environment.NewLine}Docs: {DocumentationPath}");
}
