using CliFx;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Creates validated export source requests from CLI options.
/// </summary>
public class ExportSourceRequestFactory
{
    private const string SourceSelectionHint =
        " Choose one source, for example `razorwire export --project ./MyApp.csproj --output ./dist`; run `razorwire export --help` for all options.";

    private const string HelpHint = " Run `razorwire export --help` for usage.";

    /// <summary>
    /// Creates a validated export source request from mutually exclusive CLI source options.
    /// </summary>
    /// <param name="baseUrl">Optional running application base URL.</param>
    /// <param name="projectPath">Optional project path to publish and launch.</param>
    /// <param name="dllPath">Optional compiled DLL path to launch.</param>
    /// <param name="framework">Optional target framework for project exports.</param>
    /// <param name="appArgs">Arguments forwarded to launched project or DLL exports.</param>
    /// <param name="noBuild">Whether project exports should skip publishing before launch.</param>
    /// <returns>A validated source request.</returns>
    /// <exception cref="CommandException">Thrown when source selection or source values are invalid.</exception>
    public ExportSourceRequest Create(
        string? baseUrl,
        string? projectPath,
        string? dllPath,
        string? framework,
        IReadOnlyList<string> appArgs,
        bool noBuild)
    {
        var sources = new[]
        {
            !string.IsNullOrWhiteSpace(baseUrl),
            !string.IsNullOrWhiteSpace(projectPath),
            !string.IsNullOrWhiteSpace(dllPath)
        };

        var selectedCount = sources.Count(selected => selected);
        if (selectedCount == 0)
        {
            throw new CommandException("You must specify exactly one source: --url, --project, or --dll." + SourceSelectionHint);
        }

        if (selectedCount > 1)
        {
            throw new CommandException("Source options are mutually exclusive. Specify only one of --url, --project, or --dll." + SourceSelectionHint);
        }

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new CommandException("--url must be a valid HTTP or HTTPS URL." + HelpHint);
            }

            return new ExportSourceRequest(
                ExportSourceKind.Url,
                uri.ToString().TrimEnd('/'),
                framework,
                appArgs,
                noBuild);
        }

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var fullPath = ValidateFile(projectPath, ".csproj", "--project");
            return new ExportSourceRequest(ExportSourceKind.Project, fullPath, framework, appArgs, noBuild);
        }

        var dllFullPath = ValidateFile(dllPath!, ".dll", "--dll");
        return new ExportSourceRequest(ExportSourceKind.Dll, dllFullPath, framework, appArgs, noBuild);
    }

    private static string ValidateFile(string filePath, string extension, string optionName)
    {
        if (!Path.HasExtension(filePath)
            || !string.Equals(Path.GetExtension(filePath), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandException($"{optionName} must point to a {extension} file." + HelpHint);
        }

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new CommandException($"{optionName} file not found: {fullPath}.{HelpHint}");
        }

        return fullPath;
    }
}
