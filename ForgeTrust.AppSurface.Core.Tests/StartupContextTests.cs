using System.Reflection;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public class StartupContextTests
{
    [Fact]
    public void GetDependencies_ReturnsAddedModules()
    {
        var context = new StartupContext([], new DummyModule());
        context.Dependencies.AddModule<DummyModule>();

        var deps = context.GetDependencies();

        Assert.Single(deps);
        Assert.IsType<DummyModule>(deps[0]);
    }

    [Fact]
    public void ConsoleOutputMode_DefaultsToDefault()
    {
        var context = new StartupContext([], new DummyModule());

        Assert.Equal(ConsoleOutputMode.Default, context.ConsoleOutputMode);
    }

    [Fact]
    public void ConsoleOutputMode_CanBeConfigured()
    {
        var context = new StartupContext([], new DummyModule())
        {
            ConsoleOutputMode = ConsoleOutputMode.CommandFirst
        };

        Assert.Equal(ConsoleOutputMode.CommandFirst, context.ConsoleOutputMode);
    }

    [Fact]
    public void EnvironmentProvider_DefaultsToCommandLineAwareProvider()
    {
        var context = new StartupContext(["--environment", Environments.Development], new DummyModule());

        Assert.True(context.IsDevelopment);
        Assert.Equal(Environments.Development, context.EnvironmentProvider.Environment);
    }

    [Theory]
    [InlineData(ConsoleOutputMode.Default, 0)]
    [InlineData(ConsoleOutputMode.CommandFirst, 1)]
    public void ConsoleOutputMode_NumericValues_AreStable(ConsoleOutputMode value, int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Fact]
    public void ApplicationName_DefaultsToRootModuleAssemblyName()
    {
        var context = new StartupContext([], new DummyModule());

        Assert.Equal(typeof(DummyModule).Assembly.GetName().Name, context.ApplicationName);
    }

    [Fact]
    public void ApplicationName_CanBeProvidedThroughConstructor()
    {
        var context = new StartupContext([], new DummyModule(), "CustomApp");

        Assert.Equal("CustomApp", context.ApplicationName);
    }

    [Fact]
    public void ApplicationName_CanBeOverriddenWithWithExpression()
    {
        var context = new StartupContext([], new DummyModule());

        var renamed = context with { ApplicationName = "CustomApp" };

        Assert.Equal("CustomApp", renamed.ApplicationName);
        Assert.Equal(context.RootModuleAssembly, renamed.RootModuleAssembly);
        Assert.Equal(context.HostApplicationName, renamed.HostApplicationName);
    }

    [Fact]
    public void HostApplicationName_DefaultsToProcessEntryAssembly_WhenAvailable()
    {
        var context = new StartupContext([], new DummyModule());
        var expectedAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name
            ?? typeof(DummyModule).Assembly.GetName().Name;

        Assert.Equal(expectedAssemblyName, context.HostApplicationName);
    }

    [Fact]
    public void HostApplicationName_UsesOverrideEntryPointAssemblyName()
    {
        var context = new StartupContext([], new DummyModule())
        {
            OverrideEntryPointAssembly = typeof(string).Assembly
        };

        Assert.Equal(typeof(string).Assembly.GetName().Name, context.HostApplicationName);
    }

    [Fact]
    public void EntryPointAssembly_DefaultsToRootModuleAssembly()
    {
        var context = new StartupContext([], new DummyModule());

        Assert.Equal(typeof(DummyModule).Assembly, context.EntryPointAssembly);
    }

    [Fact]
    public void EntryPointAssembly_UsesOverrideEntryPointAssembly()
    {
        var context = new StartupContext([], new DummyModule())
        {
            OverrideEntryPointAssembly = typeof(string).Assembly
        };

        Assert.Equal(typeof(string).Assembly, context.EntryPointAssembly);
    }

    private class DummyModule : IAppSurfaceHostModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
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
}
