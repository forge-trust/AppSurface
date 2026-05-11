using FakeItEasy;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Caching.Tests;

public class AppSurfaceCachingModuleTests
{
    [Fact]
    public void ConfigureServices_RegistersMemoAndMemoryCache()
    {
        var services = new ServiceCollection();
        var rootModule = A.Fake<IAppSurfaceHostModule>();
        var context = new StartupContext([], rootModule);
        var module = new AppSurfaceCachingModule();

        module.ConfigureServices(context, services);

        Assert.Contains(services, d => d.ServiceType == typeof(IMemo));
        Assert.Contains(services, d => d.ServiceType == typeof(IMemoryCache));
    }

    [Fact]
    public void ConfigureServices_ResolvesMemoSuccessfully()
    {
        var services = new ServiceCollection();
        var rootModule = A.Fake<IAppSurfaceHostModule>();
        var context = new StartupContext([], rootModule);
        var module = new AppSurfaceCachingModule();

        module.ConfigureServices(context, services);

        var sp = services.BuildServiceProvider();
        var memo = sp.GetRequiredService<IMemo>();

        Assert.NotNull(memo);
        Assert.IsType<Memo>(memo);
    }

    [Fact]
    public void ConfigureServices_DoesNotReplacePriorRegistration()
    {
        var services = new ServiceCollection();
        var fakeMemo = A.Fake<IMemo>();
        services.AddSingleton(fakeMemo);

        var rootModule = A.Fake<IAppSurfaceHostModule>();
        var context = new StartupContext([], rootModule);
        var module = new AppSurfaceCachingModule();

        module.ConfigureServices(context, services);

        var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IMemo>();

        Assert.Same(fakeMemo, resolved);
    }

    [Fact]
    public void RegisterDependentModules_DoesNothing()
    {
        var module = new AppSurfaceCachingModule();
        var builder = new ModuleDependencyBuilder();

        module.RegisterDependentModules(builder);

        // No exception, no registrations
    }
}
