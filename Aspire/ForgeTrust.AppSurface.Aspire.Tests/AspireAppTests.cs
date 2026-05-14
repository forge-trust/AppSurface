using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CliFx.Attributes;
using ForgeTrust.AppSurface.Aspire;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class AspireAppTests
{
    static AspireAppTests()
    {
        // Set dummy paths to satisfy Aspire orchestration requirements during tests
        // These are required when instantiating IDistributedApplicationBuilder
        Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", "dummy");
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_PATH", "dummy");
    }

    [Fact]
    public async Task RunAsync_RegistersAspireComponentsAsSingletons()
    {
        var completion = new TaskCompletionSource<ComponentResolutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        TestModule.Completion = completion;
        Environment.ExitCode = 0;

        try
        {
            var runTask = AspireApp<TestModule>.RunAsync(["validate"]);

            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.ComponentResolved);
            Assert.True(result.ResolvedAsSingleton);
            Assert.False(result.AbstractComponentRegistered);
            Assert.False(result.NonComponentRegistered);

            // The aspire application will not actually run
            Assert.Equal(-150, Environment.ExitCode);
        }
        finally
        {
            TestModule.Completion = null;
            Environment.ExitCode = 0;
        }
    }

    [Fact]
    public async Task RunAsync_WithoutModule_UsesCallingAssembly()
    {
        var previousExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;

            await AspireApp.RunAsync(["validate"]);

            // The aspire application will not actually run
            Assert.Equal(-150, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("not-a-command")]
    [InlineData("")]
    [InlineData("-f")]
    public async Task RunAsync_InvalidArgs_Throws(string arg)
    {
        var previousExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;

            await AspireApp.RunAsync([arg]);

            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }


    private sealed record ComponentResolutionResult(
        bool ComponentResolved,
        bool ResolvedAsSingleton,
        bool AbstractComponentRegistered,
        bool NonComponentRegistered);

    [Command("validate")]
    public sealed class ValidateComponentCommand : AspireProfile
    {
        private readonly IHostApplicationLifetime _lifetime;

        public ValidateComponentCommand(
            IServiceProvider provider,
            IHostApplicationLifetime lifetime,
            ILogger<ValidateComponentCommand> logger)
            : base(logger)
        {
            _lifetime = lifetime;
        }

        public override IEnumerable<IAspireComponent> GetComponents()
        {
            Console.WriteLine("Getting components...");
            _lifetime.StopApplication();

            return [];
        }
    }

    private sealed class TestModule : IAppSurfaceHostModule
    {
        public static TaskCompletionSource<ComponentResolutionResult>? Completion { get; set; }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            var completion = Completion ?? throw new InvalidOperationException("Completion source not initialized.");

            var descriptor = services.FirstOrDefault(sd => sd.ServiceType == typeof(TestComponent));
            var registered = descriptor is not null;
            var singleton = descriptor?.Lifetime == ServiceLifetime.Singleton;

            var abstractRegistered = services.Any(sd => sd.ServiceType == typeof(AbstractTestComponent));
            var nonComponentRegistered = services.Any(sd => sd.ServiceType == typeof(NonComponent));

            completion.TrySetResult(
                new ComponentResolutionResult(
                    registered,
                    singleton,
                    abstractRegistered,
                    nonComponentRegistered));
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }

    private sealed class TestComponent : IAspireComponent<IResource>
    {
        public IResourceBuilder<IResource> Generate(AspireStartupContext context, IDistributedApplicationBuilder appBuilder) =>
            throw new NotSupportedException("Generate is not expected to be called in this test.");
    }

    private abstract class AbstractTestComponent : IAspireComponent<IResource>
    {
        public abstract IResourceBuilder<IResource> Generate(
            AspireStartupContext context,
            IDistributedApplicationBuilder appBuilder);
    }

    private sealed class NonComponent
    {
    }
}
