namespace ForgeTrust.RazorWire.Cli;

internal sealed class ResolvedExportSource : IAsyncDisposable
{
    public string BaseUrl { get; }

    private readonly ITargetAppProcess? _ownedProcess;

    public ResolvedExportSource(string baseUrl, ITargetAppProcess? ownedProcess)
    {
        BaseUrl = baseUrl;
        _ownedProcess = ownedProcess;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownedProcess is not null)
        {
            await _ownedProcess.DisposeAsync();
        }
    }
}
