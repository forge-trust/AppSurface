namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Identifies the kind of application source the exporter should crawl.
/// </summary>
public enum ExportSourceKind
{
    /// <summary>
    /// Crawl an already-running application URL.
    /// </summary>
    Url,

    /// <summary>
    /// Publish a project and crawl the launched output.
    /// </summary>
    Project,

    /// <summary>
    /// Launch a compiled application assembly and crawl it.
    /// </summary>
    Dll
}

/// <summary>
/// Describes a validated export source and the launch options needed to make it crawlable.
/// </summary>
/// <param name="SourceKind">The selected source kind.</param>
/// <param name="SourceValue">The validated URL, project path, or DLL path.</param>
/// <param name="Framework">Optional target framework for project exports.</param>
/// <param name="AppArgs">Application arguments forwarded when the exporter launches a target app.</param>
/// <param name="NoBuild">Whether project exports skip publish before launch.</param>
public sealed record ExportSourceRequest(
    ExportSourceKind SourceKind,
    string SourceValue,
    string? Framework,
    IReadOnlyList<string> AppArgs,
    bool NoBuild);
