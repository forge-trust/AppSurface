using Xunit;

// Release CLI tests exercise process-wide Environment.ExitCode and the shared CliFx service-provider bridge.
// Keep this assembly serial so one invocation cannot observe another invocation's disposed in-memory console.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
