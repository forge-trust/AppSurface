using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Core.Tests;

[Collection("NoParallel")]
public class AppSurfaceStartupTests
{
    [Fact]
    public void CreateHostBuilder_RegistersServicesFromModules()
    {
        var context = new StartupContext([], new RootModule());
        var startup = new TestStartup();

        var hostBuilder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = hostBuilder.Build();

        using var scope = host.Services.CreateScope();
        var provider = scope.ServiceProvider;

        Assert.NotNull(provider.GetService<ServiceFromRoot>());
        Assert.NotNull(provider.GetService<ServiceFromDep>());
        Assert.NotNull(provider.GetService<ServiceFromApp>());
    }

    [Fact]
    public void CreateHostBuilder_KeepsCustomApplicationNameAsDisplayLabel()
    {
        var context = new StartupContext([], new RootModule(), "CustomApp");
        var startup = new TestStartup();
        var expectedHostApplicationName = Assembly.GetEntryAssembly()?.GetName().Name
            ?? typeof(RootModule).Assembly.GetName().Name;

        var hostBuilder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = hostBuilder.Build();

        var env = host.Services.GetRequiredService<IHostEnvironment>();
        var config = host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("CustomApp", context.ApplicationName);
        Assert.Equal(expectedHostApplicationName, context.HostApplicationName);
        Assert.Equal(context.HostApplicationName, env.ApplicationName);
        Assert.Equal(context.HostApplicationName, config[HostDefaults.ApplicationKey]);
    }

    [Fact]
    public void CreateHostBuilder_UsesOverrideEntryPointAssemblyForHostApplicationName()
    {
        var context = new StartupContext([], new RootModule(), "CustomApp")
        {
            OverrideEntryPointAssembly = typeof(string).Assembly
        };
        var startup = new TestStartup();

        var hostBuilder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = hostBuilder.Build();

        var env = host.Services.GetRequiredService<IHostEnvironment>();
        var config = host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("CustomApp", context.ApplicationName);
        Assert.Equal(typeof(string).Assembly.GetName().Name, context.HostApplicationName);
        Assert.Equal(context.HostApplicationName, env.ApplicationName);
        Assert.Equal(context.HostApplicationName, config[HostDefaults.ApplicationKey]);
    }

    [Fact]
    public void CreateHostBuilder_UsesArgsForConfiguration()
    {
        var context = new StartupContext(["--SomeKey", "SomeValue"], new RootModule());
        var startup = new TestStartup();

        var hostBuilder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = hostBuilder.Build();

        var config = host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("SomeValue", config["SomeKey"]);
    }

    [Fact]
    public void CreateHostBuilder_UsesPortArgForUrls()
    {
        var context = new StartupContext(["--port", "5005"], new RootModule());
        var startup = new TestStartup();

        var hostBuilder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = hostBuilder.Build();

        var config = host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("http://localhost:5005;http://*:5005", config["urls"]);
    }

    [Fact]
    public void CreateHostBuilder_UrlsAndPortProvided_PortWins()
    {
        var context = new StartupContext(["--urls", "http://localhost:5001", "--port", "5005"], new RootModule());
        var startup = new TestStartup();

        var hostBuilder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
        using var host = hostBuilder.Build();

        var config = host.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("http://localhost:5005;http://*:5005", config["urls"]);
    }

    [Fact]
    public async Task RunAsync_WithArgs_InvokesCreateRootModuleAndRunsHost()
    {
        var root = new RootModule();
        var startupCalled = 0;

        var startup = new TestStartupOverride(root, () => startupCalled++);

        var previous = Environment.ExitCode;
        await startup.RunAsync([]);

        Assert.Equal(1, startupCalled);
        Assert.Equal(previous, Environment.ExitCode);
        Assert.True(startup.HostStarted);
    }

    [Fact]
    public async Task RunAsync_OperationCanceledException_DoesNotChangeExitCode()
    {
        var root = new RootModuleThrows
        {
            ExceptionToThrow = new OperationCanceledException() // No exception, just to test cancellation
        };
        var startup = new ExceptionStartup(root);
        var previous = Environment.ExitCode;

        await startup.RunAsync([]);

        Assert.Equal(previous, Environment.ExitCode);
    }

