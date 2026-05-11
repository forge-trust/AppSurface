using Autofac;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Dependency.Autofac.Tests;

public class AppSurfaceAutofacModuleTests
{
    [Fact]
    public void AppSurfaceAutofacModule_SatisfiesAppSurfaceModuleWithoutMicrosoftDiRegistrations()
    {
        var module = new NoopAutofacModule();
        var services = new ServiceCollection();
        var context = new StartupContext([], new RootAutofacHostModule());

        module.ConfigureServices(context, services);
        module.RegisterDependentModules(new ModuleDependencyBuilder());

        Assert.IsAssignableFrom<IAppSurfaceModule>(module);
        Assert.Empty(services);
    }

    private sealed class NoopAutofacModule : AppSurfaceAutofacModule
    {
    }
}

public class AppSurfaceAutofacHostModuleTests
{
    [Fact]
    public void ConfigureHostBeforeServices_InstallsAutofacServiceProviderFactory()
    {
        var module = new RootAutofacHostModule();
        var context = new StartupContext([], module);
        var builder = Host.CreateDefaultBuilder();

        module.ConfigureHostBeforeServices(context, builder);
        builder.ConfigureContainer<ContainerBuilder>(container =>
            container.RegisterType<BeforeServicesAutofacService>().As<IBeforeServicesAutofacService>());

        using var host = builder.Build();

        Assert.IsType<BeforeServicesAutofacService>(
            host.Services.GetRequiredService<IBeforeServicesAutofacService>());
    }

    [Fact]
    public void ConfigureHostAfterServices_RegistersAutofacCompatibleDependentModulesFromStartupContext()
    {
        var rootModule = new RootAutofacHostModule();
        var context = new StartupContext([], rootModule);
        var startup = new TestStartup(rootModule);

        using var host = ((IAppSurfaceStartup)startup).CreateHostBuilder(context).Build();

        Assert.IsType<DependentAutofacService>(
            host.Services.GetRequiredService<IDependentAutofacService>());
        Assert.IsType<RootAutofacService>(
            host.Services.GetRequiredService<IRootAutofacService>());
    }
}

public class AppSurfaceAutofacExtensionsTests
{
    [Fact]
    public void RegisterImplementations_RegistersConcreteNonAbstractImplementationsFromInterfaceAssembly()
    {
        var builder = new ContainerBuilder();

        builder.RegisterImplementations<IScannedAutofacService>();

        using var container = builder.Build();

        var services = container.Resolve<IEnumerable<IScannedAutofacService>>().ToArray();

        Assert.Contains(services, service => service is FirstScannedAutofacService);
        Assert.Contains(services, service => service is SecondScannedAutofacService);
        Assert.DoesNotContain(services, service => service.GetType().IsAbstract);
        Assert.DoesNotContain(services, service => service.GetType() == typeof(UnrelatedAutofacService));
    }

    [Fact]
    public void RegisterImplementations_RequiresInterfaceType()
    {
        var builder = new ContainerBuilder();

        var exception = Assert.Throws<ArgumentException>(
            () => builder.RegisterImplementations<UnrelatedAutofacService>());

        Assert.Equal("TInterface", exception.ParamName);
    }
}

public sealed class TestStartup : AppSurfaceStartup<RootAutofacHostModule>
{
    private readonly RootAutofacHostModule _rootModule;

    public TestStartup(RootAutofacHostModule rootModule)
    {
        _rootModule = rootModule;
    }

    protected override RootAutofacHostModule CreateRootModule() => _rootModule;

    protected override void ConfigureServicesForAppType(StartupContext context, IServiceCollection services)
    {
    }
}

public sealed class RootAutofacHostModule : AppSurfaceAutofacHostModule
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<RootAutofacService>().As<IRootAutofacService>();
    }

    public override void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<DependentAutofacModule>();
        builder.AddModule<PlainAppSurfaceModule>();
    }
}

public sealed class DependentAutofacModule : AppSurfaceAutofacModule
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<DependentAutofacService>().As<IDependentAutofacService>();
    }
}

public sealed class PlainAppSurfaceModule : IAppSurfaceModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}

internal interface IBeforeServicesAutofacService
{
}

internal sealed class BeforeServicesAutofacService : IBeforeServicesAutofacService
{
}

internal interface IRootAutofacService
{
}

internal sealed class RootAutofacService : IRootAutofacService
{
}

internal interface IDependentAutofacService
{
}

internal sealed class DependentAutofacService : IDependentAutofacService
{
}

internal interface IScannedAutofacService
{
}

internal sealed class FirstScannedAutofacService : IScannedAutofacService
{
}

internal sealed class SecondScannedAutofacService : IScannedAutofacService
{
}

internal abstract class AbstractScannedAutofacService : IScannedAutofacService
{
}

internal sealed class UnrelatedAutofacService
{
}
