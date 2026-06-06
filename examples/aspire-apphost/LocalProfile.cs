using CliFx.Binding;
using ForgeTrust.AppSurface.Aspire;
using Microsoft.Extensions.Logging;

namespace AspireAppHostExample;

[Command("local", Description = "Run the local AppSurface Aspire example.")]
public sealed partial class LocalProfile : AspireProfile
{
    private readonly WebAppEnvironmentComponent _webApp;

    public LocalProfile(
        WebAppEnvironmentComponent webApp,
        ILogger<LocalProfile> logger) : base(logger)
    {
        _webApp = webApp;
    }

    public override IEnumerable<IAspireComponent> GetComponents()
    {
        yield return _webApp;
    }
}
