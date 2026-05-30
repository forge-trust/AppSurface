namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Represents a crawlable export source URL and any target process owned by the resolver.
/// </summary>
public sealed class ResolvedExportSource : IAsyncDisposable
{
    /// <summary>
    /// Gets the resolved base URL that the export engine should crawl.
    /// </summary>
    public string BaseUrl { get; }

    private readonly ITargetAppProcess? _ownedProcess;

    /// <summary>
    /// Initializes a new instance of <see cref="ResolvedExportSource"/>.
    /// </summary>
    /// <param name="baseUrl">The crawlable base URL.</param>
    /// <param name="ownedProcess">Optional process owned by this resolved source.</param>
    public ResolvedExportSource(string baseUrl, ITargetAppProcess? ownedProcess)
    {
        BaseUrl = baseUrl;
        _ownedProcess = ownedProcess;
    }

    /// <summary>
    /// Disposes any target application process launched while resolving the source.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_ownedProcess is not null)
        {
            await _ownedProcess.DisposeAsync();
        }
    }
}
