using FileSecretReferencesExample;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var startup = (IAppSurfaceStartup)new FileSecretReferencesStartup();
var context = new StartupContext([], new FileSecretReferencesModule());
using var host = startup.CreateHostBuilder(context).Build();
var hostStarted = false;
try
{
    await host.StartAsync();
    hostStarted = true;
    DemoOutput.Verify(
        host.Services.GetRequiredService<FileSecretReferencesConfig>(),
        host.Services.GetRequiredService<DemoGoogleSecretClient>());
}
catch (ConfigurationCompositionException exception) when (FileSecretReferencesModule.IsExpectedFailure(args, exception))
{
    Console.WriteLine($"EXPECTED FAILURE: {exception.Failures[0].Code} at {exception.Failures[0].Path}");
}
finally
{
    if (hostStarted) await host.StopAsync();
}

return 0;
