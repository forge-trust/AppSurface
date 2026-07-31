using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class AspireTestAssemblyEnvironment
{
    [ModuleInitializer]
    internal static void EnablePollingFileWatcher()
    {
        // Coverlet writes beneath AppHost test directories; polling prevents recursive native watcher callbacks.
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
    }
}
