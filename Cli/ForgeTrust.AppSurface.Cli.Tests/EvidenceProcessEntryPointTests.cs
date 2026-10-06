using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(ProgramEntryPointCollection.Name)]
public sealed class EvidenceProcessEntryPointTests
{
    [Fact]
    public async Task OrdinaryHelpStillComposesServicesWithoutDiscoveringProtectedWorker()
    {
        using var console = new FakeInMemoryConsole();
        var calls = 0;
        var previousExit = Environment.ExitCode;
        try
        {
            await ProgramEntryPoint.RunAsync(["--help"], options =>
            {
                calls++;
                options.CustomRegistrations.Add(services => services.AddSingleton<IConsole>(console));
            });
            Assert.Equal(0, Environment.ExitCode);
            Assert.Equal(1, calls);
            Assert.Contains("coverage", console.ReadOutputString(), StringComparison.Ordinal);
            Assert.DoesNotContain("evidence worker", console.ReadOutputString(), StringComparison.Ordinal);
            Assert.DoesNotContain("evidence supervise", console.ReadOutputString(), StringComparison.Ordinal);
            Assert.Empty(console.ReadErrorString());
        }
        finally { Environment.ExitCode = previousExit; }
    }

    [Theory]
    [InlineData("worker", "--control")]
    [InlineData("supervise", "--request")]
    public async Task ReservedHelpRunsBeforeAllConfiguration(string role, string option)
    {
        using var console = new FakeInMemoryConsole();
        var calls = 0;
        var previousExit = Environment.ExitCode;
        using var scope = ProgramEntryPoint.PushConfigureOptionsOverrideForTests(_ => calls++);
        try
        {
            await ProgramEntryPoint.RunAsync(["evidence", role, "--help"], _ => calls++, console);
            Assert.Equal(0, Environment.ExitCode);
            Assert.Equal(0, calls);
            Assert.Contains($"appsurface evidence {role} {option}", console.ReadOutputString(), StringComparison.Ordinal);
            Assert.Empty(console.ReadErrorString());
        }
        finally { Environment.ExitCode = previousExit; }
    }

    [Theory]
    [InlineData("Evidence", "worker", "--control", "/run/canary")]
    [InlineData("evidence", "Worker", "--control", "/run/canary")]
    [InlineData("evidence", "worker", "--request", "/run/canary")]
    [InlineData("evidence", "supervise", "--control", "/run/canary")]
    public async Task InvalidReservedAttemptDoesNotFallThroughToOrdinaryCli(string first, string role, string option, string path)
    {
        using var console = new FakeInMemoryConsole();
        var calls = 0;
        var previousExit = Environment.ExitCode;
        using var scope = ProgramEntryPoint.PushConfigureOptionsOverrideForTests(_ => calls++);
        try
        {
            await ProgramEntryPoint.RunAsync([first, role, option, path], _ => calls++, console);
            Assert.Equal(1, Environment.ExitCode);
            Assert.Equal(0, calls);
            Assert.Contains("ASEVD402:", console.ReadErrorString(), StringComparison.Ordinal);
            Assert.DoesNotContain("canary", console.ReadErrorString(), StringComparison.Ordinal);
            Assert.Empty(console.ReadOutputString());
        }
        finally { Environment.ExitCode = previousExit; }
    }

    [Fact]
    public async Task MissingControlDoesNotInvokeConfiguration()
    {
        using var console = new FakeInMemoryConsole();
        var calls = 0;
        var previousExit = Environment.ExitCode;
        try
        {
            await ProgramEntryPoint.RunAsync(["evidence", "worker"], _ => calls++, console);
            Assert.Equal(1, Environment.ExitCode);
            Assert.Equal(0, calls);
            Assert.StartsWith("ASEVD402:", console.ReadErrorString(), StringComparison.Ordinal);
        }
        finally { Environment.ExitCode = previousExit; }
    }

    [Fact]
    public async Task SupervisorWithoutProtectedRequestRejectsBeforeConfigurationAndPublishesNoSummary()
    {
        using var console = new FakeInMemoryConsole();
        var calls = 0;
        var previousExit = Environment.ExitCode;
        // This is an actual rejection invocation, not a fake owner or successful native execution.
        // Unsupported/non-root hosts reject before I/O; root Linux rejects the absent request before
        // connecting to systemd or creating any account, workspace or worker.
        var request = $"/run/appsurface-evidence-missing-canary-{Guid.NewGuid():N}.json";
        Assert.False(File.Exists(request));
        using var scope = ProgramEntryPoint.PushConfigureOptionsOverrideForTests(_ => calls++);
        try
        {
            await ProgramEntryPoint.RunAsync(["evidence", "supervise", "--request", request], _ => calls++, console);
            Assert.Equal(1, Environment.ExitCode);
            Assert.Equal(0, calls);
            var error = console.ReadErrorString();
            Assert.True(error.StartsWith("ASEVD402:", StringComparison.Ordinal)
                || error.StartsWith("ASEVD410:", StringComparison.Ordinal));
            Assert.DoesNotContain("canary", error, StringComparison.Ordinal);
            Assert.DoesNotContain(request, error, StringComparison.Ordinal);
            Assert.DoesNotContain("not available in this build", error, StringComparison.Ordinal);
            Assert.Empty(console.ReadOutputString());
            Assert.False(File.Exists(request));
        }
        finally { Environment.ExitCode = previousExit; }
    }

    [Fact]
    public async Task MissingSupervisorRequestDoesNotInvokeConfiguration()
    {
        using var console = new FakeInMemoryConsole();
        var calls = 0;
        var previousExit = Environment.ExitCode;
        using var scope = ProgramEntryPoint.PushConfigureOptionsOverrideForTests(_ => calls++);
        try
        {
            await ProgramEntryPoint.RunAsync(["evidence", "supervise"], _ => calls++, console);
            Assert.Equal(1, Environment.ExitCode);
            Assert.Equal(0, calls);
            Assert.StartsWith("ASEVD402:", console.ReadErrorString(), StringComparison.Ordinal);
            Assert.Empty(console.ReadOutputString());
        }
        finally { Environment.ExitCode = previousExit; }
    }
}
