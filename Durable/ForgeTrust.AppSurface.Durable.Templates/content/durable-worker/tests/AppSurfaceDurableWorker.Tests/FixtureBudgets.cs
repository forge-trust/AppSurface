namespace DurableWorkerTemplate.Tests;

internal static class FixtureBudgets
{
    internal static readonly TimeSpan DockerProbe = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ImagePull = TimeSpan.FromSeconds(300);
    internal static readonly TimeSpan DatabaseSetup = TimeSpan.FromSeconds(40);
    internal static readonly TimeSpan HostStartup = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan NativeObservation = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan Lifecycle = TimeSpan.FromSeconds(25);
    internal static readonly TimeSpan ActivityFlush = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan Cleanup = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan ChildTermination = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan WorkPollInterval = TimeSpan.FromMilliseconds(100);
}
