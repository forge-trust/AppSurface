using System;
using System.Threading;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Console.Tests;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[Collection(CommandServiceStateCollection.Name)]
public class ConsoleAppTests
{
    [Fact]
    public async Task RunAsync_WithCustomStartup_UsesStartupAndRunsHost()
    {
        TrackingStartup.Reset();
        TrackingModule.Reset();

        Environment.ExitCode = 0;
        await ConsoleApp<TrackingStartup, TrackingModule>.RunAsync([]);

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(1, TrackingStartup.InstancesCreated);
        Assert.True(TrackingStartup.HostStarted);
        Assert.Equal(1, TrackingModule.ConfigureServicesCalls);
        Assert.Equal(ConsoleOutputMode.Default, TrackingStartup.ContextOutputMode);
        Assert.Equal(ConsoleOutputMode.Default, TrackingModule.LastContextOutputMode);
    }

    [Fact]
    public async Task RunAsync_WithModuleOnly_UsesGenericStartup()
    {
        TrackingModule.Reset();

        Environment.ExitCode = 0;
        await ConsoleApp<TrackingModule>.RunAsync([]);

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(1, TrackingModule.ConfigureServicesCalls);
        Assert.Equal(ConsoleOutputMode.Default, TrackingModule.LastContextOutputMode);
    }

    [Fact]
    public async Task RunAsync_WithModuleOnly_OptionsConfigureStartupContext()
    {
        TrackingModule.Reset();

        Environment.ExitCode = 0;
        await ConsoleApp<TrackingModule>.RunAsync(
            [],
            options => { options.OutputMode = ConsoleOutputMode.CommandFirst; });

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(ConsoleOutputMode.CommandFirst, TrackingModule.LastContextOutputMode);
    }

    [Fact]
    public async Task RunAsync_WithCustomStartup_OptionsConfigureStartupContext()
    {
        TrackingStartup.Reset();
        TrackingModule.Reset();

        Environment.ExitCode = 0;
        await ConsoleApp<TrackingStartup, TrackingModule>.RunAsync(
            [],
            options => { options.OutputMode = ConsoleOutputMode.CommandFirst; });

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(ConsoleOutputMode.CommandFirst, TrackingStartup.ContextOutputMode);
        Assert.Equal(ConsoleOutputMode.CommandFirst, TrackingModule.LastContextOutputMode);
    }

    [Fact]
    public async Task WithOptions_CanReconfigureExistingStartupInstance()
    {
        TrackingStartup.Reset();
        TrackingModule.Reset();

        var startup = new TrackingStartup();

        Environment.ExitCode = 0;
        await startup.WithOptions(options => { options.OutputMode = ConsoleOutputMode.Default; }).RunAsync([]);

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(ConsoleOutputMode.Default, TrackingStartup.ContextOutputMode);
        Assert.Equal(ConsoleOutputMode.Default, TrackingModule.LastContextOutputMode);

        Environment.ExitCode = 0;
        await startup.WithOptions(options => { options.OutputMode = ConsoleOutputMode.CommandFirst; }).RunAsync([]);

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(ConsoleOutputMode.CommandFirst, TrackingStartup.ContextOutputMode);
        Assert.Equal(ConsoleOutputMode.CommandFirst, TrackingModule.LastContextOutputMode);
    }

    [Fact]
    public async Task RunAsync_ReusesBuiltOptions_OnSubsequentRuns_WithoutRebuilding()
    {
        TrackingStartup.Reset();
        TrackingModule.Reset();

        var startup = new TrackingStartup();
        var configureCalls = 0;

        startup.WithOptions(options =>
        {
            configureCalls++;
            options.OutputMode = ConsoleOutputMode.CommandFirst;
        });

        Environment.ExitCode = 0;
        await startup.RunAsync([]);

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(1, configureCalls);
        Assert.Equal(ConsoleOutputMode.CommandFirst, TrackingStartup.ContextOutputMode);
        Assert.Equal(ConsoleOutputMode.CommandFirst, TrackingModule.LastContextOutputMode);

        Environment.ExitCode = 0;
        await startup.RunAsync([]);

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(1, configureCalls);
        Assert.Equal(ConsoleOutputMode.CommandFirst, TrackingStartup.ContextOutputMode);
        Assert.Equal(ConsoleOutputMode.CommandFirst, TrackingModule.LastContextOutputMode);
    }

    private class TrackingStartup : ConsoleStartup<TrackingModule>
    {
        public static int InstancesCreated { get; private set; }
        public static bool HostStarted { get; private set; }
        public static ConsoleOutputMode? ContextOutputMode { get; private set; }

        public TrackingStartup()
        {
            InstancesCreated++;
        }

        protected override void ConfigureAdditionalServices(StartupContext context, IServiceCollection services)
        {
            ContextOutputMode = context.ConsoleOutputMode;
            services.AddSingleton<IHostedService>(sp =>
                new CallbackHostedService(() => HostStarted = true, sp.GetRequiredService<IHostApplicationLifetime>()));
        }

        public static void Reset()
        {
            InstancesCreated = 0;
            HostStarted = false;
            ContextOutputMode = null;
        }
    }

    private class TrackingModule : IAppSurfaceHostModule
    {
        public static int ConfigureServicesCalls { get; private set; }
        public static ConsoleOutputMode? LastContextOutputMode { get; private set; }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            ConfigureServicesCalls++;
            LastContextOutputMode = context.ConsoleOutputMode;
            services.AddSingleton<IHostedService>(sp =>
                new CallbackHostedService(() => { }, sp.GetRequiredService<IHostApplicationLifetime>()));
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
            builder.AddModule<SharedTestCommandsModule>();
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public static void Reset()
        {
            ConfigureServicesCalls = 0;
            LastContextOutputMode = null;
        }
    }

    private class CallbackHostedService : IHostedService
    {
        private readonly Action _callback;
        private readonly IHostApplicationLifetime _lifetime;

        public CallbackHostedService(Action callback, IHostApplicationLifetime lifetime)
        {
            _callback = callback;
            _lifetime = lifetime;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _callback();
            _lifetime.StopApplication();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
