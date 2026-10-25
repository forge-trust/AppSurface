using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Cli.Tests;

// The real command pipeline temporarily owns a process-wide service provider and exit code.
[Collection(ProgramEntryPointCollection.Name)]
public sealed class CloudRunExecutionAdmissionIntegrationTests
{
    [Theory]
    [MemberData(nameof(MalformedArguments))]
    public async Task RunAsync_RejectsMalformedObserveArgumentsWithoutEchoingRawInput(string[] args)
    {
        var console = new FakeInMemoryConsole();
        var original = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            await AppSurfaceCliApp.RunAsync(args, options => options.CustomRegistrations.Add(services => services.AddSingleton<IConsole>(console)));

            // protects=pre-CliFx safe command admission; fails_when=raw malformed token is echoed or generic parser runs; no_claim=provider invocation
            Assert.Equal(2, Environment.ExitCode);
            Assert.Contains("ASDEPLOY170", console.ReadErrorString());
            Assert.DoesNotContain("--bogus", console.ReadErrorString());
            Assert.DoesNotContain("--job", console.ReadErrorString());
        }
        finally { Environment.ExitCode = original; }
    }

    public static IEnumerable<object[]> MalformedArguments =>
    [
        [new[] { "deployment", "gcp-cloud-run", "observe-execution", "--bogus" }],
        [new[] { "deployment", "gcp-cloud-run", "observe-execution", "--job" }],
        [new[] { "deployment", "gcp-cloud-run", "observe-execution", "--job", "one", "--job", "two" }],
    ];

    [Fact]
    public async Task RunAsync_AllowsHelpThroughNormalCliFxHelpPath()
    {
        var console = new FakeInMemoryConsole();
        var original = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            await AppSurfaceCliApp.RunAsync(["deployment", "gcp-cloud-run", "observe-execution", "--help"], options => options.CustomRegistrations.Add(services => services.AddSingleton<IConsole>(console)));

            // protects=discoverable help; fails_when=help is treated as malformed input; no_claim=provider compatibility
            Assert.DoesNotContain("ASDEPLOY170", console.ReadErrorString());
        }
        finally { Environment.ExitCode = original; }
    }
}
