namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Identifies the kind of application source the exporter should crawl.
/// </summary>
/// <remarks>
/// Numeric values are explicit because this public enum may be bound from configuration or serialized by host tooling.
/// New values should be appended without changing existing values.
/// </remarks>
public enum ExportSourceKind
{
    /// <summary>
    /// Crawl an already-running application URL.
    /// </summary>
    Url = 0,

    /// <summary>
    /// Publish a project and crawl the launched output.
    /// </summary>
    Project = 1,

    /// <summary>
    /// Launch a compiled application assembly and crawl it.
    /// </summary>
    Dll = 2
}

/// <summary>
/// Describes a validated export source and the launch options needed to make it crawlable.
/// </summary>
/// <param name="SourceKind">
/// The selected source kind. URL sources are crawled directly; project and DLL sources are launched by the resolver.
/// </param>
/// <param name="SourceValue">
/// The validated URL, project path, or DLL path. URL values are absolute HTTP(S) URLs normalized without a trailing
/// slash; project and DLL values are absolute file-system paths.
/// </param>
/// <param name="Framework">
/// Optional target framework for project exports. This is meaningful only when <paramref name="SourceKind"/> is
/// <see cref="ExportSourceKind.Project"/>.
/// </param>
/// <param name="AppArgs">
/// Application argument tokens forwarded when the exporter launches a project or DLL source. Each item is one process
/// argument; callers should not pre-join multiple arguments into a single string unless the target app expects that
/// literal token.
/// </param>
/// <param name="NoBuild">
/// Whether project exports skip publish before launch. This is meaningful only for project sources and is ignored by URL
/// and DLL sources.
/// </param>
/// <remarks>
/// Create instances through <see cref="ExportSourceRequestFactory"/> when processing CLI input so source selection,
/// source-specific options, and file existence checks remain consistent. Direct construction is intended for tests and
/// host integrations that already validated their inputs.
/// </remarks>
public sealed record ExportSourceRequest(
    ExportSourceKind SourceKind,
    string SourceValue,
    string? Framework,
    IReadOnlyList<string> AppArgs,
    bool NoBuild);