    [Fact]
    public async Task RunAsync_GeneralException_SetsExitCode()
    {
        var root = new RootModuleThrows { ExceptionToThrow = new InvalidOperationException() };
        var startup = new ExceptionStartup(root);
        var previous = Environment.ExitCode;

        await startup.RunAsync([]);

        Assert.Equal(-100, Environment.ExitCode);

        Environment.ExitCode = previous;
    }

    [Fact]
    public void CreateHostBuilder_CallsConfigureHostMethods()
    {
        var root = new TrackingRootModule();
        var startup = new TrackingStartup();
        var context = new StartupContext([], root);

        ((IAppSurfaceStartup)startup).CreateHostBuilder(context);

        var dep = Assert.IsType<TrackingDepModule>(context.Dependencies.Modules.Single(m => m is TrackingDepModule));

        Assert.Equal(1, root.BeforeCalled);
        Assert.Equal(1, root.AfterCalled);
        Assert.Equal(1, dep.BeforeCalled);
        Assert.Equal(1, dep.AfterCalled);
    }

    private class ServiceFromRoot
    {
    }

    private class ServiceFromDep
    {
    }

    private class ServiceFromApp
    {
    }

    private class DepModule : IAppSurfaceModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services) =>
            services.AddSingleton<ServiceFromDep>();

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }

    private class RootModule : IAppSurfaceHostModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services) =>
            services.AddSingleton<ServiceFromRoot>();

        public void RegisterDependentModules(ModuleDependencyBuilder builder) => builder.AddModule<DepModule>();

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }

    private class TestStartup : AppSurfaceStartup<RootModule>
    {
        protected override void ConfigureServicesForAppType(StartupContext context, IServiceCollection services) =>
            services.AddSingleton<ServiceFromApp>();
    }

    // Hosted service used in RunAsync tests
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

    private class TestStartupOverride : AppSurfaceStartup<RootModule>
    {
        private readonly RootModule _module;
        private readonly Action _onCreate;

        public TestStartupOverride(RootModule module, Action onCreate)
        {
            _module = module;
            _onCreate = onCreate;
        }

        public bool HostStarted { get; private set; }

        protected override RootModule CreateRootModule()
        {
            _onCreate();

            return _module;
        }

        protected override void ConfigureServicesForAppType(StartupContext context, IServiceCollection services)
        {
            services.AddSingleton<IHostedService>(sp =>
                new CallbackHostedService(() => HostStarted = true, sp.GetRequiredService<IHostApplicationLifetime>()));
        }
    }

    private class RootModuleThrows : IAppSurfaceHostModule
    {
        public Exception ExceptionToThrow { get; set; } = new InvalidOperationException("Default test exception");

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            services.AddSingleton<IHostedService>(sp =>
                new CallbackHostedService(
                    () => throw ExceptionToThrow,
                    sp.GetRequiredService<IHostApplicationLifetime>()));
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }

    private class ExceptionStartup : AppSurfaceStartup<RootModuleThrows>
    {
        private readonly RootModuleThrows _module;

        public ExceptionStartup(RootModuleThrows module)
        {
            _module = module;
        }

        protected override RootModuleThrows CreateRootModule() => _module;

        protected override void ConfigureServicesForAppType(StartupContext context, IServiceCollection services)
        {
        }
    }

    private class TrackingDepModule : IAppSurfaceHostModule
    {
        public int AfterCalled;
        public int BeforeCalled;

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) => BeforeCalled++;
        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) => AfterCalled++;
    }

    private class TrackingRootModule : IAppSurfaceHostModule
    {
        public int AfterCalled;
        public int BeforeCalled;

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder) => builder.AddModule<TrackingDepModule>();
        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) => BeforeCalled++;
        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) => AfterCalled++;
    }

    private class TrackingStartup : AppSurfaceStartup<TrackingRootModule>
    {
        protected override void ConfigureServicesForAppType(StartupContext context, IServiceCollection services)
        {
        }
    }
}

[CollectionDefinition("NoParallel", DisableParallelization = true)]
public class NoParallelCollectionDefinition
{
}
