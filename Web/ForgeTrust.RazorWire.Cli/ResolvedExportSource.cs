namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Represents a crawlable export source URL and any target process owned by the resolver.
/// </summary>
public sealed class ResolvedExportSource : IAsyncDisposable
{
    /// <summary>
    /// Gets the resolved base URL that the export engine should crawl.
    /// </summary>
    /// <remarks>
    /// The value is produced by <see cref="ExportSourceResolver"/> and is expected to be an absolute HTTP(S) URL. It is
    /// stored exactly as provided except that blank values are rejected by the constructor.
    /// </remarks>
    public string BaseUrl { get; }

    private readonly ITargetAppProcess? _ownedProcess;

    /// <summary>
    /// Initializes a new instance of <see cref="ResolvedExportSource"/>.
    /// </summary>
    /// <param name="baseUrl">The non-blank crawlable base URL.</param>
    /// <param name="ownedProcess">
    /// Optional process owned by this resolved source. Pass <see langword="null"/> for already-running URL sources.
    /// </param>
    /// <remarks>
    /// Ownership is transferred to the resolved source. Callers should dispose the instance after export; direct URL
    /// sources do not own a process, while project and DLL sources usually do.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="baseUrl"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="baseUrl"/> is <see langword="null"/>.</exception>
    public ResolvedExportSource(string baseUrl, ITargetAppProcess? ownedProcess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        BaseUrl = baseUrl;
        _ownedProcess = ownedProcess;
    }

    /// <summary>
    /// Disposes any target application process launched while resolving the source.
    /// </summary>
    /// <remarks>
    /// Disposing a URL-backed source is a no-op because URL sources are owned by the caller. Disposing a launched source
    /// delegates to the owned process once so the temporary app host is stopped after crawling or after a failed export.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_ownedProcess is not null)
        {
            await _ownedProcess.DisposeAsync();
        }
    }
}
