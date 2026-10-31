using System.Text;
using System.Text.Json;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Cli.Tests;

// The real command pipeline temporarily owns a process-wide service provider and exit code.
[Collection(ProgramEntryPointCollection.Name)]
public sealed class CloudRunExecutionAdmissionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_AdmittedOptionsReachObserverAndDeliverSuccess(bool joined)
    {
        var root = TestPathUtils.PathUnder(Environment.CurrentDirectory, ".appsurface-joined=" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var console = new FakeInMemoryConsole();
        var observer = new SuccessfulObserver();
        var original = Environment.ExitCode;
        const string execution = "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1";
        try
        {
            foreach (var name in new[] { "deployment-intent.v1.json", "gcp-cloud-run-migration.tf.json", "gcp-cloud-run-migration.plan.json" })
                File.WriteAllText(TestPathUtils.PathUnder(root, name), "{}");
            string[] arguments = ["deployment", "gcp-cloud-run", "observe-execution"];
            foreach (var (name, value) in new[] { ("--bundle-dir", root), ("--job", "job"), ("--execution", execution), ("--timeout", "60s"), ("--poll-interval", "2s") })
                arguments = [.. arguments, .. (joined ? new[] { name + "=" + value } : new[] { name, value })];
            arguments = [.. arguments, "--json"];
            Environment.ExitCode = 23;

            await AppSurfaceCliApp.RunAsync(arguments, options => options.CustomRegistrations.Add(services =>
            {
                services.AddSingleton<IConsole>(console);
                services.AddSingleton<IGcpCloudRunExecutionObserver>(observer);
            }));

            // protects=joined and separate argv use the real safe command pipeline; fails_when=CliFx rejects
            // admitted options or binding changes their values; no_claim=live provider configuration proof.
            Assert.Equal(0, Environment.ExitCode);
            var request = Assert.IsType<CloudRunExecutionObservationRequest>(observer.Request);
            Assert.Equal("job", request.LogicalJobId);
            Assert.Equal(execution, request.ExecutionResourceName);
            Assert.Equal(TimeSpan.FromSeconds(2), request.PollInterval);
            Assert.InRange(request.Timeout, TimeSpan.Zero, TimeSpan.FromSeconds(60));
            using var output = JsonDocument.Parse(console.ReadOutputString());
            Assert.Equal("Succeeded", output.RootElement.GetProperty("state").GetString());
            Assert.Equal("ASDEPLOY183", output.RootElement.GetProperty("diagnosticCode").GetString());
            Assert.Empty(console.ReadErrorString());
        }
        finally
        {
            Environment.ExitCode = original;
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(MalformedArguments))]
    public async Task RunAsync_RejectsMalformedObserveArgumentsWithoutEchoingRawInput(string[] args)
    {
        using var console = new FakeInMemoryConsole();
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

    [Theory]
    [MemberData(nameof(MixedCaseMalformedArguments))]
    public async Task RunAsync_MixedCaseMalformedAdmissionUsesSafeJsonBeforeCliFx(string[] args)
    {
        using var console = new FakeInMemoryConsole();
        var original = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            await AppSurfaceCliApp.RunAsync(args, options => options.CustomRegistrations.Add(services => services.AddSingleton<IConsole>(console)));

            // protects=case-insensitive command recognition still uses safe raw-argument admission; fails_when=unknown option value reaches CliFx output; no_claim=provider invocation
            var stdout = console.ReadOutputString();
            Assert.Equal(2, Environment.ExitCode);
            Assert.Single(stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains("\"schemaVersion\":\"1.0\"", stdout);
            Assert.Contains("\"diagnosticCode\":\"ASDEPLOY170\"", stdout);
            Assert.DoesNotContain("SYNTHETIC_PRIVATE_SENTINEL", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC_PRIVATE_SENTINEL", console.ReadErrorString(), StringComparison.Ordinal);
        }
        finally { Environment.ExitCode = original; }
    }

    public static IEnumerable<object[]> MixedCaseMalformedArguments =>
    [
        [new[] { "Deployment", "gcp-cloud-run", "observe-execution", "--bogus=SYNTHETIC_PRIVATE_SENTINEL", "--json" }],
        [new[] { "deployment", "Gcp-Cloud-Run", "observe-execution", "--bogus=SYNTHETIC_PRIVATE_SENTINEL", "--json" }],
        [new[] { "deployment", "gcp-cloud-run", "Observe-Execution", "--bogus=SYNTHETIC_PRIVATE_SENTINEL", "--json" }],
        [new[] { "Deployment", "Gcp-Cloud-Run", "Observe-Execution", "--bogus=SYNTHETIC_PRIVATE_SENTINEL", "--json" }],
        [new[] { "deployment", "gcp-cloud-run", "observe-execution", "--bundle-dir=-SYNTHETIC_PRIVATE_SENTINEL", "--job=job", "--execution=projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "--json" }],
        [new[] { "deployment", "gcp-cloud-run", "observe-execution", "--bundle-dir", "-SYNTHETIC_PRIVATE_SENTINEL", "--job", "job", "--execution", "projects/sample-project/locations/us-central1/jobs/job-1/executions/execution-1", "--json" }],
    ];

    public static IEnumerable<object[]> MalformedArguments =>
    [
        [new[] { "deployment", "gcp-cloud-run", "observe-execution", "--bogus" }],
        [new[] { "deployment", "gcp-cloud-run", "observe-execution", "--job" }],
        [new[] { "deployment", "gcp-cloud-run", "observe-execution", "--job", "one", "--job", "two" }],
    ];

    [Fact]
    public async Task RunAsync_AllowsHelpThroughNormalCliFxHelpPath()
    {
        using var console = new FakeInMemoryConsole();
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

    [Fact]
    public async Task RunAsync_MalformedJsonAdmissionUsesConfiguredConsoleAndSafeEnvelope()
    {
        using var console = new FakeInMemoryConsole();
        var original = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            await AppSurfaceCliApp.RunAsync(
                ["deployment", "gcp-cloud-run", "observe-execution", "--json", "--json"],
                options => options.CustomRegistrations.Add(services => services.AddSingleton<IConsole>(console)));

            // protects=early malformed JSON admission uses the caller console and versioned safe output; fails_when=generic parsing or stale exit code occurs; no_claim=provider availability
            Assert.Equal(2, Environment.ExitCode);
            Assert.Contains("\"schemaVersion\":\"1.0\"", console.ReadOutputString());
            Assert.Contains("\"diagnosticCode\":\"ASDEPLOY170\"", console.ReadOutputString());
            Assert.DoesNotContain("--json", console.ReadOutputString());
            Assert.Empty(console.ReadErrorString());
        }
        finally { Environment.ExitCode = original; }
    }

    [Fact]
    public async Task RunAsync_AdmissionConsoleRegistrationFailureFallsBackToExitTwo()
    {
        var original = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 23;
            await AppSurfaceCliApp.RunAsync(
                ["deployment", "gcp-cloud-run", "observe-execution", "--bogus"],
                options => options.CustomRegistrations.Add(_ => throw new InvalidOperationException("registration detail")));

            // protects=nonfatal admission-console failures cannot escape or preserve a stale process exit code; fails_when=the registration exception escapes or exit is not 2; no_claim=diagnostic delivery
            Assert.Equal(2, Environment.ExitCode);
        }
        finally { Environment.ExitCode = original; }
    }

    [Fact]
    public async Task RunAsync_MalformedAdmissionWithoutRegisteredConsoleUsesSystemConsole()
    {
        var original = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            await AppSurfaceCliApp.RunAsync(["deployment", "gcp-cloud-run", "observe-execution", "--bogus"]);

            // protects=default-console fallback returns malformed-input status; fails_when=process status is stale or fallback escapes; no_claim=terminal stream capture
            Assert.Equal(2, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = original;
        }
    }

    [Fact]
    public async Task RunAsync_CancelledMalformedAdmissionDeliversCancellationEnvelope()
    {
        var original = Environment.ExitCode;
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        using var console = new FakeConsole(Stream.Null, output, error);
        try
        {
            Environment.ExitCode = 0;
            var cancellationToken = console.RegisterCancellationHandler();
            console.RequestCancellation(TimeSpan.Zero);
            Assert.True(SpinWait.SpinUntil(() => cancellationToken.IsCancellationRequested, TimeSpan.FromSeconds(5)));
            await AppSurfaceCliApp.RunAsync(
                ["deployment", "gcp-cloud-run", "observe-execution", "--bogus"],
                options => options.CustomRegistrations.Add(services => services.AddSingleton<IConsole>(console)));

            // protects=pre-CliFx cancellation is represented as cancellation instead of malformed input; fails_when=170 masks cancellation or process status differs; no_claim=remote execution cancellation
            Assert.Equal(130, Environment.ExitCode);
            Assert.Empty(output.ToArray());
            Assert.Contains("ASDEPLOY186", Encoding.UTF8.GetString(error.ToArray()), StringComparison.Ordinal);
        }
        finally { Environment.ExitCode = original; }
    }

    private sealed class SuccessfulObserver : IGcpCloudRunExecutionObserver
    {
        public CloudRunExecutionObservationRequest? Request { get; private set; }

        public Task<CloudRunExecutionObservation> ObserveAsync(CloudRunExecutionObservationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new CloudRunExecutionObservation(request.LogicalJobId, request.ExecutionResourceName,
                CloudRunExecutionState.Succeeded, true, DateTimeOffset.UnixEpoch, "ASDEPLOY183"));
        }
    }
}
