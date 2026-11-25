using System.Runtime.CompilerServices;

namespace AspireAppHostExample.Tests;

/// <summary>Configures file observation for the sample's live AppHost test process.</summary>
/// <remarks>Polling preserves configuration reload while avoiding recursive native watcher callbacks during test and coverage output writes.</remarks>
internal static class AspireExampleTestEnvironment
{
    /// <summary>Enables polling before any test creates an AppHost or its configuration sources.</summary>
    [ModuleInitializer]
    internal static void EnablePollingFileWatcher()
    {
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
    }
}
